using CompanionOS.Core.Diagnostics;
using NAudio.Wave;
using System.Buffers.Binary;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Plays her voice and measures how loud it is right now.
///
/// The amplitude is what makes the orb breathe with her speech, so it is computed
/// from a 50 ms volume envelope of the actual audio rather than guessed.
///
/// Every method is safe to call from any thread, and the player never throws into
/// a turn: if audio hardware disappears mid-sentence she simply stops talking.
/// </summary>
public sealed class SpeechAudioPlayer : IDisposable
{
    private const int EnvelopeWindowMilliseconds = 50;

    private readonly object _playbackLock = new();
    private WaveOutEvent? _outputDevice;
    private WaveStream? _reader;
    private BufferedWaveProvider? _bufferedProvider;
    private MemoryStream? _audioStream;
    private bool _disposed;
    private long _playbackGeneration;

    /// <summary>The NAudio output device slot, &#45;1 for the system default. Applied on the next play.</summary>
    public int DeviceNumber { get; set; } = -1;

    /// <summary>Raised every 50 ms while audio plays, with a 0.0 to 1.0 level.</summary>
    public event Action<double>? AmplitudeChanged;

    /// <summary>True while audio is actually audible.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Checks a saved output slot without opening it or making sound.</summary>
    public static bool IsDeviceAvailable(int deviceNumber) =>
        deviceNumber < 0 || deviceNumber < WaveOut.DeviceCount;

    /// <summary>
    /// Plays one clip and completes when it ends, is stopped, or is cancelled. The return
    /// value says whether Windows accepted the selected output device; it cannot claim that a
    /// human heard it (headphones may be muted or disconnected downstream).
    /// </summary>
    public async Task<bool> PlayAsync(SpeechAudio audio, double volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Bytes.Length == 0)
        {
            return false;
        }

        float[] envelope = Array.Empty<float>();
        long generation;
        WaveOutEvent output;
        WaveStream reader;

        lock (_playbackLock)
        {
            StopInternal();
            generation = ++_playbackGeneration;

            try
            {
                // First pass: decode to compute the envelope, over its own stream.
                // Why a separate stream: NAudio readers dispose the stream they wrap when
                // they are disposed, so if the analysis reader shared the playback stream
                // below, closing it would leave a dead stream under the player.
                using MemoryStream analysisStream = new(audio.Bytes);
                using WaveStream analysisReader = CreateReader(audio, analysisStream);
                {
                    envelope = BuildEnvelope(analysisReader);
                }

                _audioStream = new MemoryStream(audio.Bytes);
                _reader = CreateReader(audio, _audioStream);
                _outputDevice = DeviceNumber >= 0
                    ? new WaveOutEvent { DeviceNumber = DeviceNumber, Volume = (float)Math.Clamp(volume, 0, 1) }
                    : new WaveOutEvent { Volume = (float)Math.Clamp(volume, 0, 1) };
                _outputDevice.Init(_reader);
                _outputDevice.Play();
                IsPlaying = true;
                output = _outputDevice;
                reader = _reader;
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Voice", $"Playback could not start: {exception.Message}");
                StopInternal();
                return false;
            }
        }

        try
        {
            await WaitForPlaybackAsync(output, reader, envelope, generation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_playbackLock)
            {
                // A newer clip may have replaced this one while it was waiting. An old
                // finally block must never tear down the new clip.
                if (generation == _playbackGeneration)
                {
                    StopInternal();
                }
            }

            AmplitudeChanged?.Invoke(0);
        }

        return true;
    }

    /// <summary>Starts PCM WAV playback after a short prebuffer while the rest downloads/generates.</summary>
    public async Task<StreamingPlaybackResult> PlayStreamingWavAsync(Stream wavStream, double volume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wavStream);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long generation;
        WaveOutEvent output;
        BufferedWaveProvider provider;

        byte[] header = new byte[44];
        await wavStream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            !header.AsSpan(12, 4).SequenceEqual("fmt "u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)) != 16 ||
            !header.AsSpan(36, 4).SequenceEqual("data"u8))
        {
            throw new InvalidDataException("The speech stream is not a 16-bit PCM WAV.");
        }

        int channels = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2));
        int sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));
        if (channels is < 1 or > 2 || sampleRate is < 8000 or > 96000)
        {
            throw new InvalidDataException("The speech stream has an unsupported WAV format.");
        }

        WaveFormat format = new(sampleRate, 16, channels);
        provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };

        long pcmBytes = 0;
        byte[] chunk = new byte[8192];
        List<float> amplitudeEnvelope = new();
        double windowEnergy = 0;
        int windowSamples = 0;
        bool hasCarryByte = false;
        byte carryByte = 0;
        int samplesPerWindow = Math.Max(1, sampleRate * channels / 20);

        void TrackAmplitude(int length)
        {
            int index = 0;
            if (hasCarryByte && length > 0)
            {
                Span<byte> firstSample = stackalloc byte[2] { carryByte, chunk[0] };
                double sample = BinaryPrimitives.ReadInt16LittleEndian(firstSample) / 32768.0;
                windowEnergy += sample * sample;
                windowSamples++;
                index = 1;
                hasCarryByte = false;
            }

            for (; index + 1 < length; index += 2)
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(index, 2)) / 32768.0;
                windowEnergy += sample * sample;
                windowSamples++;
                if (windowSamples >= samplesPerWindow)
                {
                    amplitudeEnvelope.Add((float)Math.Sqrt(windowEnergy / windowSamples));
                    windowEnergy = 0;
                    windowSamples = 0;
                }
            }

            if (index < length)
            {
                carryByte = chunk[index];
                hasCarryByte = true;
            }
        }

        int prebufferBytes = Math.Max(4096, format.AverageBytesPerSecond / 5);
        bool streamFinished = false;
        while (provider.BufferedBytes < prebufferBytes && !streamFinished)
        {
            int read = await wavStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                streamFinished = true;
                break;
            }

            provider.AddSamples(chunk, 0, read);
            TrackAmplitude(read);
            pcmBytes += read;
        }

        if (pcmBytes == 0)
        {
            return new StreamingPlaybackResult(false, 0, 0, 0);
        }

        lock (_playbackLock)
        {
            StopInternal();
            generation = ++_playbackGeneration;
            output = DeviceNumber >= 0
                ? new WaveOutEvent { DeviceNumber = DeviceNumber, Volume = (float)Math.Clamp(volume, 0, 1) }
                : new WaveOutEvent { Volume = (float)Math.Clamp(volume, 0, 1) };
            output.Init(provider);
            _outputDevice = output;
            _bufferedProvider = provider;
            output.Play();
            IsPlaying = true;
        }

        long firstAudioTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            int maximumBufferedBytes = format.AverageBytesPerSecond * 4;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_playbackLock)
                {
                    if (generation != _playbackGeneration)
                    {
                        return new StreamingPlaybackResult(false, pcmBytes,
                            System.Diagnostics.Stopwatch.GetElapsedTime(started, firstAudioTimestamp).TotalMilliseconds,
                            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }

                if (!streamFinished && provider.BufferedBytes < maximumBufferedBytes)
                {
                    int read = await wavStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        streamFinished = true;
                    }
                    else
                    {
                        provider.AddSamples(chunk, 0, read);
                        TrackAmplitude(read);
                        pcmBytes += read;
                    }
                }

                long playedBytes = Math.Max(0, pcmBytes - provider.BufferedBytes);
                int amplitudeIndex = (int)(playedBytes / Math.Max(1, format.AverageBytesPerSecond / 20));
                if (amplitudeIndex >= 0 && amplitudeIndex < amplitudeEnvelope.Count)
                {
                    AmplitudeChanged?.Invoke(Math.Clamp(amplitudeEnvelope[amplitudeIndex] * 5f, 0f, 1f));
                }

                if (streamFinished && provider.BufferedBytes == 0)
                {
                    break;
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            return new StreamingPlaybackResult(true, pcmBytes,
                System.Diagnostics.Stopwatch.GetElapsedTime(started, firstAudioTimestamp).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            lock (_playbackLock)
            {
                if (generation == _playbackGeneration)
                {
                    StopInternal();
                }
            }

            AmplitudeChanged?.Invoke(0);
        }
    }

    /// <summary>Stops immediately. Used for barge-in, where speed matters more than polish.</summary>
    public void Stop()
    {
        lock (_playbackLock)
        {
            _playbackGeneration++;
            StopInternal();
        }

        AmplitudeChanged?.Invoke(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_playbackLock)
        {
            StopInternal();
        }
    }

    private async Task WaitForPlaybackAsync(WaveOutEvent output, WaveStream reader,
        float[] envelope, long generation, CancellationToken cancellationToken)
    {
        int envelopeIndex = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_playbackLock)
            {
                if (generation != _playbackGeneration)
                {
                    return;
                }
            }

            if (output.PlaybackState == PlaybackState.Stopped)
            {
                return;
            }

            if (envelope.Length > 0)
            {
                double positionMilliseconds = reader.CurrentTime.TotalMilliseconds;
                envelopeIndex = Math.Clamp(
                    (int)(positionMilliseconds / EnvelopeWindowMilliseconds),
                    0,
                    envelope.Length - 1);
                AmplitudeChanged?.Invoke(envelope[envelopeIndex]);
            }

            try
            {
                await Task.Delay(EnvelopeWindowMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static WaveStream CreateReader(SpeechAudio audio, MemoryStream stream) =>
        audio.Format == "mp3"
            ? new Mp3FileReader(stream)
            : new WaveFileReader(stream);

    /// <summary>Reads the clip once and reduces it to a 50 ms loudness curve.</summary>
    private static float[] BuildEnvelope(WaveStream reader)
    {
        try
        {
            ISampleProvider samples = reader.ToSampleProvider();
            int sampleRate = Math.Max(samples.WaveFormat.SampleRate, 8000);
            int channels = Math.Max(samples.WaveFormat.Channels, 1);
            int samplesPerWindow = Math.Max(1, sampleRate / 1000 * EnvelopeWindowMilliseconds);

            List<float> envelope = new();
            float[] buffer = new float[8192];
            double windowEnergy = 0;
            int windowSamples = 0;

            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int index = 0; index < read; index++)
                {
                    windowEnergy += buffer[index] * buffer[index];
                    windowSamples++;

                    if (windowSamples < samplesPerWindow * channels)
                    {
                        continue;
                    }

                    envelope.Add((float)Math.Sqrt(windowEnergy / windowSamples));
                    windowEnergy = 0;
                    windowSamples = 0;
                }
            }

            if (envelope.Count == 0)
            {
                return Array.Empty<float>();
            }

            // Normalize so the orb uses its full range regardless of clip loudness.
            float peak = envelope.Max();
            if (peak <= 0.0001f)
            {
                return envelope.ToArray();
            }

            for (int index = 0; index < envelope.Count; index++)
            {
                envelope[index] = Math.Clamp(envelope[index] / peak, 0f, 1f);
            }

            return envelope.ToArray();
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Envelope could not be computed: {exception.Message}");
            return Array.Empty<float>();
        }
    }

    private void StopInternal()
    {
        try
        {
            _outputDevice?.Stop();
        }
        catch
        {
            // Losing the audio device must not throw.
        }

        try
        {
            _outputDevice?.Dispose();
        }
        catch
        {
            // Same here.
        }

        _outputDevice = null;

        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // Same here.
        }

        _reader = null;
        _bufferedProvider = null;

        try
        {
            _audioStream?.Dispose();
        }
        catch
        {
            // Same here.
        }

        _audioStream = null;
        IsPlaying = false;
    }
}

public sealed record StreamingPlaybackResult(bool Accepted, long PcmBytes,
    double FirstAudioMilliseconds, double TotalMilliseconds);
