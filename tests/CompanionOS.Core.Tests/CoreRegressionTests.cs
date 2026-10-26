using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Voice;
using CompanionOS.Core.Modules.BuiltIn;
using CompanionOS.Core.Plugins;
using CompanionOS.Modules;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CompanionOS.Core.Tests;

public sealed class CoreRegressionTests
{
    [Theory]
    [InlineData("{\"schema\":1,\"id\":\"sample\",\"name\":\"Sample\",\"transport\":\"builtin\"}", true)]
    [InlineData("{\"schema\":1,\"id\":\"sample\",\"name\":\"Sample\",\"transport\":\"builtin\",\"disabled\":true}", true)]
    [InlineData("{\"schema\":1,\"id\":\"sample\",\"name\":\"Sample\",\"transport\":\"builtin\",\"disabled\":false}", false)]
    public void ExternalModuleManifestRequiresExplicitEnablement(string json, bool expectedDisabled)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plugin.json"), json);

            bool loaded = PluginManifest.TryLoad(folder, out PluginManifest? manifest, out string reason);

            Assert.True(loaded, reason);
            Assert.NotNull(manifest);
            Assert.Equal(expectedDisabled, manifest.Disabled);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExternalModuleManifestRejectsNonBooleanEnablementState()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plugin.json"),
                "{\"schema\":1,\"id\":\"sample\",\"name\":\"Sample\",\"transport\":\"builtin\",\"disabled\":\"false\"}");

            bool loaded = PluginManifest.TryLoad(folder, out _, out string reason);

            Assert.False(loaded);
            Assert.Contains("disabled", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExternalModuleManifestAcceptsBoundedSidebarControlsForDeclaredTools()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-surface-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plugin.json"), """
                {
                  "schema": 1,
                  "id": "telegram",
                  "name": "Telegram",
                  "transport": "builtin",
                  "asksPermission": ["contacts.read", "messages.send"],
                  "tools": [{
                    "name": "send_message",
                    "description": "Send a message.",
                    "permission": "messages.send",
                    "schema": {"type":"object","properties":{"recipient":{"type":"string"},"message":{"type":"string"}}}
                  }],
                  "surfaces": [{
                    "id": "compose",
                    "title": "New message",
                    "slot": "sidebar",
                    "icon": "telegram",
                    "elements": [
                      {"id":"recipient","type":"input","name":"recipient","label":"To","placeholder":"Contact"},
                      {"id":"message","type":"input","name":"message","label":"Message"},
                      {"id":"send","type":"button","label":"Send","tool":"send_message"}
                    ]
                  }]
                }
                """);

            bool loaded = PluginManifest.TryLoad(folder, out PluginManifest? manifest, out string reason);

            Assert.True(loaded, reason);
            Assert.NotNull(manifest);
            PluginSurfaceManifest surface = Assert.Single(manifest.Surfaces);
            Assert.Equal("telegram", surface.Icon);
            Assert.Equal("sidebar", surface.Slot);
            Assert.Equal(3, surface.Elements.Count);
            Assert.Equal("send_message", surface.Elements[^1].ToolName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("messages.send", "contacts.read", "undeclared permission scope")]
    [InlineData("bad scope", "bad scope", "invalid or duplicated")]
    [InlineData("", "", "invalid or duplicated")]
    public void ExternalModuleManifestRejectsInvalidOrUndeclaredPermissionScopes(
        string toolPermission, string declaredPermission, string expectedReason)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-permission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            object manifest = new
            {
                schema = 1,
                id = "sample",
                name = "Sample",
                transport = "builtin",
                asksPermission = new[] { declaredPermission },
                tools = new[]
                {
                    new
                    {
                        name = "send",
                        description = "Send a message.",
                        permission = toolPermission,
                        schema = new { type = "object", properties = new { } }
                    }
                }
            };
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(manifest));

            bool loaded = PluginManifest.TryLoad(folder, out _, out string reason);

            Assert.False(loaded);
            Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExternalModuleManifestRejectsSurfaceActionsThatAreNotDeclaredTools()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-surface-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plugin.json"), """
                {
                  "schema": 1,
                  "id": "sample",
                  "name": "Sample",
                  "transport": "builtin",
                  "surfaces": [{
                    "id": "actions",
                    "title": "Actions",
                    "slot": "sidebar",
                    "icon": "body",
                    "elements": [{"id":"run","type":"button","label":"Run","tool":"missing_tool"}]
                  }]
                }
                """);

            bool loaded = PluginManifest.TryLoad(folder, out _, out string reason);

            Assert.False(loaded);
            Assert.Contains("declared tool", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExternalModuleToolNamesAreQualifiedAndCollisionResistant()
    {
        using PluginHost host = new(new EventBus(), Path.Combine(Path.GetTempPath(), $"iris-plugin-{Guid.NewGuid():N}"));
        PluginManifest manifest = new()
        {
            Id = "clock",
            Name = "Clock",
            Tools =
            [
                new PluginTool("now.time", "Read the current time.", null, "{}"),
                new PluginTool("now_time", "Read the current time another way.", null, "{}")
            ]
        };

        PluginModuleAdapter adapter = new(host, manifest);

        Assert.Equal(2, adapter.Tools.Count);
        Assert.All(adapter.Tools, tool => Assert.StartsWith("plugin_clock_", tool.Name));
        Assert.NotEqual(adapter.Tools[0].Name, adapter.Tools[1].Name);
    }

    [Fact]
    public async Task ExternalCommandModuleResolvesPathRuntimeAndReceivesLocalToolName()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "read-tool.ps1"),
            "$request = ConvertFrom-Json -InputObject ([Console]::In.ReadLine()); [Console]::Out.WriteLine($request.tool)");
        using PluginHost host = new(new EventBus(), folder);
        PluginManifest plugin = new()
        {
            Id = "path-check",
            Name = "Path check",
            Transport = "command",
            Command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "read-tool.ps1"],
            Folder = folder,
            Disabled = false
        };

        try
        {
            PluginResult result = await host.CallAsync(plugin,
                new PluginTool("send_message", "Send a message.", null, "{}"), "{}", "test-turn",
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Contains("send_message", result.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task McpPluginDiscoversToolsAndUsesManifestPermissionPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-plugin-discovery-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "sample");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "server.ps1"), """
            while ($null -ne ($line = [Console]::In.ReadLine())) {
                $request = ConvertFrom-Json -InputObject $line
                if ($request.method -eq 'notifications/initialized') { continue }
                if ($request.method -eq 'initialize') {
                    $result = @{ protocolVersion = '2024-11-05'; capabilities = @{}; serverInfo = @{ name = 'test'; version = '1' } }
                } elseif ($request.method -eq 'tools/list') {
                    if ($request.params.cursor -eq 'page-2') {
                        $result = @{ tools = @(@{
                            name = 'search_contacts'
                            description = 'Find contacts.'
                            inputSchema = @{ type = 'object'; properties = @{ query = @{ type = 'string' } }; required = @('query') }
                        }) }
                    } else {
                        $result = @{
                            tools = @(@{
                                name = 'send_message'
                                description = 'Send a message to a contact.'
                                inputSchema = @{ type = 'object'; properties = @{ recipient = @{ type = 'string' } }; required = @('recipient') }
                            })
                            nextCursor = 'page-2'
                        }
                    }
                } else {
                    continue
                }
                $response = @{ jsonrpc = '2.0'; id = $request.id; result = $result }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 20))
            }
            """);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """
            {
              "schema": 1,
              "id": "telegram",
              "name": "Telegram",
              "version": "0.1.0",
              "description": "Messaging tools",
              "transport": "mcp",
              "command": ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "server.ps1"],
              "disabled": false,
              "discoverTools": true,
              "asksPermission": ["messages.send"],
              "tools": [{ "name": "send_message", "permission": "messages.send" }],
              "surfaces": [{
                "id": "compose",
                "title": "New message",
                "slot": "sidebar",
                "icon": "telegram",
                "elements": [
                  { "id": "recipient", "type": "input", "name": "recipient", "label": "To" },
                  { "id": "send", "type": "button", "label": "Send", "tool": "send_message" }
                ]
              }]
            }
            """);

        try
        {
            using PluginHost host = new(new EventBus(), root);
            host.Load();

            await host.PrepareEnabledAsync(CancellationToken.None);

            PluginManifest plugin = Assert.Single(host.Loaded);
            Assert.Equal(2, plugin.Tools.Count);
            PluginTool tool = plugin.Tools.Single(candidate => candidate.Name == "send_message");
            Assert.Equal("send_message", tool.Name);
            Assert.Equal("messages.send", tool.Permission);
            Assert.Contains("recipient", tool.Schema);
            Assert.Contains(plugin.Tools, candidate => candidate.Name == "search_contacts");
            Assert.Single(plugin.Surfaces);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("IRIS", true, "")]
    [InlineData("Hey, Iris!", true, "")]
    [InlineData("YO IRIS, play the next song", true, "play the next song")]
    [InlineData("Hey irys find that folder", true, "find that folder")]
    [InlineData("Iris's add this to my playlist", true, "add this to my playlist")]
    [InlineData("hey there", false, "")]
    [InlineData("Actually can you please Iris play music", false, "")]
    public void WakeMatcherRespondsToCallsAndKeepsTheCommandWithoutWakingOnUnrelatedSpeech(
        string transcript, bool expectedMatch, string expectedRemainder)
    {
        WakePhraseMatcher matcher = new(new Persona());

        bool matched = matcher.TryMatch(transcript, out string remainder);

        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedRemainder, remainder);
    }

    [Fact]
    public void WakeMatcherUsesTheUpdatedPersonaNameAndGreetingList()
    {
        Persona persona = new() { WakeName = "Nova", GreetingWords = new List<string> { "salutations" } };
        WakePhraseMatcher matcher = new(persona);

        Assert.False(matcher.TryMatch("Iris play music", out _));
        Assert.True(matcher.TryMatch("Nova, play music", out string request));
        Assert.Equal("play music", request);

        persona.WakeName = "Mira";
        persona.GreetingWords = new List<string> { "greetings" };
        matcher.Refresh(persona);

        Assert.False(matcher.TryMatch("Nova play music", out _));
        Assert.True(matcher.TryMatch("greetings Mira", out string greeting));
        Assert.Empty(greeting);
    }

    [Fact]
    public void MusicScanFindsSupportedTracksWithoutFollowingAnUnboundedTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-music-scan-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "Albums", "One");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "single.MP3"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(nested, "deep.flac"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(nested, "notes.txt"), Array.Empty<byte>());

            MusicLibraryScanResult scan = MusicLibraryScanner.Scan(new[] { root, root },
                timeBudget: TimeSpan.FromSeconds(2));

            Assert.Equal(2, scan.Tracks.Count);
            Assert.Equal(3, scan.DirectoriesVisited);
            Assert.Contains(scan.Tracks, path => path.EndsWith("single.MP3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(scan.Tracks, path => path.EndsWith("deep.flac", StringComparison.OrdinalIgnoreCase));
            Assert.False(scan.WasLimited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MusicScanReportsTrackAndFolderLimitsInsteadOfClaimingACompleteLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-music-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.mp3"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(root, "b.wav"), Array.Empty<byte>());
            Directory.CreateDirectory(Path.Combine(root, "one"));
            Directory.CreateDirectory(Path.Combine(root, "two"));

            MusicLibraryScanResult trackLimited = MusicLibraryScanner.Scan(new[] { root },
                maximumTracks: 1, timeBudget: TimeSpan.FromSeconds(2));
            MusicLibraryScanResult folderLimited = MusicLibraryScanner.Scan(new[] { root },
                maximumDirectories: 1, timeBudget: TimeSpan.FromSeconds(2));

            Assert.Single(trackLimited.Tracks);
            Assert.True(trackLimited.WasLimited);
            Assert.Contains("track limit", trackLimited.LimitReason);
            Assert.Equal(1, folderLimited.DirectoriesVisited);
            Assert.True(folderLimited.WasLimited);
            Assert.Contains("folder limit", folderLimited.LimitReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MusicScanHonorsCancellationWithoutReturningAFalseSuccess()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MusicLibraryScanner.Scan(
            new[] { Path.GetTempPath() }, cancellation.Token));
    }

    [Fact]
    public void MusicScanReportsDisconnectedRootsAsUnavailable()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"iris-missing-music-{Guid.NewGuid():N}");

        MusicLibraryScanResult scan = MusicLibraryScanner.Scan(new[] { missing },
            timeBudget: TimeSpan.FromSeconds(1));

        Assert.Empty(scan.Tracks);
        Assert.Equal(1, scan.UnavailableItems);
        Assert.False(scan.WasLimited);
    }

    [Fact]
    public void MusicScanBoundsEntriesRootsAndElapsedBudget()
    {
        string first = Path.Combine(Path.GetTempPath(), $"iris-music-bounds-a-{Guid.NewGuid():N}");
        string second = Path.Combine(Path.GetTempPath(), $"iris-music-bounds-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllBytes(Path.Combine(first, "a.mp3"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(first, "b.mp3"), Array.Empty<byte>());

            MusicLibraryScanResult entries = MusicLibraryScanner.Scan(new[] { first }, maximumEntries: 1,
                timeBudget: TimeSpan.FromSeconds(1));
            MusicLibraryScanResult roots = MusicLibraryScanner.Scan(new[] { first, second }, maximumRoots: 1,
                timeBudget: TimeSpan.FromSeconds(1));
            MusicLibraryScanResult time = MusicLibraryScanner.Scan(new[] { first },
                timeBudget: TimeSpan.FromTicks(1));

            Assert.True(entries.WasLimited);
            Assert.Contains("entry limit", entries.LimitReason);
            Assert.True(roots.WasLimited);
            Assert.Contains("root limit", roots.LimitReason);
            Assert.True(time.WasLimited);
            Assert.Contains("time budget", time.LimitReason);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void MusicPermissionDescriptionsNameTheResolvedFolderAndDoNotEchoControlCharacters()
    {
        using MusicPlayerModule module = new();
        ModuleTool browseTool = module.Tools.Single(tool => tool.Name == "browse_music_folder");
        ModuleTool addTool = module.Tools.Single(tool => tool.Name == "add_music_folder");
        using JsonDocument browseArguments = JsonDocument.Parse("{\"path\":\"Downloads\\\\New Music\"}");
        using JsonDocument addArguments = JsonDocument.Parse("{\"path\":\"C:\\\\Music\\n'quoted'\"}");

        string browseDescription = module.DescribeAction(browseTool, browseArguments.RootElement);
        string addDescription = module.DescribeAction(addTool, addArguments.RootElement);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "New Music");

        Assert.Contains(downloads, browseDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", browseDescription);
        Assert.Contains("C:\\Music", addDescription);
        Assert.DoesNotContain('\n', addDescription);
        Assert.DoesNotContain("'quoted'", addDescription);
        Assert.Contains("no source files will be changed", addDescription);
    }

    [Fact]
    public void RollingDiagnosticsStayBoundedAndKeepTheMostRecentPreviousSegment()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"iris-log-test-{Guid.NewGuid():N}");
        try
        {
            RollingJsonLinesWriter writer = new(directory, "test", 128);
            DateTimeOffset day = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            string payload = new string('x', 32);
            writer.Append(day, $"{{\"n\":1,\"p\":\"{payload}\"}}");
            writer.Append(day, $"{{\"n\":2,\"p\":\"{payload}\"}}");
            writer.Append(day, $"{{\"n\":3,\"p\":\"{payload}\"}}");
            writer.Append(day, $"{{\"n\":4,\"p\":\"{payload}\"}}");

            string active = writer.ActivePath;
            string previous = Path.Combine(directory, "test-20261025.previous.jsonl");
            Assert.True(File.Exists(active));
            Assert.True(File.Exists(previous));
            Assert.InRange(new FileInfo(active).Length, 1, 128);
            Assert.InRange(new FileInfo(previous).Length, 1, 128);
            Assert.Equal("4", ReadEventNumber(active));
            Assert.Equal("2", ReadEventNumber(previous));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RollingDiagnosticsRejectAnOversizedSingleEvent()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"iris-log-test-{Guid.NewGuid():N}");
        try
        {
            RollingJsonLinesWriter writer = new(directory, "test", 128);
            Assert.Throws<InvalidDataException>(() => writer.Append(DateTimeOffset.Now, "{\"payload\":\"" + new string('x', 256) + "\"}"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BehaviorEventsKeepCorrelationAcrossAsyncWorkAndRestoreNestedScopes()
    {
        string correlationId = Guid.NewGuid().ToString("N")[..8];
        List<EventRecord> captured = new();
        void Capture(EventRecord record)
        {
            if (record.Event.StartsWith("correlation_test", StringComparison.Ordinal))
            {
                captured.Add(record);
            }
        }

        IrisEvent.EventLogged += Capture;
        try
        {
            using (IrisEvent.BeginCorrelationScope(correlationId))
            {
                IrisEvent.Log("Test", "correlation_test.before_await");
                await Task.Yield();
                IrisEvent.Log("Test", "correlation_test.after_await");
                await Task.Run(async () =>
                {
                    await Task.Yield();
                    IrisEvent.Log("Test", "correlation_test.background_flow");
                });
                IrisEvent.Log(EventRecord.Capture("Test", "correlation_test.direct_record",
                    LogLevel.Info, null, null, null));

                using (IrisEvent.BeginCorrelationScope("nested-scope"))
                {
                    IrisEvent.Log("Test", "correlation_test.nested");
                }

                IrisEvent.Log("Test", "correlation_test.restored");
            }

            IrisEvent.Log("Test", "correlation_test.outside");
        }
        finally
        {
            IrisEvent.EventLogged -= Capture;
        }

        Assert.Collection(captured,
            before => Assert.Equal(correlationId, before.CorrelationId),
            after => Assert.Equal(correlationId, after.CorrelationId),
            background => Assert.Equal(correlationId, background.CorrelationId),
            direct => Assert.Equal(correlationId, direct.CorrelationId),
            nested => Assert.Equal("nested-scope", nested.CorrelationId),
            restored => Assert.Equal(correlationId, restored.CorrelationId),
            outside => Assert.Null(outside.CorrelationId));

        using JsonDocument json = JsonDocument.Parse(captured[0].ToJsonLine());
        Assert.Equal(correlationId, json.RootElement.GetProperty("correlation_id").GetString());
    }

    [Fact]
    public void RollingDiagnosticsExpireFilesOutsideTheSevenDayWindow()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"iris-log-test-{Guid.NewGuid():N}");
        try
        {
            RollingJsonLinesWriter writer = new(directory, "test", 256);
            DateTimeOffset today = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            writer.Append(today.AddDays(-8), "{\"day\":\"expired\"}");
            writer.Append(today.AddDays(-6), "{\"day\":\"keep\"}");
            writer.Append(today, "{\"day\":\"today\"}");

            Assert.False(File.Exists(Path.Combine(directory, "test-20261017.jsonl")));
            Assert.True(File.Exists(Path.Combine(directory, "test-20261019.jsonl")));
            Assert.True(File.Exists(Path.Combine(directory, "test-20261025.jsonl")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReadEventNumber(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllLines(path)[^1]);
        return json.RootElement.GetProperty("n").GetInt32().ToString();
    }

    [Fact]
    public void PassiveReadinessDoesNotRequireModelWeightsForAnExternalBrain()
    {
        CompanionSettings settings = new();
        settings.Brain.ActiveProfileId = "external";
        settings.Ears.Enabled = false;
        settings.Music.SearchDirectories.Clear();

        VoiceReadiness voice = new()
        {
            EarsEnabled = false,
            IsMicrophoneConfigured = false,
            MicrophoneDetail = "No recording device detected",
            IsRecognizerAvailable = false,
            RecognizerDetail = "Offline recognizer unavailable",
            IsVoiceEngineOnline = false,
            VoiceEngineDetail = "No verified voice engine",
            IsOutputConfigured = true,
            OutputDetail = "Windows default output"
        };

        CompanionReadiness readiness = CompanionReadinessProbe.Read(settings, voice);

        Assert.True(readiness.Brain.IsReady);
        Assert.True(readiness.Hearing.IsReady);
        Assert.False(readiness.Voice.IsReady);
        Assert.False(readiness.Music.IsReady);
        Assert.True(readiness.Memory.IsReady);
    }

    [Fact]
    public void PassiveReadinessReportsAMissingManagedBrainWithoutStartingIt()
    {
        CompanionSettings settings = new();
        settings.Brain.ActiveProfileId = "vision-agent";
        settings.ActiveBrainProfile.ModelFile = $"missing-{Guid.NewGuid():N}.gguf";

        VoiceReadiness voice = new()
        {
            EarsEnabled = true,
            IsMicrophoneConfigured = true,
            MicrophoneDetail = "Windows default microphone",
            IsRecognizerAvailable = true,
            RecognizerDetail = "Offline recognizer is available",
            IsVoiceEngineOnline = true,
            VoiceEngineDetail = "Voice engine connected",
            IsOutputConfigured = true,
            OutputDetail = "Windows default output"
        };

        CompanionReadiness readiness = CompanionReadinessProbe.Read(settings, voice);

        Assert.False(readiness.Brain.IsReady);
        Assert.Contains("Missing model", readiness.Brain.Detail);
    }

    [Fact]
    public async Task EmptyAudioClipIsRejectedWithoutOpeningAnOutputDevice()
    {
        using SpeechAudioPlayer player = new();
        SpeechAudio empty = new() { Bytes = Array.Empty<byte>(), Format = "wav" };

        bool played = await player.PlayAsync(empty, 1.0, CancellationToken.None);

        Assert.False(played);
    }

    [Fact]
    public void PocketVoicePickerKeepsSixLocalProfilesWithoutStartingTheEngine()
    {
        CompanionSettings settings = new();
        VoiceGallery gallery = new(settings);

        IReadOnlyList<VoiceCandidate> candidates = gallery.DescribeCandidates();

        Assert.Equal(new[] { "alba", "anna", "eve", "mary", "vera", "estelle" },
            candidates.Select(candidate => candidate.VoiceId));
        Assert.All(candidates, candidate => Assert.Equal("pocket", candidate.Engine));
        Assert.Equal("pocket", VoiceProfile.FromSettings(settings).Engine);
        Assert.Equal("alba", VoiceProfile.FromSettings(settings).VoiceId);
    }

    [Fact]
    public void PocketVoiceGalleryProfilesMatchTheConfiguredPocketEngine()
    {
        CompanionSettings settings = new();
        settings.Voice.Engine = "pocket";
        settings.Voice.CandidateVoices = new() { "alba", "anna", "eve", "mary", "vera", "estelle" };
        using VoiceGallery gallery = new(settings);

        IReadOnlyList<VoiceCandidate> candidates = gallery.DescribeCandidates();

        Assert.Equal(settings.Voice.CandidateVoices, candidates.Select(candidate => candidate.VoiceId).ToArray());
        Assert.All(candidates, candidate => Assert.Equal("pocket", candidate.Engine));
        Assert.All(candidates, candidate => Assert.Equal(candidate.VoiceId, gallery.BuildProfile(candidate).VoiceId));
    }

    [Fact]
    public async Task PocketTtsMissingRuntimeFailsWithoutStartingAProcess()
    {
        VoiceSettings settings = new()
        {
            Engine = "pocket",
            PocketExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-pocket-{Guid.NewGuid():N}.exe")
        };
        await using PocketTtsServerHost host = new(settings);

        bool started = await host.StartAsync();

        Assert.False(started);
    }

    [Fact]
    public void DefaultEarsUseAModerateThresholdAndTwoRecognitionThreads()
    {
        EarsSettings ears = new();
        using WhisperNetRecognizer recognizer = new("missing-whisper-model.bin", "auto",
            ears.MaxRecognitionThreads);

        Assert.Equal(0.025, ears.VoiceThreshold);
        Assert.Equal(2, recognizer.ThreadCount);
        Assert.False(recognizer.IsAvailable);
    }

    [Fact]
    public void MicrophoneSignalAssessmentDoesNotCallSilenceOrDisconnectAHealthyTest()
    {
        VoiceDiagnosticResult noDevice = MicrophoneSignalAssessment.Evaluate("USB microphone", false, 0.2);
        VoiceDiagnosticResult silent = MicrophoneSignalAssessment.Evaluate("USB microphone", true, 0.001);
        VoiceDiagnosticResult speaking = MicrophoneSignalAssessment.Evaluate("USB microphone", true, 0.04);
        VoiceDiagnosticResult invalid = MicrophoneSignalAssessment.Evaluate("USB microphone", true, double.NaN);

        Assert.False(noDevice.Succeeded);
        Assert.Contains("did not keep", noDevice.Detail);
        Assert.False(silent.Succeeded);
        Assert.Contains("no clear input signal", silent.Detail);
        Assert.True(speaking.Succeeded);
        Assert.Contains("RMS level", speaking.Detail);
        Assert.False(invalid.Succeeded);
    }

    [Fact]
    public void FormerDefaultHttpVoiceMigratesToPocketAndRequiresVoiceConfirmation()
    {
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"Voice\":{\"Engine\":\"http\",\"VoiceId\":\"af_heart\"," +
                "\"HttpEndpoint\":\"http://127.0.0.1:8880/v1\",\"ChosenByUser\":true}," +
                "\"Ears\":{\"VoiceThreshold\":0.055,\"MaxRecognitionThreads\":0}}");

            CompanionSettings loaded = new SettingsStore().Load();

            Assert.Equal("pocket", loaded.Voice.Engine);
            Assert.Equal("alba", loaded.Voice.VoiceId);
            Assert.False(loaded.Voice.ChosenByUser);
            Assert.Equal(6, loaded.Voice.CandidateVoices.Count);
            Assert.Equal(0.025, loaded.Ears.VoiceThreshold);
            Assert.Equal(2, loaded.Ears.MaxRecognitionThreads);
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
    public async Task LocalPocketTtsIntegrationSmokeTest()
    {
        // Model-backed integration stays opt-in so ordinary unit tests never download or
        // start a speech model. It exercises the real managed sidecar without launching Iris.
        if (!string.Equals(Environment.GetEnvironmentVariable("IRIS_RUN_LOCAL_TTS_INTEGRATION"), "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        VoiceSettings settings = new()
        {
            Engine = "pocket",
            VoiceId = "alba",
            PocketEndpoint = $"http://127.0.0.1:{port}",
            PocketMaxThreads = 2,
            PocketLanguage = "english_18m_2026-10"
        };

        await using PocketTtsServerHost host = new(settings);
        using PocketTtsSpeechSynthesizer synthesizer = new(settings);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Assert.True(await host.StartAsync(timeout.Token));

        foreach (string voiceId in new[] { "alba", "anna", "eve", "mary", "vera", "estelle" })
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            SpeechAudio? audio = await synthesizer.SynthesizeAsync(
                "Iris here. I found your music folder. Want me to play it?",
                new VoiceProfile { Engine = "pocket", VoiceId = voiceId }, timeout.Token);
            stopwatch.Stop();

            Assert.NotNull(audio);
            Assert.Equal("wav", audio.Format);
            Assert.True(audio.Bytes.Length > 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio.Bytes, 0, 4));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Warm speech request for '{voiceId}' took {stopwatch.Elapsed.TotalSeconds:0.00} seconds.");
        }
    }

    [Fact]
    public void ModeTransitionPublishesTheDerivedMindState()
    {
        MindStateMachine machine = new();
        List<MindState> states = new();
        machine.StateChanged += (_, current, _) => states.Add(current);

        machine.MoveTo(MindState.Thinking, "test");
        machine.MoveModeTo(OperatingMode.Gaming, "test");

        Assert.Equal(MindState.Gaming, machine.CurrentState);
        Assert.Equal(new[] { MindState.Thinking, MindState.Gaming }, states);
    }

    [Fact]
    public void ServerArgumentsKeepQuotedValuesTogether()
    {
        List<string> arguments = LlamaServerHost.SplitArguments(
            "--model \"C:\\Models\\brain file.gguf\" --threads 6 --flash-attn on");

        Assert.Equal(
            new[] { "--model", "C:\\Models\\brain file.gguf", "--threads", "6", "--flash-attn", "on" },
            arguments);
    }

    [Fact]
    public async Task ForgetNodeRemovesTheNodeAndItsLinksAtomically()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"iris-test-{Guid.NewGuid():N}.db");
        try
        {
            await using MemoryGraph graph = new(databasePath);
            await graph.InitializeAsync();
            MemoryNode subject = await graph.UpsertNodeAsync("subject", "", MemoryLayer.Semantic, "test");
            MemoryNode target = await graph.UpsertNodeAsync("target", "", MemoryLayer.Semantic, "test");
            await graph.LinkAsync(subject.Id, target.Id, SynapseType.RelatedTo, 0.7, "test");

            await graph.ForgetNodeAsync(subject.Id);

            Assert.Null(graph.FindById(subject.Id));
            Assert.Empty(graph.SnapshotSynapses());
            Assert.NotNull(graph.FindById(target.Id));
        }
        finally
        {
            foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // The test result is already decided; cleanup is best effort.
                }
            }
        }
    }

    [Fact]
    public async Task ForgetMemoryRequiresAnExactTitleAndNeverRemovesASimilarMemory()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"iris-test-{Guid.NewGuid():N}.db");
        try
        {
            await using MemoryGraph graph = new(databasePath);
            await graph.InitializeAsync();
            SynapticMemoryEngine memory = new(graph, new SettingsStore(), new EventBus());
            await memory.RememberAsync(new RememberRequest
            {
                Subject = "Boss",
                Relation = "likes",
                Target = "Synthwave",
                Provenance = "test"
            }, CancellationToken.None);

            MemoryForgetResult missed = await memory.ForgetAsync("Synth", CancellationToken.None);
            Assert.False(missed.Succeeded);
            Assert.NotNull(graph.FindByTitle("Synthwave"));

            MemoryForgetResult forgotten = await memory.ForgetAsync("Synthwave", CancellationToken.None);
            Assert.True(forgotten.Succeeded);
            Assert.Null(graph.FindByTitle("Synthwave"));
            Assert.NotNull(graph.FindByTitle("Boss"));
        }
        finally
        {
            foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Test cleanup is best effort.
                }
            }
        }
    }
}
