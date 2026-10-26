using System.Text.Json;
using System.Text.Json.Serialization;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Configuration;

/// <summary>
/// Owns the settings file: reading it, writing it safely, and telling everyone
/// when it changed.
///
/// Crash safety: the file is written to a temporary name, flushed, then swapped in
/// with File.Replace, which also keeps the previous version as settings.json.bak.
/// A power cut can therefore lose the newest change, but it can never leave her
/// with a half-written settings file.
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _saveLock = new();

    /// <summary>The settings currently in force. Never null after construction.</summary>
    public CompanionSettings Current { get; private set; } = new();

    /// <summary>Set when the file could not be read cleanly; shown once in the interface.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Raised after a successful save so live screens can refresh.</summary>
    public event Action<CompanionSettings>? Changed;

    /// <summary>
    /// Reads settings from disk, creating the file with defaults on first run.
    /// If the file is damaged, the backup is tried before falling back to defaults,
    /// and the damaged copy is preserved for inspection.
    /// </summary>
    public CompanionSettings Load()
    {
        CompanionPaths.EnsureCreated();
        string path = CompanionPaths.SettingsFile;

        if (!File.Exists(path))
        {
            Current = new CompanionSettings();
            Save();
            CompanionLog.Info("Settings", "Created a fresh settings file with defaults.");
            return Current;
        }

        CompanionSettings? loaded = TryRead(path);
        if (loaded is null)
        {
            string backupPath = path + ".bak";
            loaded = TryRead(backupPath);
            if (loaded is not null)
            {
                LoadWarning = "Your settings file was damaged, so Iris restored the last good backup.";
                CompanionLog.Warning("Settings", "Primary settings file unreadable; backup restored.");
            }
            else
            {
                string quarantine = path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
                TryMove(path, quarantine);
                loaded = new CompanionSettings();
                LoadWarning = "Your settings file could not be read, so Iris started from defaults. " +
                              $"The old file was kept as {Path.GetFileName(quarantine)}.";
                CompanionLog.Error("Settings", "No readable settings or backup; defaults applied.");
            }
        }

        Current = loaded;
        Normalize();
        Save();
        return Current;
    }

    /// <summary>
    /// Coerces values a previous version wrote. The built-in neural engine is Pocket TTS;
    /// explicitly configured HTTP gateways remain supported, but OS voice fallbacks do not.
    /// </summary>
    private void Normalize()
    {
        VoiceSettings voice = Current.Voice;
        voice.Engine ??= "pocket";
        voice.VoiceId ??= "vera";
        voice.CandidateVoices ??= new() { "anna", "mary", "vera" };
        voice.HttpEndpoint ??= "http://127.0.0.1:8880/v1";
        voice.PocketEndpoint ??= "http://127.0.0.1:18765";
        voice.KokoroEndpoint ??= "http://127.0.0.1:18766";
        if (voice.Engine.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            voice.HttpEndpoint.TrimEnd('/').Equals("http://127.0.0.1:8880/v1", StringComparison.OrdinalIgnoreCase))
        {
            // That was the old default URL, not a user-selected server. Move the old
            // Kokoro ids to the new local engine and ask the user to confirm the voice again.
            Dictionary<string, string> oldVoiceMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["af_heart"] = "vera",
                ["af_bella"] = "anna",
                ["af_nicole"] = "vera",
                ["af_aoede"] = "vera",
                ["bf_emma"] = "mary",
                ["bf_isabella"] = "vera"
            };
            if (oldVoiceMap.TryGetValue(voice.VoiceId, out string? mappedVoice))
            {
                voice.VoiceId = mappedVoice;
            }
            else
            {
                voice.VoiceId = "vera";
            }

            voice.Engine = "pocket";
            voice.CandidateVoices = new() { "anna", "mary", "vera" };
            voice.ChosenByUser = false;
            CompanionLog.Info("Settings", "Migrated the former default speech server to local Pocket TTS.");
            IrisEvent.Log("Settings", "settings.migrated", LogLevel.Info,
                message: "Default speech engine migrated to Pocket TTS; voice confirmation is required.");
        }
        else if (voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            // A recognized engine: her own names are hers. Only fill values the user
            // never expressed, never rewrite what they chose.
            if (string.IsNullOrWhiteSpace(voice.VoiceId) || voice.VoiceId.Equals("vera", StringComparison.OrdinalIgnoreCase))
            {
                voice.VoiceId = voice.KokoroDefaultVoice;
            }
            if (voice.CandidateVoices.Count == 0)
            {
                voice.CandidateVoices = new() { "af_heart", "af_bella" };
            }
        }
        else if (!voice.Engine.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                 !voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            // An unrecognized engine string: previously this reset silently, which made
            // a user question ("why is she speaking pocket?") unanswerable. Now it is a
            // receipted migration.
            CompanionLog.Warning("Settings",
                $"Engine '{voice.Engine}' is not one of pocket/kokoro/http; moving her to pocket TTS.");
            IrisEvent.Log("Settings", "settings.engine_migrated", LogLevel.Warning,
                message: "Unrecognized voice engine migrated to Pocket TTS.",
                data: new Dictionary<string, string?> { ["from"] = voice.Engine });
            voice.Engine = "pocket";
            voice.VoiceId = "vera";
            voice.CandidateVoices = new() { "anna", "mary", "vera" };
            voice.ChosenByUser = false;
        }

        if (voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase) &&
            (voice.VoiceId.StartsWith("af_", StringComparison.OrdinalIgnoreCase) ||
             voice.VoiceId.StartsWith("bf_", StringComparison.OrdinalIgnoreCase)))
        {
            voice.VoiceId = "vera";
            voice.ChosenByUser = false;
        }

        if (voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase) &&
            voice.CandidateVoices.Any(candidate => candidate.StartsWith("af_", StringComparison.OrdinalIgnoreCase) ||
                                                   candidate.StartsWith("bf_", StringComparison.OrdinalIgnoreCase)))
        {
            voice.CandidateVoices = new() { "anna", "mary", "vera" };
        }

        if (voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            // Alba sounded male on the target setup, Estelle is the French preset paired
            // with an English model, and Eve mangles English sentences. Keep the picker
            // to the tested English set the owner actually liked.
            voice.CandidateVoices = voice.CandidateVoices
                .Where(candidate => !candidate.Equals("alba", StringComparison.OrdinalIgnoreCase) &&
                                    !candidate.Equals("estelle", StringComparison.OrdinalIgnoreCase) &&
                                    !candidate.Equals("eve", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (voice.CandidateVoices.Count == 0)
            {
                voice.CandidateVoices = new() { "anna", "mary", "vera" };
            }

            if (voice.VoiceId.Equals("alba", StringComparison.OrdinalIgnoreCase) ||
                voice.VoiceId.Equals("estelle", StringComparison.OrdinalIgnoreCase) ||
                voice.VoiceId.Equals("eve", StringComparison.OrdinalIgnoreCase))
            {
                // Eve is gone; anyone parked on her moves to Vera, the tested default.
                voice.VoiceId = "vera";
                voice.ChosenByUser = false;
            }
        }

        voice.PocketMaxThreads = Math.Clamp(voice.PocketMaxThreads, 1, 2);
        if (Current.Ears.MaxRecognitionThreads <= 0)
        {
            Current.Ears.MaxRecognitionThreads = 2;
        }
        else
        {
            Current.Ears.MaxRecognitionThreads = Math.Clamp(Current.Ears.MaxRecognitionThreads, 1, 4);
        }

        // The old default missed quiet speech on many headset mics. Preserve custom
        // thresholds, but migrate that exact historical default to a more forgiving one.
        if (Math.Abs(Current.Ears.VoiceThreshold - 0.055) < 0.000001)
        {
            Current.Ears.VoiceThreshold = 0.025;
        }

        if (string.IsNullOrWhiteSpace(voice.PocketLanguage))
        {
            voice.PocketLanguage = "english_18m_2026-10";
        }
    }

    /// <summary>Replaces the settings object and writes it immediately.</summary>
    public void Update(CompanionSettings newSettings)
    {
        Current = newSettings;
        Save();
        IrisEvent.Log("Settings", "settings.updated", LogLevel.Info);
        Changed?.Invoke(Current);
    }

    /// <summary>Writes the current settings atomically.</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            CompanionPaths.EnsureCreated();
            string path = CompanionPaths.SettingsFile;
            string temporaryPath = path + ".tmp";

            try
            {
                string json = JsonSerializer.Serialize(Current, _serializerOptions);
                using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            catch (Exception exception)
            {
                CompanionLog.Error("Settings", "Could not save settings.", exception);
            }
        }
    }

    /// <summary>Serialises any object with the same conventions as the settings file.</summary>
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _serializerOptions);

    /// <summary>Deserialises with the same conventions, or returns null when the text is unusable.</summary>
    public T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _serializerOptions);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Settings", $"JSON could not be read: {exception.Message}");
            return default;
        }
    }

    private CompanionSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CompanionSettings>(json, _serializerOptions);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Settings", $"Could not read {Path.GetFileName(path)}: {exception.Message}");
            return null;
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Settings", $"Could not quarantine damaged file: {exception.Message}");
        }
    }
}
