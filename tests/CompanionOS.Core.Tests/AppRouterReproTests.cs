using CompanionOS.Core.Configuration;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;
using Xunit;
using Xunit.Abstractions;

namespace CompanionOS.Core.Tests;

/// <summary>
/// Reproduces the owner's own sentences against the real module list, so a routed
/// hand that betrayed a music request is caught the same way it betrayed her live.
/// </summary>
[Collection("settings-store-serial")]
public class AppRouterReproTests
{
    private readonly ITestOutputHelper _output;

    public AppRouterReproTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("play the playlist", "music_playlists")]
    [InlineData("play hollowlight", "music_player")]
    [InlineData("its in your custom player", "music_library")]
    [InlineData("Uh open up calculator", "open_application")]
    [InlineData("can you use the music player", "music_player")]
    public async Task TheOwnersMusicRequestsRouteHerOwnPlayer(string request, string expectedTool)
    {
        EventBus events = new();
        string temp = Path.Combine(Path.GetTempPath(), $"iris-router-repro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            SandboxGuard sandbox = new(events,
                new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
                new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
            await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);
            await host.RegisterAsync(new CoreToolsModule());
            await host.RegisterAsync(new BackgroundTasksModule());
            await host.RegisterAsync(new MusicPlayerModule());
            await host.RegisterAsync(new SystemControlModule());

            IReadOnlyList<ModuleTool> routed = host.RouteTools(request, out IReadOnlyList<IAssistantModule> modules);
            string routedNames = string.Join(", ", routed.Select(tool => tool.Name));
            _output.WriteLine($"Request: '{request}'");
            _output.WriteLine($"Modules: {string.Join(", ", modules.Select(module => module.Id))}");
            _output.WriteLine($"Tools: {routedNames}");

            Assert.Contains(expectedTool, routed.Select(tool => tool.Name).ToList());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SettingsStoreKeepsTheEngineTheUserChooses()
    {
        // Live lesson from 2026-10-26: the file said Engine "kokoro" and Normalize()
        // silently rewrote it to pocket/vera before Save(), so her next boot spoke
        // the old engine with no receipt explaining why. A user-chosen engine must
        // survive the round trip; only a genuinely unrecognized engine may migrate.
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"SchemaVersion\":1,\"Voice\":{\"Engine\":\"kokoro\",\"VoiceId\":\"af_heart\"," +
                "\"CandidateVoices\":[\"af_heart\",\"af_bella\"]}}");

            CompanionSettings loaded;
            SettingsStore store = new();
            loaded = store.Load();

            Assert.Equal("kokoro", loaded.Voice.Engine);
            Assert.Equal("af_heart", loaded.Voice.VoiceId);
            Assert.True(string.IsNullOrEmpty(store.LoadWarning));
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
}
