using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>One voice she could sound like, offered to the user exactly once.</summary>
public sealed class VoiceCandidate
{
    /// <summary>Speaker name handed to the engine. Becomes VoiceProfile.VoiceId.</summary>
    public required string VoiceId { get; init; }

    /// <summary>What the picker shows, e.g. "Heart · warm, American English".</summary>
    public required string Label { get; init; }

    /// <summary>"http" for the neural engine — the only engine she has.</summary>
    public required string Engine { get; init; }

    /// <summary>One line of context, never a promise about quality.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Builds the shortlist for the voice picker and renders real previews of it.
///
/// The rule this class exists to enforce: a voice is chosen by *hearing it*, not by
/// reading a description. Every candidate here can be synthesised through the same
/// engine that will later speak her answers, so what the user approves is what they get.
///
/// A candidate that the machine cannot actually render (a Kokoro speaker on a server
/// that does not have it, a Windows voice that is not installed) reports failure when
/// previewed and is never offered, rather than being silently substituted later.
/// </summary>
public sealed class VoiceGallery : IDisposable
{
    private readonly CompanionSettings _settings;
    private readonly VoicePipeline? _pipeline;

    private ISpeechSynthesizer? _httpSynthesizer;
    private bool _isDisposed;

    public VoiceGallery(CompanionSettings settings, VoicePipeline? pipeline = null)
    {
        _settings = settings;
        _pipeline = pipeline;
    }

    /// <summary>
    /// The candidates worth showing right now. Cheap: no audio is rendered and no
    /// server is contacted, so the picker can be opened instantly.
    /// </summary>
    public IReadOnlyList<VoiceCandidate> DescribeCandidates()
    {
        List<VoiceCandidate> candidates = new();
        string engine = (_settings.Voice.Engine ?? "auto").Trim().ToLowerInvariant();

        if (engine == "pocket")
        {
            foreach (string voiceId in _settings.Voice.CandidateVoices
                         .Where(voiceId => !voiceId.Equals("alba", StringComparison.OrdinalIgnoreCase) &&
                                           !voiceId.Equals("estelle", StringComparison.OrdinalIgnoreCase) &&
                                           !voiceId.Equals("eve", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(new VoiceCandidate
                {
                    VoiceId = voiceId,
                    Label = DescribePocketVoice(voiceId),
                    Engine = "pocket",
                    Detail = "English neural voice · local CPU"
                });
            }

            return candidates;
        }

        if (engine is "http" or "kokoro")
        {
            foreach (string voiceId in _settings.Voice.CandidateVoices.Distinct())
            {
                candidates.Add(new VoiceCandidate
                {
                    VoiceId = voiceId,
                    Label = DescribeNeuralVoice(voiceId),
                    Engine = engine,
                    Detail = engine == "kokoro"
                        ? "Kokoro neural voice, rendered locally."
                        : "Neural voice, rendered locally."
                });
            }
        }

        return candidates;
    }

    /// <summary>
    /// Renders one candidate speaking the configured preview sentence.
    /// Returns null when this machine cannot produce that voice, which the picker
    /// shows as "not available here" instead of pretending.
    /// </summary>
    public async Task<SpeechAudio?> PreviewAsync(VoiceCandidate candidate,
        CancellationToken cancellationToken)
    {
        VoiceProfile previewProfile = new()
        {
            Engine = candidate.Engine,
            VoiceId = candidate.VoiceId,
            Rate = Math.Clamp(_settings.Voice.Rate, 0.5, 1.8),
            Pitch = Math.Clamp(_settings.Voice.Pitch, -0.5, 0.5),
            Volume = Math.Clamp(_settings.Voice.Volume, 0.2, 1.0)
        };

        string sentence = string.IsNullOrWhiteSpace(_settings.Voice.PreviewSentence)
            ? "This is what I sound like."
            : _settings.Voice.PreviewSentence.Trim();

        try
        {
            if (_pipeline is not null &&
                !await _pipeline.EnsureVoiceEngineAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            ISpeechSynthesizer synthesizer = Synthesizer();

            return await synthesizer.SynthesizeAsync(sentence, previewProfile, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            CompanionLog.Info("Voice", $"Voice preview for '{candidate.VoiceId}' failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Locks one candidate in. The returned profile is what the rest of the system
    /// records; nothing else may derive her voice from loose settings afterwards.
    /// This builds the profile without touching settings: the caller records the
    /// choice only after the engine has accepted it, so a failed apply can never
    /// leave in-memory state claiming a voice she does not produce.
    /// </summary>
    public VoiceProfile BuildProfile(VoiceCandidate candidate)
    {
        return new VoiceProfile
        {
            Engine = candidate.Engine,
            VoiceId = string.IsNullOrWhiteSpace(candidate.VoiceId)
                ? candidate.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase) ? "vera" : "af_heart"
                : candidate.VoiceId.Trim(),
            Rate = Math.Clamp(_settings.Voice.Rate, 0.5, 1.8),
            Pitch = Math.Clamp(_settings.Voice.Pitch, -0.5, 0.5),
            Volume = Math.Clamp(_settings.Voice.Volume, 0.0, 1.0)
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _httpSynthesizer?.Dispose();
        _httpSynthesizer = null;
    }

    private ISpeechSynthesizer Synthesizer()
    {
        if (_settings.Voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            return _httpSynthesizer ??= new PocketTtsSpeechSynthesizer(_settings.Voice);
        }

        if (_settings.Voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            // Kokoro speaks its own OpenAI-compatible server on its own port; pointing
            // the preview at the generic http endpoint would dial a dead port.
            return _httpSynthesizer ??= new HttpSpeechSynthesizer(
                _settings.Voice, endpoint: _settings.Voice.KokoroEndpoint, engineName: "kokoro");
        }

        return _httpSynthesizer ??= new HttpSpeechSynthesizer(_settings.Voice);
    }

    /// <summary>Turns "af_heart" into something readable without lying about it.</summary>
    private static string DescribeNeuralVoice(string voiceId)
    {
        string bare = voiceId.Replace('_', ' ');
        if (bare.Length == 0)
        {
            return voiceId;
        }

        string spacing = bare.Replace("af ", string.Empty).Replace("bf ", string.Empty)
            .Replace("am ", string.Empty).Replace("bm ", string.Empty);
        string readable = spacing.Length > 0
            ? char.ToUpperInvariant(spacing[0]) + spacing[1..]
            : voiceId;

        string accent = voiceId.StartsWith("af_", StringComparison.Ordinal) ||
                        voiceId.StartsWith("am_", StringComparison.Ordinal)
            ? "American English"
            : voiceId.StartsWith("bf_", StringComparison.Ordinal) ||
              voiceId.StartsWith("bm_", StringComparison.Ordinal)
                ? "British English"
                : "neural";

        return $"{readable} · {accent}";
    }

    private static string DescribePocketVoice(string voiceId) =>
        string.IsNullOrWhiteSpace(voiceId)
            ? "Unknown voice"
            : char.ToUpperInvariant(voiceId[0]) + voiceId[1..].Replace('_', ' ');
}
