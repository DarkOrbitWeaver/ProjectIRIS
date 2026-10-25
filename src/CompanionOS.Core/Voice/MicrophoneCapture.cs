using CompanionOS.Core.Diagnostics;
using NAudio.Wave;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Her ears, at the hardware level: opens the microphone and hands out 16 kHz mono
/// float samples plus a loudness reading for the listening ring.
///
/// 16 kHz mono is deliberate. It is what Whisper and every small speech model expect,
/// it halves the CPU cost of resampling, and it is plenty for speech.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly object _captureLock = new();
    private WaveInEvent? _waveInput;
    private bool _disposed;

    /// <summary>Raised with float samples in the range -1 to 1.</summary>
    public event Action<float[]>? SamplesAvailable;

    /// <summary>Raised with microphone loudness, 0.0 to 1.0.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Raised when the Windows capture handle actually opens or closes. This
    /// is distinct from a user asking to listen: a disconnected USB microphone can
    /// close underneath us and the UI must not continue claiming it is open.</summary>
    public event Action<bool>? CaptureStateChanged;

    /// <summary>True while the microphone is open.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Names of every recording device, for the settings picker.</summary>
    public static IReadOnlyList<string> ListInputDevices()
    {
        List<string> names = new();
        try
        {
            for (int deviceIndex = 0; deviceIndex < WaveInEvent.DeviceCount; deviceIndex++)
            {
                WaveInCapabilities capabilities = WaveInEvent.GetCapabilities(deviceIndex);
                names.Add(capabilities.ProductName);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Could not list microphones: {exception.Message}");
        }

        return names;
    }

    /// <summary>
    /// Opens the microphone. Device number -1 means the system default. Returns false
    /// when Windows has no recording device (or it cannot be opened), so an unplugged
    /// headset is a recoverable state instead of an application failure.
    /// </summary>
    public bool Start(int deviceNumber)
    {
        lock (_captureLock)
        {
            if (_disposed || IsCapturing)
            {
                return IsCapturing;
            }

            try
            {
                int deviceCount = WaveInEvent.DeviceCount;
                if (deviceCount <= 0)
                {
                    CompanionLog.Info("Voice", "No microphone is attached; ears remain closed.");
                    return false;
                }

                // A stale explicit device must not silently fall back to device zero:
                // after a USB headset is unplugged that may be the wrong microphone.
                if (deviceNumber >= deviceCount)
                {
                    CompanionLog.Warning("Voice", $"Microphone device {deviceNumber} is no longer available.");
                    return false;
                }

                // NAudio/WinMM reserves -1 for the Microsoft Audio Mapper. Mapping
                // "Default microphone" to index 0 silently selects the first device,
                // which may not be Windows' actual default input.
                int resolvedDevice = deviceNumber < 0 ? -1 : deviceNumber;

                _waveInput = new WaveInEvent
                {
                    DeviceNumber = resolvedDevice,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 30,
                    NumberOfBuffers = 3
                };

                _waveInput.DataAvailable += OnDataAvailable;
                _waveInput.RecordingStopped += OnRecordingStopped;
                _waveInput.StartRecording();
                IsCapturing = true;
                CompanionLog.Info("Voice", "Microphone opened.");
                CaptureStateChanged?.Invoke(true);
                return true;
            }
            catch (Exception exception)
            {
                CompanionLog.Error("Voice", "Microphone could not be opened.", exception);
                CleanUpInput();
                return false;
            }
        }
    }

    /// <summary>Closes the microphone and releases the device.</summary>
    public void Stop()
    {
        lock (_captureLock)
        {
            CleanUpInput();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded == 0)
        {
            return;
        }

        int sampleCount = eventArgs.BytesRecorded / 2;
        float[] samples = new float[sampleCount];
        double energy = 0;

        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            short rawSample = BitConverter.ToInt16(eventArgs.Buffer, sampleIndex * 2);
            float sample = rawSample / 32768f;
            samples[sampleIndex] = sample;
            energy += sample * sample;
        }

        double level = Math.Sqrt(energy / sampleCount);

        try
        {
            LevelChanged?.Invoke(Math.Clamp(level * 4.0, 0, 1));
            SamplesAvailable?.Invoke(samples);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Microphone subscriber failed: {exception.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null)
        {
            CompanionLog.Warning("Voice", $"Recording stopped unexpectedly: {eventArgs.Exception.Message}");
        }

        // An unplugged USB device reaches this callback without going through Stop.
        // Release its native handle here; otherwise the next Start overwrites a stale
        // WaveInEvent and Windows may keep the old device locked indefinitely.
        WaveInEvent? stoppedInput = sender as WaveInEvent;
        bool wasCapturing;
        lock (_captureLock)
        {
            if (stoppedInput is null || !ReferenceEquals(_waveInput, stoppedInput))
            {
                return;
            }

            _waveInput = null;
            wasCapturing = IsCapturing;
            IsCapturing = false;
            stoppedInput.DataAvailable -= OnDataAvailable;
            stoppedInput.RecordingStopped -= OnRecordingStopped;
        }

        try
        {
            stoppedInput.Dispose();
        }
        catch
        {
            // A lost device can already be disposed by its driver.
        }

        if (wasCapturing)
        {
            CaptureStateChanged?.Invoke(false);
        }
    }

    private void CleanUpInput()
    {
        WaveInEvent? input = _waveInput;
        _waveInput = null;
        bool wasCapturing = IsCapturing;
        IsCapturing = false;

        if (input is null)
        {
            return;
        }

        try
        {
            input.DataAvailable -= OnDataAvailable;
            input.RecordingStopped -= OnRecordingStopped;
            input.StopRecording();
        }
        catch
        {
            // The device may already be gone.
        }

        try
        {
            input.Dispose();
        }
        catch
        {
            // Same here.
        }

        if (wasCapturing)
        {
            CaptureStateChanged?.Invoke(false);
        }
    }
}
