using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using SherpaOnnx;

namespace CompanionOS.Core.Voice;

/// <summary>CPU transcription using a local Sherpa-ONNX NeMo Parakeet TDT bundle.</summary>
public sealed class ParakeetOnnxRecognizer : ISpeechRecognizer
{
    private readonly string? _modelDirectory;
    private readonly int _threadCount;
    private readonly object _sessionLock = new();
    private OfflineRecognizer? _recognizer;

    public ParakeetOnnxRecognizer(EarsSettings ears)
    {
        _modelDirectory = ResolveModelDirectory(ears.ParakeetModelFile);
        int cores = Math.Max(2, Environment.ProcessorCount);
        _threadCount = ears.MaxRecognitionThreads > 0
            ? Math.Clamp(ears.MaxRecognitionThreads, 1, Math.Min(4, cores))
            : 2;
    }

    public string EngineName => "parakeet";

    public bool IsAvailable => _modelDirectory is not null;

    public async Task<SpeechRecognitionResult> TranscribeAsync(
        float[] samples,
        CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech);
        }

        if (!IsAvailable)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Unavailable,
                FailureCode: "model_missing");
        }

        try
        {
            return await Task.Run(() => RunTranscription(samples, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice",
                $"Parakeet ONNX could not transcribe ({exception.GetType().Name}): {exception.Message}");
            return new SpeechRecognitionResult(SpeechRecognitionOutcome.Failed,
                FailureCode: exception.GetType().Name);
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private SpeechRecognitionResult RunTranscription(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sessionLock)
        {
            _recognizer ??= CreateRecognizer();
            using OfflineStream stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            cancellationToken.ThrowIfCancellationRequested();
            _recognizer.Decode(stream);
            string text = stream.Result.Text.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? new SpeechRecognitionResult(SpeechRecognitionOutcome.NoSpeech)
                : new SpeechRecognitionResult(SpeechRecognitionOutcome.Recognized, text);
        }
    }

    private OfflineRecognizer CreateRecognizer()
    {
        string directory = _modelDirectory
            ?? throw new FileNotFoundException("The Parakeet ONNX model directory is missing.");

        OfflineRecognizerConfig config = new();
        config.ModelConfig.Transducer.Encoder = Path.Combine(directory, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(directory, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(directory, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = _threadCount;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var recognizer = new OfflineRecognizer(config);
        CompanionLog.Info("Voice",
            $"Parakeet ONNX ears loaded from {directory} on CPU ({_threadCount} threads).");
        return recognizer;
    }

    private static string? ResolveModelDirectory(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        string path = CompanionPaths.Model(configuredPath);
        if (!Directory.Exists(path))
        {
            return null;
        }

        string[] requiredFiles = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];
        return requiredFiles.All(file => File.Exists(Path.Combine(path, file))) ? path : null;
    }
}
