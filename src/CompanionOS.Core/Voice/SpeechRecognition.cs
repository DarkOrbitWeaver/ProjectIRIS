using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using Whisper.net;

namespace CompanionOS.Core.Voice;

public enum SpeechRecognitionOutcome
{
    Recognized,
    NoSpeech,
    Unavailable,
    Failed,
    Cancelled
}

/// <summary>Recognized words or an explicit reason recognition produced no usable text.</summary>
public sealed record SpeechRecognitionResult(
    SpeechRecognitionOutcome Outcome,
    string Text = "",
    string FailureCode = "");

/// <summary>Anything that can turn recorded speech into text.</summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>"whisper", "parakeet" or "http", used in logs and diagnostics.</summary>
    string EngineName { get; }

    /// <summary>False when the model or server is missing.</summary>
    bool IsAvailable { get; }

    /// <summary>Transcribes 16 kHz mono float samples and distinguishes silence from runtime failure.</summary>
    Task<SpeechRecognitionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// Offline speech recognition through Whisper.net, which runs whisper.cpp locally on
/// the CPU. This is the default ear: no server, no network, no GPU cost.
///
/// The model file is downloaded once by scripts/setup.ps1. If it is missing the
/// recogniser reports itself unavailable instead of failing at the first sentence.
/// </summary>
public sealed class WhisperNetRecognizer : ISpeechRecognizer
{
    /// <summary>
    /// Read live, not frozen at construction: the model path, language and thread
    /// budget change in settings, and a stale ear must not outlive the decision
    /// (2026-10-27 audit — SettingsStore replaces Current wholesale, so a snapshot
    /// taken here kept the old values forever).
    /// </summary>
    private readonly Func<EarsSettings> _settings;
    private readonly object _factoryLock = new();
    private WhisperFactory? _factory;
    private string? _factoryModelPath;

    /// <summary>
    /// The dataset boilerplate Whisper invents for silence, noise and music. The old
    /// single-string filter ("[BLANK_AUDIO]") let song breaks turn into phantom user
    /// requests — "Thank you for watching" became a turn into her brain. Exact matches
    /// or ≥18-character prefixes count as NoSpeech; short real speech ("thank you" to
    /// her alone) deliberately does not match enough of these to be swallowed.
    /// </summary>
    private static readonly string[] HallucinationPhrases =
    [
        "[BLANK_AUDIO]",
        "[inaudible]",
        "[music]",
        "[applause]",
        "[silence]",
        "[placeholder]",
        "thank you for watching",
        "thanks for watching",
        "see you in the next video",
        "subscribe to my channel",
        "you are watching",
        "этой программой был", // common whisper dataset leak
        "начать разговор", // whisper Russian dataset leak
        "Ура! Вперёд"
    ];

    public WhisperNetRecognizer(Func<EarsSettings> settings)
    {
        _settings = settings;
    }

    private EarsSettings Options => _settings();

    /// <summary>
    /// How many cores transcription is allowed to touch.
    ///
    /// Why this matters more than it looks: whisper.cpp saturates every thread it is
    /// given, and a game that loses cores to a background transcription drops frames.
    /// Two threads is the default: responsive on the target mid-range CPU while leaving
    /// most of the machine for foreground applications and games. An explicit setting
    /// can raise this to four when the owner prefers speed over headroom.
    /// </summary>
    public int ThreadCount => ResolveThreadCount(Options.MaxRecognitionThreads);

    private static int ResolveThreadCount(int configuredThreads)
    {
        int cores = Math.Max(2, Environment.ProcessorCount);
        if (configuredThreads > 0)
        {
            return Math.Clamp(configuredThreads, 1, Math.Min(4, cores));
        }

        return Math.Clamp(2, 1, Math.Min(4, cores));
    }

    public string EngineName => "whisper";

    public bool IsAvailable => File.Exists(CompanionPaths.Model(Options.WhisperModelFile));

    public async Task<SpeechRecognitionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech);
        }

        if (!IsAvailable)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable, FailureCode: "model_missing");
        }

        try
        {
            WhisperFactory factory = GetOrCreateFactory();
            WhisperProcessorBuilder builder = factory.CreateBuilder()
                .WithThreads(ResolveThreadCount(Options.MaxRecognitionThreads));

            string language = Options.Language;
            if (!string.IsNullOrWhiteSpace(language) &&
                !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                builder = builder.WithLanguage(language);
            }

            using WhisperProcessor processor = builder.Build();

            StringBuilder transcript = new();

            // Best effort: ask the operating system to treat this work as less urgent
            // than whatever is in the foreground. The thread cap above is the real
            // guarantee; this only decides who wins when the two compete for a core.
            ThreadPriority previousPriority = ThreadPriority.Normal;
            bool priorityLowered = TryLowerPriority(ref previousPriority);

            try
            {
                await foreach (SegmentData segment in processor.ProcessAsync(samples, cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        transcript.Append(segment.Text.Trim()).Append(' ');
                    }
                }
            }
            finally
            {
                if (priorityLowered)
                {
                    RestorePriority(previousPriority);
                }
            }

            string text = transcript.ToString().Trim();
            // Whisper invents dataset boilerplate for silence and music. The one-string
            // filter ("[BLANK_AUDIO]") let everything else through as a fake user
            // request — "Thank you for watching" over a song literally became a turn
            // into her brain (2026-10-27 audit). The set is lowercase-compared trimmed
            // text; anything here is NoSpeech, never a request.
            string normalized = text.Trim().TrimEnd(' ', '.', ',', '!', '?');
            if (HallucinationPhrases.Any(phrase =>
                    normalized.Equals(phrase, StringComparison.OrdinalIgnoreCase) ||
                    (phrase.Length > 18 && normalized.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))))
            {
                return new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech);
            }

            return string.IsNullOrWhiteSpace(text)
                ? new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech)
                : new SpeechRecognitionResult(SpeechRecognitionOutcome.Recognized, text);
        }
        catch (OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Whisper could not transcribe ({exception.GetType().Name}).");
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                FailureCode: exception.GetType().Name);
        }
    }

    public void Dispose()
    {
        lock (_factoryLock)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }

    /// <summary>
    /// Drops the calling thread's priority for the duration of a transcription.
    /// Returns false when the runtime refuses to change it, which is not an error:
    /// thread-pool threads are allowed to say no, and the cap still applies.
    /// </summary>
    private static bool TryLowerPriority(ref ThreadPriority previousPriority)
    {
        try
        {
            Thread currentThread = Thread.CurrentThread;
            previousPriority = currentThread.Priority;
            if (previousPriority <= ThreadPriority.BelowNormal)
            {
                return false;
            }

            currentThread.Priority = ThreadPriority.BelowNormal;
            return true;
        }
        catch (Exception exception)
        {
            // ThreadStateException on a stopped thread, or a host that forbids it.
            CompanionLog.Trace("Voice", $"Could not lower the transcription thread priority: {exception.Message}");
            return false;
        }
    }

    private static void RestorePriority(ThreadPriority previousPriority)
    {
        try
        {
            Thread.CurrentThread.Priority = previousPriority;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Could not restore the thread priority: {exception.Message}");
        }
    }

    private WhisperFactory GetOrCreateFactory()
    {
        lock (_factoryLock)
        {
            // Loading the model takes a moment, so it is done once and kept. The path
            // is re-read live: a model-file change in settings rebuilds the factory
            // instead of transcribing with yesterday's weights.
            string modelPath = CompanionPaths.Model(Options.WhisperModelFile);
            if (_factory is null || !string.Equals(_factoryModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _factory?.Dispose();
                _factoryModelPath = modelPath;
                _factory = WhisperFactory.FromPath(modelPath);
            }

            return _factory;
        }
    }
}

/// <summary>
/// Speech recognition through a local OpenAI-compatible transcription server.
/// Useful when the user already runs whisper.cpp's server or a speech gateway,
/// and it keeps the desktop app free of native model files.
/// </summary>
public sealed class HttpSpeechRecognizer : ISpeechRecognizer
{
    /// <summary>Read live: a changed endpoint must not wait for a restart to count.</summary>
    private readonly Func<EarsSettings> _settings;
    private readonly HttpClient _httpClient;

    public HttpSpeechRecognizer(Func<EarsSettings> settings)
    {
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string EngineName => "http";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_settings().HttpEndpoint);

    public async Task<SpeechRecognitionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech);
        }

        if (!IsAvailable)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable,
                FailureCode: "endpoint_not_configured");
        }

        try
        {
            byte[] waveBytes = PcmWavWriter.ToWaveBytes(samples, 16000);
            EarsSettings options = _settings();

            using MultipartFormDataContent form = new();
            form.Add(new StringContent(options.HttpModel), "model");
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(options.Language) &&
                !options.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                form.Add(new StringContent(options.Language), "language");
            }

            ByteArrayContent audioContent = new(waveBytes);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "speech.wav");

            using HttpRequestMessage request = new(HttpMethod.Post,
                options.HttpEndpoint.TrimEnd('/') + "/audio/transcriptions");
            request.Content = form;
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                CompanionLog.Warning("Voice", $"Transcription server answered {(int)response.StatusCode}.");
                return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                    FailureCode: $"http_{(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("text", out JsonElement textElement))
            {
                return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                    FailureCode: "response_missing_text");
            }

            string text = (textElement.GetString() ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(text)
                ? new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech)
                : new SpeechRecognitionResult(SpeechRecognitionOutcome.Recognized, text);
        }
        catch (OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            // Exception messages can contain the configured endpoint, including credentials
            // embedded in its query string. The correlated event records only the error type.
            CompanionLog.Warning("Voice", $"Transcription request failed ({exception.GetType().Name}).");
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                FailureCode: exception.GetType().Name);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// Turns float samples into a standard 16-bit WAV file in memory.
/// Small enough to live here, and it removes a dependency on a WAV library.
/// </summary>
public static class PcmWavWriter
{
    public static byte[] ToWaveBytes(float[] samples, int sampleRate, int channelCount = 1)
    {
        int byteRate = sampleRate * channelCount * 2;
        int dataLength = samples.Length * 2;

        using MemoryStream stream = new(44 + dataLength);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)(channelCount * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (float sample in samples)
        {
            short value = (short)Math.Clamp((int)(sample * 32767f), short.MinValue, short.MaxValue);
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }
}

/// <summary>
/// The recogniser she has while her ears are deliberately shut.
///
/// It exists so the rest of the system never has to ask "are the ears off?" before
/// calling Transcribe: it simply reports itself unavailable and returns nothing. That
/// keeps every caller honest and keeps a null check out of the audio path.
/// </summary>
public sealed class SuspendedRecognizer : ISpeechRecognizer
{
    public string EngineName => "suspended";

    public bool IsAvailable => false;

    public Task<SpeechRecognitionResult> TranscribeAsync(float[] samples, CancellationToken cancellationToken) =>
        Task.FromResult(new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable,
            FailureCode: "ears_suspended"));

    public void Dispose()
    {
    }
}
