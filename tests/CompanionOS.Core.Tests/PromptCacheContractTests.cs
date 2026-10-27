using System.Text.Json;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The settings-store tests touch the process-wide CompanionPaths root override, so
/// they share one serial collection with AppRouterReproTests's settings test — run in
/// parallel, the two override/restore around the same static state and race.
/// </summary>
[CollectionDefinition("settings-store-serial")]
public class SettingsStoreSerialCollection;

/// <summary>
/// The prompt-cache contract (2026-10-27 speed autopsy): before it, the system prompt
/// embedded the time, mode, machine state and surfaced memories, so the request's text
/// differed every turn and the model server re-prefilled the whole tool block and
/// history — seconds of dead weight on every reply. Now the static prompt must be
/// byte-stable and the per-turn facts must ride in the &lt;context&gt; block at the END
/// of the request, inside the latest user message, where they can never invalidate
/// anything cached before them.
/// </summary>
[Collection("settings-store-serial")]
public class PromptCacheContractTests
{
    [Fact]
    public void StockTemplateCarriesNoPerTurnPlaceholders()
    {
        // A placeholder in the static template means a per-turn byte change inside
        // the prompt's head, which destroys the cache for everything after it.
        Assert.DoesNotContain("{{time}}", Persona.DefaultTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("{{mode}}", Persona.DefaultTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("{{situation}}", Persona.DefaultTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("{{intuition}}", Persona.DefaultTemplate, StringComparison.Ordinal);
        // The placeholder the template keeps is filled once and never varies per turn.
        Assert.Contains("{{capabilities}}", Persona.DefaultTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSystemPromptIsByteIdenticalAcrossTurns()
    {
        // The whole point of the contract: two turns with the world in a different
        // state must produce the SAME system prompt bytes, so the server reuses its
        // KV cache. (Persona-level guard; the brain assembles it the same way every turn.)
        Persona persona = Persona.CreateDefault();
        string before = persona.BuildSystemPrompt("Music Player: plays music.");

        System.Threading.Thread.Sleep(1100); // the clock moves; the prompt must not
        string after = persona.BuildSystemPrompt("Music Player: plays music.");

        Assert.Equal(before, after);
    }

    [Fact]
    public void StaticPromptResolvesEveryPlaceholderItKeeps()
    {
        string prompt = Persona.CreateDefault().BuildSystemPrompt("Music Player: plays music.");

        Assert.DoesNotContain("{{name}}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{user}}", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{capabilities}}", prompt, StringComparison.Ordinal);
        // Legacy per-turn placeholders from owner-edited stock collapse to nothing,
        // so an old-edited template never leaves a half-rendered slot behind.
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
        Assert.Contains("Music Player: plays music.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnContextCarriesTheDynamicFactsInsideContextTags()
    {
        string context = Persona.CreateDefault().BuildTurnContext(
            "Foreground: VS Code.", "Boss likes rain.", "awake", "Tuesday 09:41");

        Assert.StartsWith("<context>", context, StringComparison.Ordinal);
        Assert.EndsWith("</context>", context, StringComparison.Ordinal);
        Assert.Contains("mode: awake", context, StringComparison.Ordinal);
        Assert.Contains("time: Tuesday 09:41", context, StringComparison.Ordinal);
        Assert.Contains("machine: Foreground: VS Code.", context, StringComparison.Ordinal);
        Assert.Contains("memories:", context, StringComparison.Ordinal);
        Assert.Contains("Boss likes rain.", context, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnContextOmitsSectionsWithNothingToSay()
    {
        string context = Persona.CreateDefault().BuildTurnContext("", "", "gaming", "Tuesday 09:41");

        Assert.DoesNotContain("machine:", context, StringComparison.Ordinal);
        Assert.DoesNotContain("memories:", context, StringComparison.Ordinal);
        Assert.Contains("mode: gaming", context, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsVersionTwoWithOldAutoSleepDefaultMigratesToStayWarm()
    {
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"SchemaVersion\":2,\"Brain\":{\"AutoSleepIdleMinutes\":20}}");

            SettingsStore store = new();
            CompanionSettings loaded = store.Load();

            Assert.Equal(0, loaded.Brain.AutoSleepIdleMinutes);
            Assert.Equal(CompanionSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        }
        finally
        {
            CompanionPaths.OverrideRoot(previousRoot);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DeliberateCustomAutoSleepTimeoutIsNeverMoved()
    {
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"SchemaVersion\":2,\"Brain\":{\"AutoSleepIdleMinutes\":45}}");

            SettingsStore store = new();
            CompanionSettings loaded = store.Load();

            Assert.Equal(45, loaded.Brain.AutoSleepIdleMinutes);
        }
        finally
        {
            CompanionPaths.OverrideRoot(previousRoot);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void VersionOneFileRunsVoiceAndAutoSleepMigrationsInOrder()
    {
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"SchemaVersion\":1,\"Voice\":{\"Engine\":\"kokoro\",\"VoiceId\":\"af_heart\"}," +
                "\"Brain\":{\"AutoSleepIdleMinutes\":20}}");

            SettingsStore store = new();
            CompanionSettings loaded = store.Load();

            // The voice migration preserves a recognized Kokoro voice; only the dead
            // HTTP-default path rewrites ids. See SettingsStoreKeepsTheEngineTheUserChooses.
            Assert.Equal("kokoro", loaded.Voice.Engine);
            Assert.Equal("af_heart", loaded.Voice.VoiceId);
            Assert.Equal(0, loaded.Brain.AutoSleepIdleMinutes);
            Assert.Equal(CompanionSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        }
        finally
        {
            CompanionPaths.OverrideRoot(previousRoot);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SerializedTurnContextGoesToTheRequestTailNotTheSystemMessage()
    {
        // The brain places the <context> block inside the latest user message. This
        // guard pins the SHAPE the brain relies on: the block is self-delimited so a
        // reader (model or human, in a journal) can tell OS facts from user words.
        string context = Persona.CreateDefault().BuildTurnContext(
            "Foreground: Chrome.", "", "awake", "Tuesday 09:41");
        string userMessage = $"{context}\n\nwhat time is it?";

        Assert.StartsWith("<context>", userMessage, StringComparison.Ordinal);
        Assert.EndsWith("what time is it?", userMessage, StringComparison.Ordinal);
        Assert.Equal(1, userMessage.Split("</context>").Length - 1);
    }
}
