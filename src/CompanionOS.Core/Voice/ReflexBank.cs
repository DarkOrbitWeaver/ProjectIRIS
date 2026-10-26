using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Voice;

/// <summary>
/// Pre-rendered phrases in her own locked voice, cached on disk.
///
/// This is the trick that makes her feel instant: saying "On it." takes zero
/// milliseconds because the audio already exists, while the model is still thinking.
/// The cache is keyed by the voice profile signature, so changing her voice
/// invalidates it automatically and she never speaks in yesterday's timbre.
/// </summary>
public sealed class ReflexBank
{
    private readonly Persona _persona;
    private readonly VoiceProfile _profile;
    private readonly ISpeechSynthesizer _synthesizer;
    private readonly string _cacheDirectory;
    private readonly Random _random = new();
    private readonly Dictionary<string, List<SpeechAudio>> _loadedCategories = new(StringComparer.OrdinalIgnoreCase);

    public ReflexBank(Persona persona, VoiceProfile profile, ISpeechSynthesizer synthesizer, string cacheDirectory)
    {
        _persona = persona;
        _profile = profile;
        _synthesizer = synthesizer;
        _cacheDirectory = Path.Combine(cacheDirectory, "reflex", profile.Signature);
    }

    /// <summary>How many phrases are ready to be spoken instantly.</summary>
    public int ReadyCount => _loadedCategories.Values.Sum(list => list.Count);

    /// <summary>
    /// Renders every phrase once and keeps it in memory. Runs in the background at
    /// startup; while it is running, calling <see cref="TryPick"/> with a category that
    /// is not ready yet simply returns null and the brain speaks nothing extra.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<string>> categories = DescribeCategories();
        Directory.CreateDirectory(_cacheDirectory);

        foreach (KeyValuePair<string, List<string>> category in categories)
        {
            List<SpeechAudio> rendered = new();

            for (int phraseIndex = 0; phraseIndex < category.Value.Count; phraseIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string filePath = Path.Combine(_cacheDirectory, $"{category.Key}-{phraseIndex}.{_synthesizer.EngineName}.wav");
                byte[]? cachedBytes = TryReadCache(filePath);

                if (cachedBytes is not null)
                {
                    rendered.Add(new SpeechAudio { Bytes = cachedBytes, Format = "wav" });
                    continue;
                }

                SpeechAudio? audio = await _synthesizer
                    .SynthesizeAsync(category.Value[phraseIndex], _profile, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (audio is null || audio.Format != "wav")
                {
                    continue;
                }

                rendered.Add(audio);
                TryWriteCache(filePath, audio.Bytes);
            }

            lock (_loadedCategories)
            {
                _loadedCategories[category.Key] = rendered;
            }
        }

        CompanionLog.Info("Voice", $"Reflex bank ready with {ReadyCount} instant phrases.");
    }

    /// <summary>Returns one already-rendered phrase for a category, or null.</summary>
    public SpeechAudio? TryPick(string category)
    {
        lock (_loadedCategories)
        {
            if (!_loadedCategories.TryGetValue(category, out List<SpeechAudio>? phrases) || phrases.Count == 0)
            {
                return null;
            }

            return phrases[_random.Next(phrases.Count)];
        }
    }

    /// <summary>Every category and its phrases, taken from the persona file.</summary>
    public Dictionary<string, List<string>> DescribeCategories() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["greeting"] = _persona.Reflex.Greeting,
        ["acknowledge"] = _persona.Reflex.Acknowledge,
        ["thinking"] = _persona.Reflex.Thinking,
        ["confirmed"] = _persona.Reflex.Confirmed,
        ["clarify"] = _persona.Reflex.Clarify,
        ["sleep"] = _persona.Reflex.Sleep,
        ["wake"] = _persona.Reflex.Wake,
        ["gaming"] = _persona.Reflex.Gaming,
        ["gamingEnd"] = _persona.Reflex.GamingEnd,
        ["error"] = _persona.Reflex.Error,
        ["busy"] = _persona.Reflex.ThinkingBusy
    };

    private static byte[]? TryReadCache(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteCache(string filePath, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(filePath, bytes);
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"Reflex cache not saved: {exception.Message}");
        }
    }
}
