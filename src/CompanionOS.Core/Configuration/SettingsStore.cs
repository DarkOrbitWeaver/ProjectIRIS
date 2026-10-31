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
        Converters = { new TolerantEnumConverterFactory() }
    };

    private readonly object _saveLock = new();

    /// <summary>
    /// The one window for deliberate settings writes from NON-UI threads: modules and
    /// housekeeping timers. Reads and mutates under one lock, then rewrites the file
    /// only when the delegate says the content truly changed. The interface treads on
    /// the UI thread and serializes on its own pump; a module thread never races a
    /// serialization of the same list it is editing (2026-10-27 audit finding 96).
    /// </summary>
    public bool Mutate(Func<CompanionSettings, bool> mutate)
    {
        bool changed;
        lock (_saveLock)
        {
            changed = mutate(Current);
        }

        if (!changed)
        {
            return false;
        }

        Save();
        return true;
    }

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
            Normalize();
            Save();
            CompanionLog.Info("Settings", "Created a fresh settings file with defaults.");
            return Current;
        }

        CompanionSettings? loaded = TryRead(path);
        if (loaded is null)
        {
            loaded = TryRead(path + ".bak");
            if (loaded is not null)
            {
                LoadWarning = "Your settings file was damaged, so Iris restored the last good backup.";
                CompanionLog.Warning("Settings", "Primary settings file unreadable; backup restored.");

                // The on-disk file still holds the damaged text, so rewriting IS the
                // repair here; the comment-preserving rule below does not apply.
                Current = loaded;
                Normalize();
                Save();
                return Current;
            }

            string quarantine = path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            TryMove(path, quarantine);
            Current = new CompanionSettings();
            Normalize();
            LoadWarning = "Your settings file could not be read, so Iris started from defaults. " +
                          $"The old file was kept as {Path.GetFileName(quarantine)}.";
            CompanionLog.Error("Settings", "No readable settings or backup; defaults applied.");
            Save();
            return Current;
        }

        Current = loaded;

        // Canonical snapshot of what the file MEANT, taken before coercion. The boot
        // save is skipped unless normalization actually changed a value, decided on
        // canonical serializations so user comments and formatting never count as a
        // change — the old unconditional boot-save destroyed every comment a user had
        // typed, though the reader deliberately tolerates them (2026-10-27 audit).
        string loadedJson = JsonSerializer.Serialize(Current, _serializerOptions);
        Normalize();
        string normalizedJson = JsonSerializer.Serialize(Current, _serializerOptions);
        if (!string.Equals(loadedJson, normalizedJson, StringComparison.Ordinal))
        {
            Save();
        }

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

        if (Current.SchemaVersion < CompanionSettings.CurrentSchemaVersion)
        {
            int previousVersion = Current.SchemaVersion;
            if (previousVersion < 2)
            {
                MigrateLegacyVoiceSettings(voice);
            }
            if (previousVersion < 3)
            {
                MigrateAutoSleepPolicy();
            }
            if (previousVersion < 4)
            {
                MigrateOverlayPosition();
            }
            if (previousVersion < 5)
            {
                MigrateConversationWindow();
            }
            if (previousVersion < 6)
            {
                MigrateVoiceSensitivity();
            }
            if (previousVersion < 7)
            {
                MigrateVoiceEndpointing();
            }

            Current.SchemaVersion = CompanionSettings.CurrentSchemaVersion;
            CompanionLog.Info("Settings",
                $"Settings migrated from schema version {previousVersion} to {CompanionSettings.CurrentSchemaVersion}.");
        }

        voice.PocketMaxThreads = Math.Clamp(voice.PocketMaxThreads, 1, 4);
        voice.KokoroMaxThreads = Math.Clamp(voice.KokoroMaxThreads, 1, 4);
        voice.KokoroProvider = string.Equals(voice.KokoroProvider, "dml", StringComparison.OrdinalIgnoreCase)
            ? "dml"
            : "cpu";
        Current.RememberedPromptChoices ??= new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in Current.RememberedPromptChoices
                     .Where(pair => pair.Key.Length > 80 || string.IsNullOrWhiteSpace(pair.Value) ||
                                    pair.Value.Length > 100).ToArray())
        {
            Current.RememberedPromptChoices.Remove(pair.Key);
        }
        while (Current.RememberedPromptChoices.Count > 100)
        {
            Current.RememberedPromptChoices.Remove(Current.RememberedPromptChoices.Keys.First());
        }
        if (Current.Ears.MaxRecognitionThreads <= 0)
        {
            Current.Ears.MaxRecognitionThreads = 2;
        }
        else
        {
            Current.Ears.MaxRecognitionThreads = Math.Clamp(Current.Ears.MaxRecognitionThreads, 1, 4);
        }

        if (string.IsNullOrWhiteSpace(voice.PocketLanguage))
        {
            voice.PocketLanguage = "english_18m_2026-10";
        }
    }

    private void MigrateVoiceSensitivity()
    {
        // These were the historical defaults, including the 0.025 value written by
        // the earlier threshold migration. Keep user-tuned values untouched.
        if (Math.Abs(Current.Ears.VoiceThreshold - 0.055) < 0.000001 ||
            Math.Abs(Current.Ears.VoiceThreshold - 0.025) < 0.000001)
        {
            Current.Ears.VoiceThreshold = 0.01;
        }
    }

    private void MigrateVoiceEndpointing()
    {
        Current.Ears.SilenceMilliseconds = Math.Max(3500, Current.Ears.SilenceMilliseconds);
    }

    /// <summary>
    /// The schema-2-to-3 migration: move files still carrying the historical 20-minute
    /// auto-unload default to the stay-warm policy. A deliberate custom timeout keeps
    /// its meaning; only the old untouched default moves. The GPU is freed on demand
    /// by Gaming mode, so a periodic unload costs every next message a full model
    /// load (2026-10-27 speed autopsy), and silent surprise migrations are exactly
    /// what this store refuses — hence the schema step and the receipt.
    /// </summary>
    private void MigrateAutoSleepPolicy()
    {
        if (Current.Brain.AutoSleepIdleMinutes == 20)
        {
            Current.Brain.AutoSleepIdleMinutes = 0;
            IrisEvent.Log("Settings", "settings.autosleep_migrated", LogLevel.Info,
                message: "Auto-unload after idle moved to stay-warm; gaming mode still frees the GPU on demand.");
        }
    }

    /// <summary>Schema 3 to 4: move the floating companion and its overlay origin
    /// to the top-right corner. Clear the old drag location once so it cannot keep
    /// restoring the pill to the former bottom dock.</summary>
    private void MigrateOverlayPosition()
    {
        Current.Interface.OverlayPosition = "top right";
        Current.Interface.OverlayPositionPx = string.Empty;
    }

    /// <summary>
    /// Schema 4 to 5: keep the untouched 16-message default, which tool-call-heavy
    /// exchanges could exhaust in only a few turns, for twice as much recent context.
    /// A deliberately customized window keeps its chosen size.
    /// </summary>
    private void MigrateConversationWindow()
    {
        if (Current.Memory.ConversationWindowMessages == 16)
        {
            Current.Memory.ConversationWindowMessages = 32;
        }
    }

    /// <summary>
    /// The schema-1-to-2 migration, keyed on <see cref="CompanionSettings.CurrentSchemaVersion"/>
    /// rather than re-derived from magic strings at every boot. A user's saved voice
    /// list and ChosenByUser flag must not be re-decided on each start whenever a
    /// legacy id happens to be in the list (2026-10-27 audit).
    /// </summary>
    private void MigrateLegacyVoiceSettings(VoiceSettings voice)
    {
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
            voice.VoiceId = oldVoiceMap.TryGetValue(voice.VoiceId, out string? mappedVoice)
                ? mappedVoice
                : "vera";

            voice.Engine = "pocket";
            voice.CandidateVoices = new() { "anna", "mary", "vera" };
            voice.ChosenByUser = false;
            CompanionLog.Info("Settings", "Migrated the former default speech server to local Pocket TTS.");
            IrisEvent.Log("Settings", "settings.migrated", LogLevel.Info,
                message: "Default speech engine migrated to Pocket TTS; voice confirmation is required.");
        }
        else if (!voice.Engine.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                 !voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase) &&
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

/// <summary>
/// Reads enum settings forgivingly. The old strict reader threw a single malformed
/// enum word out of the whole deserialization — picky "gamingg" typo meant both the
/// primary file and its backup failed, Iris quarantined everything and restarted
/// from defaults, destroying profiles, hotkeys and endpoints (2026-10-27 audit).
/// Unknown words now reset just that one property to its enum default and the rest
/// of the file keeps every bit of its meaning.
/// </summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Numbers pass through unchanged: staying byte- and number-compatible with
        // the standard enum writer matters more than strictness.
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64());
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return default;
        }

        string word = reader.GetString() ?? string.Empty;
        if (Enum.TryParse(word, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        // The enum's zero value is the neutral fallback; normalization keeps filling
        // any fields a defaulted value leaves empty. No other property is touched.
        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
