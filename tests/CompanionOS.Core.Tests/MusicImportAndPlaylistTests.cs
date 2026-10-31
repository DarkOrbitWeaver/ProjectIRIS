using System.Text.Json;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;
using CompanionOS.Core.Perception;
using CompanionOS.Modules;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The journaled music failure, end to end: Iris found a downloaded Yates XV track,
/// created "Boss's Playlist", then could not add it because downloads were never
/// indexed — and the follow-up "sure" / "add it" produced a claim with no tool
/// behind it.
///
/// These run the real module against real files in a temp workspace. Every stage is
/// separate on purpose: found ≠ imported ≠ in a playlist.
/// </summary>
public sealed class MusicImportAndPlaylistTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _downloads;
    private readonly EventBus _events = new();

    public MusicImportAndPlaylistTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "iris-music-" + Guid.NewGuid().ToString("n")[..8]);
        _workspace = Path.Combine(_root, "workspace");
        _downloads = Path.Combine(_workspace, "Downloads");
        Directory.CreateDirectory(_downloads);
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateDownloadedTrack(string fileName)
    {
        string path = Path.Combine(_downloads, fileName);
        File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00 });
        return path;
    }

    private ModuleContext CreateContext()
    {
        CompanionSettings settings = new();
        // Point the scanner at an empty folder so the test never walks the real machine.
        settings.Music.SearchDirectories = new List<string> { Path.Combine(_root, "configured-music") };
        Directory.CreateDirectory(settings.Music.SearchDirectories[0]);

        return new ModuleContext
        {
            Settings = settings,
            PersistSettings = () => { },
            MutateSettings = mutate => mutate(settings),
            Events = _events,
            Memory = null!,
            Sandbox = new SandboxGuard(_events,
                new FileAuditTrail(Path.Combine(_root, "audit.jsonl")),
                new PermissionGrantStore(Path.Combine(_root, "permissions.json"))),
            BackgroundTasks = new BackgroundTaskManager(_events),
            Ui = new ModuleUiService(_events).ForModule("music", "Music Player", "music"),
            WorkspaceDirectory = _workspace,
            ModelsDirectory = Path.Combine(_root, "models"),
            CacheDirectory = Path.Combine(_root, "cache"),
            Host = new SilentHost()
        };
    }

    private static async Task<ToolResult> CallAsync(MusicPlayerModule module, string toolName, string argumentsJson)
    {
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
        return await module.ExecuteAsync(new ToolInvocation
        {
            ModuleId = "music",
            Tool = module.Tools.Single(tool => tool.Name == toolName),
            Arguments = arguments.RootElement.Clone(),
            TurnId = "test-turn"
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TheDownloadedTrackIsFoundImportedAndVerifiedInThePlaylistAsSeparateSteps()
    {
        string downloaded = CreateDownloadedTrack("Yates XV - Midnight Signal.mp3");
        using MusicPlayerModule module = new();
        await module.InitializeAsync(CreateContext(), CancellationToken.None);

        // 1. Find: read-only, reports candidates and changes nothing.
        ToolResult found = await CallAsync(module, "music_find_download", "{\"query\":\"Yates XV\"}");
        Assert.True(found.Succeeded);
        Assert.Contains(downloaded, found.ModelText, StringComparison.OrdinalIgnoreCase);

        // Finding is not importing: the library still does not contain it.
        ToolResult beforeImport = await CallAsync(module, "music_playlists",
            $"{{\"action\":\"add_file\",\"name\":\"Boss's Playlist\",\"file_path\":{JsonSerializer.Serialize(downloaded)}}}");
        Assert.False(beforeImport.Succeeded);

        // 2. Import: indexes the existing file by reference.
        ToolResult imported = await CallAsync(module, "music_import_file",
            $"{{\"path\":{JsonSerializer.Serialize(downloaded)}}}");
        Assert.True(imported.Succeeded);
        Assert.True(File.Exists(downloaded)); // never moved, copied or renamed

        // 3. Playlist create, then add the exact imported file.
        Assert.True((await CallAsync(module, "music_playlists",
            "{\"action\":\"create\",\"name\":\"Boss's Playlist\"}")).Succeeded);

        ToolResult added = await CallAsync(module, "music_playlists",
            $"{{\"action\":\"add_file\",\"name\":\"Boss's Playlist\",\"file_path\":{JsonSerializer.Serialize(downloaded)}}}");

        Assert.True(added.Succeeded);
        // 4. The success is backed by a read-after-write, not by the call returning.
        Assert.True(added.SelfVerified);
        Assert.Contains("read back", added.ModelText, StringComparison.OrdinalIgnoreCase);

        // 5. Independent inspection confirms real membership on disk.
        ToolResult inspected = await CallAsync(module, "music_playlists",
            "{\"action\":\"inspect\",\"name\":\"Boss's Playlist\"}");
        Assert.Contains(downloaded, inspected.ModelText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnImportedFileIsStillInTheLibraryAfterARestart()
    {
        string downloaded = CreateDownloadedTrack("Yates XV - Restart Proof.mp3");

        using (MusicPlayerModule first = new())
        {
            await first.InitializeAsync(CreateContext(), CancellationToken.None);
            Assert.True((await CallAsync(first, "music_import_file",
                $"{{\"path\":{JsonSerializer.Serialize(downloaded)}}}")).Succeeded);
        }

        // A brand new module instance over the same workspace is the restart.
        using MusicPlayerModule second = new();
        await second.InitializeAsync(CreateContext(), CancellationToken.None);
        ToolResult listed = await CallAsync(second, "music_library", "{\"action\":\"refresh\"}");
        Assert.True(listed.Succeeded);

        ToolResult titles = await CallAsync(second, "music_library", "{\"action\":\"list\",\"limit\":50}");
        Assert.Contains("Restart Proof", titles.ModelText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADownloadedFileIsNotPlaylistMembershipUntilItIsActuallyAdded()
    {
        string downloaded = CreateDownloadedTrack("Yates XV - Not In Playlist.mp3");
        using MusicPlayerModule module = new();
        await module.InitializeAsync(CreateContext(), CancellationToken.None);

        await CallAsync(module, "music_import_file", $"{{\"path\":{JsonSerializer.Serialize(downloaded)}}}");
        await CallAsync(module, "music_playlists", "{\"action\":\"create\",\"name\":\"Boss's Playlist\"}");

        ToolResult inspected = await CallAsync(module, "music_playlists",
            "{\"action\":\"inspect\",\"name\":\"Boss's Playlist\"}");

        Assert.True(inspected.Succeeded);
        Assert.Contains("empty", inspected.ModelText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not In Playlist", inspected.ModelText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingOrUnsupportedFileIsRefusedInsteadOfInvented()
    {
        using MusicPlayerModule module = new();
        await module.InitializeAsync(CreateContext(), CancellationToken.None);

        ToolResult missing = await CallAsync(module, "music_import_file",
            $"{{\"path\":{JsonSerializer.Serialize(Path.Combine(_downloads, "does-not-exist.mp3"))}}}");
        Assert.False(missing.Succeeded);

        string textFile = Path.Combine(_downloads, "notes.txt");
        File.WriteAllText(textFile, "not audio");
        ToolResult unsupported = await CallAsync(module, "music_import_file",
            $"{{\"path\":{JsonSerializer.Serialize(textFile)}}}");
        Assert.False(unsupported.Succeeded);
    }

    [Fact]
    public async Task TheDownloadSearchReportsItsExactScopeAndNeverClaimsTheWholePc()
    {
        using MusicPlayerModule module = new();
        await module.InitializeAsync(CreateContext(), CancellationToken.None);

        ToolResult result = await CallAsync(module, "music_find_download", "{\"query\":\"nothing matches this\"}");

        Assert.True(result.Succeeded);
        Assert.Contains("Scope checked", result.ModelText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No file was imported", result.ModelText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheExternalFileToolsKeepTheirConfirmationGate()
    {
        using MusicPlayerModule module = new();
        ModuleTool import = module.Tools.Single(tool => tool.Name == "music_import_file");
        ModuleTool find = module.Tools.Single(tool => tool.Name == "music_find_download");

        // Reading and indexing files outside her own folder must always ask.
        Assert.Equal(ToolTier.SystemCritical, import.Tier);
        Assert.True(import.AlwaysConfirm);
        Assert.Equal(ToolTier.SystemCritical, find.Tier);
        Assert.False(find.AlwaysConfirm);
        Assert.Equal("music.downloads.read", find.PermissionScope);
    }

    [Fact]
    public void ImportIndexRejectsRelativePathsAndKeepsSourceFilesUntouched()
    {
        MusicLibraryImportIndex index = new(_workspace);
        Assert.False(index.Add("relative/path.mp3", out _, out string reason));
        Assert.Contains("absolute", reason, StringComparison.OrdinalIgnoreCase);

        string track = CreateDownloadedTrack("Keep Me.mp3");
        long sizeBefore = new FileInfo(track).Length;
        Assert.True(index.Add(track, out string resolved, out _));
        Assert.Equal(track, resolved);
        Assert.True(File.Exists(track));
        Assert.Equal(sizeBefore, new FileInfo(track).Length);
        Assert.Contains(track, index.Load(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A host that does nothing, so a module test never touches the machine.</summary>
    private sealed class SilentHost : IAssistantHostServices
    {
        public OperatingMode CurrentMode => OperatingMode.Awake;
        public bool IsMicrophoneOpen => false;
        public string PersonaName => "Iris";
        public Task SetOperatingModeAsync(OperatingMode mode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetManualGamingAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ScreenFrame?> LookAtScreenAsync(bool forceFreshCapture, CancellationToken cancellationToken) =>
            Task.FromResult<ScreenFrame?>(null);
        public string DescribeSituation() => "test";
        public Task<MediaControlResult> ControlMediaAsync(string action, CancellationToken cancellationToken) =>
            Task.FromResult(MediaControlResult.Failure("no media in tests"));
        public string DescribeCapabilities() => string.Empty;
        public void LearnGame(string executableName, bool isGame) { }
        public void SetMicrophoneOpen(bool isOpen) { }
        public Task SpeakAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
