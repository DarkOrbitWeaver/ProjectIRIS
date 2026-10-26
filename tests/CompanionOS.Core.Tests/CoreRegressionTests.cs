using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Voice;
using CompanionOS.Core.Modules.BuiltIn;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Plugins;
using CompanionOS.Core.Recovery;
using CompanionOS.Modules;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CompanionOS.Core.Tests;

public sealed class CoreRegressionTests
{
    [Fact]
    public async Task SpeechOutputArbiterSerializesConcurrentCallers()
    {
        SpeechOutputArbiter arbiter = new();
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;

        Task first = arbiter.RunAsync(async _ =>
        {
            int current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            firstEntered.TrySetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref active);
        }, CancellationToken.None);

        await firstEntered.Task;
        Task second = arbiter.RunAsync(_ =>
        {
            int current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            Interlocked.Decrement(ref active);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(second.IsCompleted);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task SpeechOutputStopCancelsActiveAndQueuedJobsButAllowsLaterSpeech()
    {
        SpeechOutputArbiter arbiter = new();
        TaskCompletionSource activeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = arbiter.RunAsync(async token =>
        {
            activeEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None);
        await activeEntered.Task;

        bool queuedRan = false;
        Task queued = arbiter.RunAsync(_ =>
        {
            queuedRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        arbiter.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(queuedRan);

        bool nextRequestRan = false;
        await arbiter.RunAsync(_ =>
        {
            nextRequestRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(nextRequestRan);
    }

    [Fact]
    public async Task SpeechOutputArbiterCancelsQueuedAndActiveWorkDuringShutdown()
    {
        SpeechOutputArbiter arbiter = new();
        TaskCompletionSource activeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = arbiter.RunAsync(async token =>
        {
            activeEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None);
        await activeEntered.Task;

        using CancellationTokenSource queuedCancellation = new();
        Task queued = arbiter.RunAsync(_ => Task.CompletedTask, queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        arbiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await arbiter.DrainAsync();
    }

    [Fact]
    public void ToolArgumentsRejectMalformedJsonAndNonObjectValues()
    {
        Assert.False(ToolArgumentsParser.TryParse("{\"path\":", out _));
        Assert.False(ToolArgumentsParser.TryParse("[]", out _));
        Assert.False(ToolArgumentsParser.TryParse("null", out _));
    }

    [Fact]
    public void ToolArgumentsAcceptObjectAndEmptyNoArgumentPayloads()
    {
        Assert.True(ToolArgumentsParser.TryParse("{\"path\":\"music\"}", out JsonElement arguments));
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("music", arguments.GetProperty("path").GetString());

        Assert.True(ToolArgumentsParser.TryParse(" ", out JsonElement emptyArguments));
        Assert.Equal(JsonValueKind.Object, emptyArguments.ValueKind);
        Assert.Empty(emptyArguments.EnumerateObject());
    }

    [Fact]
    public void ModuleToolSchemaRejectsMissingOrWrongArgumentsBeforeExecution()
    {
        using JsonDocument missing = JsonDocument.Parse("{\"minutes\":\"five\"}");
        const string schema = """{"type":"object","properties":{"minutes":{"type":"number","minimum":1},"label":{"type":"string","enum":["tea","break"]}},"required":["minutes"],"additionalProperties":false}""";

        Assert.False(ToolSchemaValidator.TryValidate(missing.RootElement, schema, out string reason));
        Assert.Contains("wrong type", reason);

        using JsonDocument absent = JsonDocument.Parse("{}");
        Assert.False(ToolSchemaValidator.TryValidate(absent.RootElement, schema, out reason));
        Assert.Contains("required", reason);
    }

    [Fact]
    public void ModuleToolSchemaValidatesNestedValuesEnumsAndRejectsUnknownFields()
    {
        const string schema = """{"type":"object","properties":{"profile":{"type":"object","properties":{"voice":{"type":"string","enum":["a","b"]}},"required":["voice"],"additionalProperties":false}},"required":["profile"],"additionalProperties":false}""";

        using JsonDocument valid = JsonDocument.Parse("{\"profile\":{\"voice\":\"b\"}}");
        Assert.True(ToolSchemaValidator.TryValidate(valid.RootElement, schema, out _));

        using JsonDocument invalidEnum = JsonDocument.Parse("{\"profile\":{\"voice\":\"c\"}}");
        Assert.False(ToolSchemaValidator.TryValidate(invalidEnum.RootElement, schema, out _));

        using JsonDocument unexpected = JsonDocument.Parse("{\"profile\":{\"voice\":\"a\"},\"extra\":true}");
        Assert.False(ToolSchemaValidator.TryValidate(unexpected.RootElement, schema, out _));

        using JsonDocument unsupported = JsonDocument.Parse("{\"value\":\"x\"}");
        Assert.False(ToolSchemaValidator.TryValidate(unsupported.RootElement,
            """{"type":"object","properties":{"value":{"$ref":"#/definitions/value"}}}""", out string reason));
        Assert.Contains("unsupported keyword", reason);
    }

    [Fact]
    public async Task VisionOffCannotBeOverriddenByForcedCapture()
    {
        SettingsStore settings = new();
        settings.Current.Senses.VisionMode = "off";
        EventBus events = new();
        List<SensesEvent> senseEvents = [];
        using IDisposable subscription = events.Subscribe<SensesEvent>(senseEvents.Add);
        using PerceptionEngine perception = new(settings, events);

        ScreenFrame? frame = await perception.LookAsync(forceFreshCapture: true);

        Assert.Null(frame);
        Assert.Contains(senseEvents, senseEvent =>
            senseEvent.SenseName == "vision" && senseEvent.Summary.Contains("switched off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SituationPromptSeparatesVisualConfigurationFromObservedImages()
    {
        SettingsStore settings = new();
        settings.Current.Senses.VisionMode = "ondemand";
        using PerceptionEngine perception = new(settings, new EventBus());

        string withoutImage = perception.DescribeForPrompt();
        string withImage = perception.DescribeForPrompt(userAttachedImage: true);

        Assert.Contains("Screen capture setting: ondemand", withoutImage);
        Assert.Contains("No image is attached", withoutImage);
        Assert.Contains("only after a successful screen tool result", withoutImage);
        Assert.Contains("A user-supplied image is attached to this turn", withImage);
        Assert.Contains("does not provide continuous screen access", withImage);
    }

    [Fact]
    public void DefaultPersonaDoesNotClaimPassiveScreenAccess()
    {
        string prompt = Persona.CreateDefault().BuildSystemPrompt(
            "Screen capture setting: ondemand.", "", "", "awake", "Monday 12:00");

        Assert.DoesNotContain("you can reason, see the screen and act", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the desktop is not continuously visible to you", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when an image is attached to the current message", prompt,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SituationPromptMarksAmbientStateUnknownUntilItHasBeenObserved()
    {
        SettingsStore settings = new();
        using PerceptionEngine perception = new(settings, new EventBus());

        string prompt = perception.DescribeForPrompt();

        Assert.Contains("Ambient machine state is unknown", prompt);
        Assert.DoesNotContain("Foreground:", prompt);
        Assert.DoesNotContain("Machine: CPU", prompt);
    }

    [Fact]
    public void PerceptionHealthSeparatesDisabledSourcesFromUnobservedSources()
    {
        SettingsStore settings = new();
        settings.Current.Senses.WatchSystemStats = false;
        settings.Current.Senses.VisionMode = "ondemand";
        using PerceptionEngine perception = new(settings, new EventBus());

        IReadOnlyDictionary<string, PerceptionSourceHealth> health =
            perception.GetSourceHealth().ToDictionary(source => source.Name);

        Assert.Equal("Disabled", health["CPU load"].Status);
        Assert.Equal("Disabled", health["Memory"].Status);
        Assert.Equal("Stopped", health["Media session"].Status);
        Assert.Equal("On demand", health["Screen vision"].Status);
        Assert.Contains("No continuous screen access", health["Screen vision"].Detail);
    }

    [Fact]
    public void PromptContextReturnsTheSourceSnapshotUsedForItsTurn()
    {
        SettingsStore settings = new();
        settings.Current.Senses.WatchSystemStats = false;
        settings.Current.Senses.WatchMediaSession = false;
        settings.Current.Senses.WatchIdleState = false;
        settings.Current.Senses.WatchActiveWindow = false;
        settings.Current.Senses.VisionMode = "off";
        using PerceptionEngine perception = new(settings, new EventBus());

        string prompt = perception.DescribeForPrompt(false, out IReadOnlyList<PerceptionSourceHealth> sources);
        IReadOnlyDictionary<string, PerceptionSourceHealth> health =
            sources.ToDictionary(source => source.Name);

        Assert.Contains("System metrics sensing is disabled", prompt);
        Assert.Equal("Disabled", health["CPU load"].Status);
        Assert.Equal("Disabled", health["Screen vision"].Status);
    }

    [Fact]
    public void TurnJournalTreatsCancellationAsTerminalAndKeepsLaterCrashRecoverable()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TurnJournal journal = new(Path.Combine(root, "turns.jsonl"));
            journal.RecordUserMessage("cancelled-turn", "stop this");
            journal.RecordAnswerCancelled("cancelled-turn");

            Assert.False(journal.Analyse().HadIncompleteTurn);
            journal.RecordBackgroundTaskState("task-active", "plugin:telegram", "send_message", "Running");
            journal.RecordBackgroundTaskState("task-finished", "plugin:web", "search", "Running");
            journal.RecordBackgroundTaskState("task-finished", "plugin:web", "search", "Completed");
            JournalRecovery backgroundRecovery = journal.Analyse();
            Assert.False(backgroundRecovery.HadIncompleteTurn);
            Assert.Equal("task-active", Assert.Single(backgroundRecovery.InterruptedBackgroundTasks).TaskId);

            journal.RecordUserMessage("interrupted-turn", "continue later");
            journal.RecordAnswerProgress("interrupted-turn", "partial reply");
            string completedOperation = journal.RecordToolCall("interrupted-turn", "music", "add_to_playlist");
            journal.RecordToolResult("interrupted-turn", completedOperation, JournalToolStatus.Succeeded);
            string uncertainOperation = journal.RecordToolCall("interrupted-turn", "telegram", "send_message");
            string backgroundOperation = journal.RecordToolCall("interrupted-turn", "web", "search");
            journal.RecordToolResult("interrupted-turn", backgroundOperation,
                JournalToolStatus.BackgroundStarted, "background-task-7");
            JournalRecovery recovery = journal.Analyse();

            Assert.True(recovery.HadIncompleteTurn);
            Assert.Equal("interrupted-turn", recovery.InterruptedTurnId);
            Assert.Equal("continue later", recovery.InterruptedUserMessage);
            Assert.Equal("partial reply", recovery.PartialAnswer);
            Assert.Collection(recovery.InterruptedTools,
                tool => Assert.Equal(JournalToolStatus.Succeeded, tool.Status),
                tool =>
                {
                    Assert.Equal("telegram/send_message", tool.Tool);
                    Assert.Equal(uncertainOperation, tool.OperationId);
                    Assert.Equal(JournalToolStatus.Unknown, tool.Status);
                },
                tool =>
                {
                    Assert.Equal(JournalToolStatus.BackgroundStarted, tool.Status);
                    Assert.Equal("background-task-7", tool.BackgroundTaskId);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SituationPromptStopsPresentingAmbientValuesAsCurrentAfterSensingStops()
    {
        SettingsStore settings = new();
        settings.Current.Senses.WatchMediaSession = false;
        using PerceptionEngine perception = new(settings, new EventBus());

        perception.Start();
        string runningPrompt = perception.DescribeForPrompt();
        perception.Stop();
        string stoppedPrompt = perception.DescribeForPrompt();

        Assert.Contains("Ambient machine state was checked", runningPrompt);
        Assert.Contains("Ambient sensing is stopped", stoppedPrompt);
        Assert.DoesNotContain("Foreground:", stoppedPrompt);
        Assert.DoesNotContain("Machine: CPU", stoppedPrompt);
    }

    [Fact]
    public async Task FailedMediaProbeDoesNotPresentThePreviousTrackAsCurrent()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new(
            new MediaSessionReadResult(true, new MediaSessionSnapshot
            {
                Title = "Last known track",
                Artist = "Known artist",
                PlaybackStatus = "Playing"
            }),
            new MediaSessionReadResult(false, null, "simulated media service failure"));
        using PerceptionEngine perception = new(settings, new EventBus(), media);
        TaskCompletionSource mediaUnavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        perception.SituationUpdated += snapshot =>
        {
            if (snapshot.MediaObservedAt is not null && !snapshot.MediaObservationAvailable)
            {
                mediaUnavailable.TrySetResult();
            }
        };

        perception.Start();
        await mediaUnavailable.Task.WaitAsync(TimeSpan.FromSeconds(6));

        string prompt = perception.DescribeForPrompt();
        Assert.Contains("Windows media session information is unavailable", prompt);
        Assert.DoesNotContain("Last known track", prompt);
        Assert.Equal("Unavailable", Assert.Single(perception.GetSourceHealth(),
            source => source.Name == "Media session").Status);
    }

    [Fact]
    public async Task SuccessfulMediaProbeReportsWhenNoSessionIsPlaying()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new(new MediaSessionReadResult(true, null));
        using PerceptionEngine perception = new(settings, new EventBus(), media);
        TaskCompletionSource noSessionObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        perception.SituationUpdated += snapshot =>
        {
            if (snapshot.MediaObservedAt is not null && snapshot.MediaObservationAvailable &&
                !snapshot.MediaSessionPresent)
            {
                noSessionObserved.TrySetResult();
            }
        };

        perception.Start();
        await noSessionObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("No active Windows media session was found", perception.DescribeForPrompt());
        PerceptionSourceHealth mediaHealth = Assert.Single(perception.GetSourceHealth(),
            source => source.Name == "Media session");
        Assert.Equal("Available", mediaHealth.Status);
        Assert.Contains("No active Windows media session", mediaHealth.Detail);
    }

    [Fact]
    public async Task ConcurrentVoiceAttentionPausesMediaOnlyOnce()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new() { PauseResult = true, PauseDelayMilliseconds = 40 };
        using PerceptionEngine perception = new(settings, new EventBus(), media);

        bool[] results = await Task.WhenAll(
            perception.PauseMediaForVoiceAsync(),
            perception.PauseMediaForVoiceAsync());

        Assert.Equal(1, media.PauseCalls);
        Assert.Single(results, paused => paused);
        Assert.Single(results, paused => !paused);
    }

    [Fact]
    public async Task DisablingAutoResumeClearsPauseOwnershipForTheNextInteraction()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new() { PauseResult = true, PlayResult = true };
        using PerceptionEngine perception = new(settings, new EventBus(), media);

        Assert.True(await perception.PauseMediaForVoiceAsync());
        settings.Current.Voice.ResumeMediaAfterVoiceTurn = false;
        Assert.False(await perception.ResumeMediaAfterVoiceAsync());

        settings.Current.Voice.ResumeMediaAfterVoiceTurn = true;
        Assert.True(await perception.PauseMediaForVoiceAsync());
        Assert.True(await perception.ResumeMediaAfterVoiceAsync());
        Assert.Equal(2, media.PauseCalls);
        Assert.Equal(1, media.PlayCalls);
    }

    [Fact]
    public async Task CancelledResumeAttemptPreservesOwnershipForLaterRetry()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new() { PauseResult = true, PlayResult = true };
        using PerceptionEngine perception = new(settings, new EventBus(), media);
        Assert.True(await perception.PauseMediaForVoiceAsync());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            perception.ResumeMediaAfterVoiceAsync(cancellation.Token));

        Assert.True(await perception.ResumeMediaAfterVoiceAsync());
        Assert.Equal(1, media.PlayCalls);
    }

    [Fact]
    public async Task FailedMediaResumeSurfacesARecoveryNotice()
    {
        SettingsStore settings = CreatePerceptionTestSettings();
        FakeMediaSessionSensor media = new() { PauseResult = true, PlayResult = false };
        EventBus events = new();
        List<NoticeEvent> notices = [];
        using IDisposable subscription = events.Subscribe<NoticeEvent>(notices.Add);
        using PerceptionEngine perception = new(settings, events, media);

        Assert.True(await perception.PauseMediaForVoiceAsync());
        Assert.False(await perception.ResumeMediaAfterVoiceAsync());

        Assert.Contains(notices, notice => notice.Severity == NoticeSeverity.Warning &&
            notice.Message.Contains("media resumed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, media.PlayCalls);
    }

    private static SettingsStore CreatePerceptionTestSettings()
    {
        SettingsStore settings = new();
        settings.Current.Senses.StatsIntervalMilliseconds = 1000;
        settings.Current.Senses.WatchActiveWindow = false;
        settings.Current.Senses.WatchMediaSession = true;
        settings.Current.Senses.WatchSystemStats = false;
        settings.Current.Senses.WatchIdleState = false;
        settings.Current.System.GamingModeEnabled = false;
        settings.Current.System.GamingAutoDetect = false;
        return settings;
    }

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
    public void ExternalModuleManifestAcceptsSmallLocalPngBranding()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-branding-{Guid.NewGuid():N}");
        string assets = Path.Combine(folder, "assets");
        Directory.CreateDirectory(assets);
        try
        {
            File.WriteAllBytes(Path.Combine(assets, "brand.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"));
            File.WriteAllText(Path.Combine(folder, "plugin.json"), """
                {"schema":1,"id":"branded","name":"Branded","transport":"builtin","icon":"assets/brand.png"}
                """);

            bool loaded = PluginManifest.TryLoad(folder, out PluginManifest? manifest, out string reason);

            Assert.True(loaded, reason);
            Assert.NotNull(manifest);
            Assert.StartsWith("png:", manifest.ResolveIcon(manifest.Icon), StringComparison.Ordinal);
            using PluginHost host = new(new EventBus(), folder);
            PluginModuleAdapter adapter = new(host, manifest);
            Assert.Equal(manifest.ResolveIcon(manifest.Icon), adapter.IconKey);

            File.WriteAllText(Path.Combine(folder, "plugin.json"), """
                {"schema":1,"id":"branded","name":"Branded","transport":"builtin","icon":"../brand.png"}
                """);
            bool traversalLoaded = PluginManifest.TryLoad(folder, out _, out string traversalReason);
            Assert.False(traversalLoaded);
            Assert.Contains("safe PNG", traversalReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExternalModuleManifestAcceptsBoundedSidebarControlsAndModuleOverlays()
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
                    "background": true,
                    "schema": {"type":"object","properties":{"recipient":{"type":"string"},"message":{"type":"string"},"source":{"type":"string","enum":["recent","all"]}}}
                  }],
                  "surfaces": [{
                    "id": "compose",
                    "title": "New message",
                    "slot": "sidebar",
                    "icon": "telegram",
                    "elements": [
                      {"id":"recipient","type":"select","name":"recipient","label":"To","dynamic":true,"options":[]},
                      {"id":"message","type":"input","name":"message","label":"Message"},
                      {"id":"source","type":"select","name":"source","label":"Contact source","options":[{"value":"recent","label":"Recent"},{"value":"all","label":"All contacts"}]},
                      {"id":"send","type":"button","label":"Send","tool":"send_message"}
                    ]
                  }, {
                    "id": "activity",
                    "title": "Telegram activity",
                    "slot": "overlay",
                    "icon": "telegram",
                    "elements": [{"id":"progress","type":"text","label":"Sending message"}]
                  }, {
                    "id": "contacts",
                    "title": "Contact picker",
                    "slot": "overlay",
                    "icon": "telegram",
                    "elements": [{"id":"contact-list","type":"text","label":"Choose a contact"}]
                  }]
                }
                """);

            bool loaded = PluginManifest.TryLoad(folder, out PluginManifest? manifest, out string reason);

            Assert.True(loaded, reason);
            Assert.NotNull(manifest);
            Assert.Equal(3, manifest.Surfaces.Count);
            PluginSurfaceManifest surface = manifest.Surfaces[0];
            Assert.Equal("telegram", surface.Icon);
            Assert.Equal("sidebar", surface.Slot);
            Assert.Equal(4, surface.Elements.Count);
            Assert.True(surface.Elements[0].DynamicOptions);
            Assert.Equal("send_message", surface.Elements[^1].ToolName);
            Assert.Equal("recent", surface.Elements[2].Options[0].Value);
            Assert.Equal("overlay", manifest.Surfaces[1].Slot);
            Assert.Equal("overlay", manifest.Surfaces[2].Slot);
            using PluginHost host = new(new EventBus(), folder);
            PluginModuleAdapter adapter = new(host, manifest);
            Assert.Equal(ShellSlot.Overlay, adapter.GetSurfaces()[1].Slot);
            Assert.Equal(ShellSlot.Overlay, adapter.GetSurfaces()[2].Slot);
            Assert.True(Assert.Single(adapter.Tools).BackgroundCapable);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BackgroundModuleTaskCanBeCancelledAndReportsTerminalState()
    {
        EventBus events = new();
        await using BackgroundTaskManager manager = new(events);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string id = manager.Start("plugin:sample", "Sample", "search", "scan", async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ToolResult.Ok("unexpected completion");
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(manager.Cancel(id, out _));

        BackgroundTaskSnapshot? finished = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            finished = manager.Find(id);
            if (finished?.State == BackgroundTaskState.Cancelled)
            {
                break;
            }
            await Task.Delay(10);
        }

        Assert.NotNull(finished);
        Assert.Equal(BackgroundTaskState.Cancelled, finished.State);
        Assert.Equal("search", finished.IconKey);
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
    public async Task CapabilityGrantsPersistPerModuleAndScopeAndCanBeRevokedIndependently()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"iris-scope-grants-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        string grantsPath = Path.Combine(temp, "permissions.json");
        File.WriteAllText(grantsPath, "[{\"ModuleId\":\"plugin:telegram\",\"ToolName\":\"legacy_tool\"}]");

        try
        {
            EventBus events = new();
            PermissionGrantStore grants = new(grantsPath);
            SandboxGuard guard = new(events, new FileAuditTrail(Path.Combine(temp, "audit.jsonl")), grants);
            int promptCount = 0;
            bool allowAlwaysWasOfferedForSend = true;
            using IDisposable subscription = events.Subscribe<PermissionRequestedEvent>(payload =>
            {
                promptCount++;
                if (payload.Request.PermissionScope == "messages.send")
                {
                    allowAlwaysWasOfferedForSend &= payload.Request.CanAlwaysAllow;
                    payload.Request.Answer(PermissionDecision.AllowAlways);
                }
                else
                {
                    payload.Request.Answer(PermissionDecision.AllowAlways);
                }
            });

            ModuleTool readContact = new("read_contact", "Read a contact.", ToolTier.SystemCritical,
                "{}") { PermissionScope = "contacts.read" };
            ModuleTool listContacts = new("list_contacts", "List contacts.", ToolTier.SystemCritical,
                "{}") { PermissionScope = "contacts.read" };
            ModuleTool sendMessage = new("send_message", "Send a message.", ToolTier.SystemCritical,
                "{}") { PermissionScope = "messages.send", AlwaysConfirm = true };

            Assert.True(await guard.AuthorizeAsync("plugin:telegram", "Telegram", readContact,
                "Telegram wants to read Alex's contact.", CancellationToken.None));
            Assert.True(grants.IsScopeGranted("plugin:telegram", "contacts.read"));
            Assert.False(grants.IsScopeGranted("plugin:telegram", "messages.send"));
            Assert.True(await guard.AuthorizeAsync("plugin:telegram", "Telegram", listContacts,
                "Telegram wants to list contacts.", CancellationToken.None));
            Assert.Equal(1, promptCount);
            Assert.True(await guard.AuthorizeAsync("plugin:telegram", "Telegram", sendMessage,
                "Telegram wants to send a message.", CancellationToken.None));
            Assert.Equal(2, promptCount);
            Assert.False(grants.IsScopeGranted("plugin:telegram", "messages.send"));
            Assert.False(allowAlwaysWasOfferedForSend);
            Assert.True(await guard.AuthorizeAsync("plugin:telegram", "Telegram", sendMessage,
                "Telegram wants to send a second message.", CancellationToken.None));
            Assert.Equal(3, promptCount);
            Assert.False(grants.IsScopeGranted("plugin:telegram", "messages.send"));

            PermissionGrantStore reloaded = new(grantsPath);
            Assert.Contains(reloaded.ListAll(), grant =>
                grant.ModuleId == "plugin:telegram" && grant.ToolName == "legacy_tool");
            Assert.Contains(reloaded.ListScopeGrants(), grant =>
                grant.ModuleId == "plugin:telegram" && grant.PermissionScope == "contacts.read");
            reloaded.RevokeScope("plugin:telegram", "contacts.read");

            PermissionGrantStore afterRevoke = new(grantsPath);
            Assert.False(afterRevoke.IsScopeGranted("plugin:telegram", "contacts.read"));
            Assert.True(afterRevoke.IsGranted("plugin:telegram", "legacy_tool"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ModuleSecretStoreEncryptsAndSeparatesCredentialsByModule()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-module-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "module-secrets.json");
        const string secret = "telegram-token-123456789";
        try
        {
            ModuleSecretStore store = new(path);
            store.Set("telegram", "bot_token", secret);

            Assert.True(store.Contains("telegram", "bot_token"));
            Assert.Equal(secret, store.Get("telegram", "bot_token"));
            Assert.Null(store.Get("music", "bot_token"));
            Assert.DoesNotContain(secret, File.ReadAllText(path), StringComparison.Ordinal);

            ModuleSecretStore reopened = new(path);
            Assert.Equal(secret, reopened.Get("telegram", "bot_token"));
            Assert.True(reopened.Remove("telegram", "bot_token"));
            Assert.False(store.Contains("telegram", "bot_token"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
    public void ExternalModuleManifestLimitsIndependentOverlaysToThreePerModule()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plugin.json"), """
                {
                  "schema": 1,
                  "id": "sample",
                  "name": "Sample",
                  "transport": "builtin",
                  "surfaces": [
                    {"id":"first","title":"First","slot":"overlay","icon":"body","elements":[{"id":"text1","type":"text","label":"One"}]},
                    {"id":"second","title":"Second","slot":"overlay","icon":"body","elements":[{"id":"text2","type":"text","label":"Two"}]},
                    {"id":"third","title":"Third","slot":"overlay","icon":"body","elements":[{"id":"text3","type":"text","label":"Three"}]},
                    {"id":"fourth","title":"Fourth","slot":"overlay","icon":"body","elements":[{"id":"text4","type":"text","label":"Four"}]}
                  ]
                }
                """);

            bool loaded = PluginManifest.TryLoad(folder, out _, out string reason);

            Assert.False(loaded);
            Assert.Contains("at most 3 independent overlay", reason, StringComparison.OrdinalIgnoreCase);
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
    public async Task ExternalCommandModuleCanReportBoundedProgressAndStructuredResult()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-plugin-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker.ps1"), """
            $request = ConvertFrom-Json -InputObject ([Console]::In.ReadLine())
            [Console]::Out.WriteLine('{"type":"progress","message":"Finding contact","progress":1,"total":2}')
            [Console]::Out.WriteLine('{"type":"ui","surface":"contacts","element":"undeclared","value":"ignore"}')
            [Console]::Out.WriteLine('{"type":"ui","surface":"contacts","element":"recipient","options":[{"value":"ada","label":"Ada"}],"value":"ada","visible":true}')
            [Console]::Out.WriteLine('{"type":"ui","surface":"contacts","visible":false}')
            [Console]::Out.WriteLine('{"type":"result","text":"Found Ada"}')
            """);
        EventBus events = new();
        List<ModuleProgressEvent> progress = [];
        List<ModuleSurfaceStateChangedEvent> surfaceUpdates = [];
        using IDisposable subscription = events.Subscribe<ModuleProgressEvent>(progress.Add);
        using IDisposable surfaceSubscription = events.Subscribe<ModuleSurfaceStateChangedEvent>(surfaceUpdates.Add);
        using PluginHost host = new(events, folder);
        PluginManifest plugin = new()
        {
            Id = "telegram",
            Name = "Telegram",
            Transport = "command",
            Command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "worker.ps1"],
            Folder = folder,
            Disabled = false,
            Surfaces =
            [
                new PluginSurfaceManifest("contacts", "Contacts", "overlay", "telegram",
                [new PluginSurfaceElementManifest("recipient", "select", "Recipient", "recipient", "", "",
                    [], DynamicOptions: true)])
            ]
        };

        try
        {
            PluginResult result = await host.CallAsync(plugin,
                new PluginTool("search_contacts", "Find a contact.", "contacts.read", "{}"), "{}", "task-123",
                CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.Equal("Found Ada", result.Text);
            ModuleProgressEvent update = Assert.Single(progress);
            Assert.Equal("plugin:telegram", update.ModuleId);
            Assert.Equal("task-123", update.TurnId);
            Assert.Equal("Finding contact", update.Message);
            Assert.Equal(1, update.Progress);
            Assert.Equal(2, update.Total);
            Assert.Equal(2, surfaceUpdates.Count);
            ModuleSurfaceStateChangedEvent surfaceUpdate = surfaceUpdates[0];
            Assert.Equal("plugin:telegram", surfaceUpdate.ModuleId);
            Assert.Equal("plugin:telegram:contacts", surfaceUpdate.SurfaceId);
            Assert.Equal("recipient", surfaceUpdate.ElementId);
            Assert.Equal("task-123", surfaceUpdate.TurnId);
            Assert.Equal("ada", surfaceUpdate.Value);
            Assert.True(surfaceUpdate.IsVisible);
            Assert.Equal("Ada", Assert.Single(surfaceUpdate.Options!).Label);
            ModuleSurfaceStateChangedEvent surfaceVisibility = surfaceUpdates[1];
            Assert.Equal("task-123", surfaceVisibility.TurnId);
            Assert.Empty(surfaceVisibility.ElementId);
            Assert.False(surfaceVisibility.IsSurfaceVisible);
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
                                description = "Send a message to a contact. Token: $env:IRIS_SECRET_API_KEY"
                                inputSchema = @{ type = 'object'; properties = @{ recipient = @{ type = 'string' } }; required = @('recipient') }
                            })
                            nextCursor = 'page-2'
                        }
                    }
                } elseif ($request.method -eq 'tools/call') {
                    $notification = @{
                        jsonrpc = '2.0'
                        method = 'notifications/iris/ui'
                        params = @{
                            progressToken = $request.params._meta.progressToken
                            surface = 'activity'
                            visible = $false
                        }
                    }
                    [Console]::Out.WriteLine(($notification | ConvertTo-Json -Compress -Depth 20))
                    $stateNotification = @{
                        jsonrpc = '2.0'
                        method = 'notifications/iris/ui'
                        params = @{
                            progressToken = $request.params._meta.progressToken
                            surface = 'activity'
                            element = 'status'
                            label = "Connected with $env:IRIS_SECRET_API_KEY"
                        }
                    }
                    [Console]::Out.WriteLine(($stateNotification | ConvertTo-Json -Compress -Depth 20))
                    $result = @{ content = @(@{ type = 'text'; text = 'Message sent.' }); isError = $false }
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
              "secrets": ["api_key"],
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
              }, {
                "id": "activity",
                "title": "Telegram activity",
                "slot": "overlay",
                "icon": "telegram",
                "elements": [ { "id": "status", "type": "text", "label": "Ready" } ]
              }]
            }
            """);

        try
        {
            ModuleSecretStore secrets = new(Path.Combine(root, "secrets.json"));
            secrets.Set("telegram", "api_key", "telegram-test-\"api-key\"-123456");
            EventBus events = new();
            List<ModuleSurfaceStateChangedEvent> surfaceUpdates = [];
            using IDisposable surfaceSubscription = events.Subscribe<ModuleSurfaceStateChangedEvent>(surfaceUpdates.Add);
            using PluginHost host = new(events, root, secrets);
            host.Load();

            await host.PrepareEnabledAsync(CancellationToken.None);

            PluginManifest plugin = Assert.Single(host.Loaded);
            Assert.Equal(2, plugin.Tools.Count);
            PluginTool tool = plugin.Tools.Single(candidate => candidate.Name == "send_message");
            Assert.Equal("send_message", tool.Name);
            Assert.Equal("messages.send", tool.Permission);
            Assert.Equal("Send a message to a contact. Token: [redacted]", tool.Description);
            Assert.Contains("recipient", tool.Schema);
            Assert.Contains(plugin.Tools, candidate => candidate.Name == "search_contacts");
            Assert.Equal(2, plugin.Surfaces.Count);

            PluginResult call = await host.CallAsync(plugin, tool, "{\"recipient\":\"Ada\"}",
                "mcp-turn-42", CancellationToken.None);

            Assert.True(call.Ok, call.Error);
            Assert.Equal("Message sent.", call.Text);
            Assert.Equal(2, surfaceUpdates.Count);
            ModuleSurfaceStateChangedEvent surfaceUpdate = surfaceUpdates[0];
            Assert.Equal("plugin:telegram", surfaceUpdate.ModuleId);
            Assert.Equal("plugin:telegram:activity", surfaceUpdate.SurfaceId);
            Assert.Equal("mcp-turn-42", surfaceUpdate.TurnId);
            Assert.False(surfaceUpdate.IsSurfaceVisible);
            ModuleSurfaceStateChangedEvent textUpdate = surfaceUpdates[1];
            Assert.Equal("mcp-turn-42", textUpdate.TurnId);
            Assert.Equal("Connected with [redacted]", textUpdate.Label);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CommandPluginDiscoversToolsAndKeepsManifestPolicies()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-command-discovery-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "contacts");
        Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "module.ps1"), """
            $request = ConvertFrom-Json -InputObject ([Console]::In.ReadLine())
            if ($request.action -eq 'tools/list') {
                $toolSchema = @{ type = 'object'; properties = @{ query = @{ type = 'string' } }; required = @('query') }
                $response = @{ type = 'tools'; tools = @(@{ name = 'lookup_contact'; description = "Find a contact by name. $env:IRIS_SECRET_API_KEY"; schema = $toolSchema }) }
            } else {
                $response = @{ type = 'result'; text = "Found Ada. Token: $env:IRIS_SECRET_API_KEY" }
            }
            [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 20))
            """);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """
            {
              "schema": 1,
              "id": "contacts",
              "name": "Contacts",
              "transport": "command",
              "command": ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "module.ps1"],
              "disabled": false,
              "discoverTools": true,
              "asksPermission": ["contacts.read"],
              "secrets": ["api_key"],
              "tools": [{ "name": "lookup_contact", "permission": "contacts.read", "background": true, "alwaysConfirm": true }]
            }
            """);

        try
        {
            ModuleSecretStore secrets = new(Path.Combine(root, "secrets.json"));
            secrets.Set("contacts", "api_key", "test-secret-value-123456");
            using PluginHost host = new(new EventBus(), root, secrets);
            host.Load();
            await host.PrepareEnabledAsync(CancellationToken.None);

            PluginManifest plugin = Assert.Single(host.Loaded);
            PluginTool tool = Assert.Single(plugin.Tools);
            Assert.Equal("lookup_contact", tool.Name);
            Assert.Equal("contacts.read", tool.Permission);
            Assert.True(tool.Background);
            Assert.True(tool.AlwaysConfirm);
            Assert.Equal("Find a contact by name. ", tool.Description);
            Assert.Contains("query", tool.Schema);

            PluginResult result = await host.CallAsync(plugin, tool, "{\"query\":\"Ada\"}", "test-turn",
                CancellationToken.None);
            Assert.True(result.Ok, result.Error);
            Assert.Equal("Found Ada. Token: [redacted]", result.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginHostReloadRespectsEnabledPredicateAndSeesFilesystemChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), $"iris-plugin-reload-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "reloadable");
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, "discovery-ran.txt");
        File.WriteAllText(Path.Combine(folder, "module.ps1"), """
            [IO.File]::WriteAllText('discovery-ran.txt', 'yes')
            [Console]::Out.WriteLine('{"type":"tools","tools":[]}')
            """);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """
            {
              "schema": 1,
              "id": "reloadable",
              "name": "Reloadable",
              "transport": "command",
              "command": ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "module.ps1"],
              "disabled": false,
              "discoverTools": true,
              "tools": []
            }
            """);

        try
        {
            using PluginHost host = new(new EventBus(), root, new ModuleSecretStore(Path.Combine(root, "secrets.json")));
            host.Load();
            await host.PrepareEnabledAsync(CancellationToken.None, _ => false);
            Assert.False(File.Exists(marker));

            await host.ReloadAsync(CancellationToken.None);
            Assert.True(File.Exists(marker));
            Assert.Equal("reloadable", Assert.Single(host.Loaded).Id);

            Directory.Delete(folder, recursive: true);
            await host.ReloadAsync(CancellationToken.None);
            Assert.Empty(host.Loaded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ModuleRouterFindsRelevantToolsBeyondTheFirstEightDeclarations()
    {
        EventBus events = new();
        string temp = Path.Combine(Path.GetTempPath(), $"iris-router-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        SandboxGuard sandbox = new(events,
            new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
            new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
        await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);
        List<ModuleTool> tools = Enumerable.Range(0, 24)
            .Select(index => new ModuleTool($"calendar_{index}", $"Read calendar event metadata {index}.",
                ToolTier.Reflex, "{}", "calendar", "event"))
            .Append(new ModuleTool("send_telegram_message", "Send a message to a Telegram contact.",
                ToolTier.SystemCritical, "{}", "telegram", "send", "message", "contact"))
            .ToList();
        TestAssistantModule module = new("telegram", "Telegram", "Send messages to contacts.", tools);

        try
        {
            await host.RegisterAsync(module);

            IReadOnlyList<ModuleTool> routed = host.RouteTools(
                "Send a Telegram message to Alex", out IReadOnlyList<IAssistantModule> selected);

            Assert.Contains(module, selected);
            Assert.Contains(routed, tool => tool.Name == "send_telegram_message");
            // The chosen module hands the model its complete toolset (cap raised from
            // 8 so requests naming unknown things still surface the right hand), so the
            // zero-hit calendar fillers ride along — but only behind the signal. The
            // requested hand must lead the offer, never trail it by lottery.
            List<ModuleTool> offered = routed.ToList();
            Assert.True(
                offered.FindIndex(tool => tool.Name == "send_telegram_message")
                < offered.FindIndex(tool => tool.Name == "calendar_0"),
                "The requested tool must outrank the zero-hit filler declared before it.");
            Assert.True(routed.Count <= ModuleHost.MaximumToolsPerTurn);
            Assert.DoesNotContain("calendar_0", host.DescribeCapabilities());
            // Terse on purpose, but room for the act-don't-narrate guidance sentence.
            Assert.True(host.DescribeCapabilities().Length < 260);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task RouterKeepsTheLauncherVisibleWhenFillerWordsDiluteTheSentence()
    {
        EventBus events = new();
        string temp = Path.Combine(Path.GetTempPath(), $"iris-launcher-routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            SandboxGuard sandbox = new(events,
                new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
                new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
            await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);

            // The launcher exactly as SystemControlModule declares it, with the same
            // routing keywords, against a distractor module — reproducing the live
            // miss where "Uh open up calculator" left open_application unroutable.
            ModuleTool launcher = new("open_application",
                "Launch an application by name from the allowed set.",
                ToolTier.Scoped,
                """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
                "open app", "launch", "start program", "open calculator", "open the calculator");
            TestAssistantModule system = new("system", "System Control",
                "Opens allowed applications and reports system status.", [launcher]);
            TestAssistantModule media = new("media", "Media",
                "Controls music playback volume.",
                [new ModuleTool("control_media", "Pause or resume media.", ToolTier.Scoped, "{}",
                    "play", "pause", "music", "volume")]);
            await host.RegisterAsync(system);
            await host.RegisterAsync(media);

            IReadOnlyList<ModuleTool> routed = host.RouteTools(
                "Uh open up calculator", out IReadOnlyList<IAssistantModule> selected);

            Assert.Contains(system, selected);
            Assert.Contains(routed, tool => tool.Name == "open_application");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronousModuleToolPublishesOneStartAndOneFinishForItsTurn()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"iris-tool-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            EventBus events = new();
            List<ConversationEvent> conversation = [];
            using IDisposable subscription = events.Subscribe<ConversationEvent>(conversation.Add);
            SandboxGuard sandbox = new(events,
                new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
                new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
            await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);
            await host.RegisterAsync(new TestAssistantModule("test", "Test", "Test module.",
                [new ModuleTool("do_thing", "Do one thing.", ToolTier.Reflex, "{}") ]));

            using JsonDocument argumentsDocument = JsonDocument.Parse("{}");
            ToolResult result = await host.ExecuteAsync("do_thing", argumentsDocument.RootElement.Clone(),
                "turn-one", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(conversation, item => item.TurnId == "turn-one" &&
                item.Phase == ConversationPhase.ToolStarted);
            Assert.Single(conversation, item => item.TurnId == "turn-one" &&
                item.Phase == ConversationPhase.ToolFinished);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ModuleHostRejectsSchemaMismatchBeforePermissionOrExecution()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"iris-schema-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            EventBus events = new();
            List<ConversationEvent> conversation = [];
            using IDisposable subscription = events.Subscribe<ConversationEvent>(conversation.Add);
            SandboxGuard sandbox = new(events,
                new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
                new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
            await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);
            CountingTestModule module = new($"schema-{Guid.NewGuid():N}", "Schema test", [
                new ModuleTool("schema_test_action", "Perform schema test.", ToolTier.Reflex,
                    """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"]}""")
            ]);
            await host.RegisterAsync(module);

            using JsonDocument arguments = JsonDocument.Parse("{}");
            ToolResult result = await host.ExecuteAsync("schema_test_action", arguments.RootElement.Clone(),
                "schema-turn", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("missing a required argument", result.ModelText);
            Assert.Equal(0, module.ExecutionCount);
            Assert.Collection(conversation.Where(item => item.TurnId == "schema-turn"),
                item => Assert.Equal(ConversationPhase.ToolStarted, item.Phase),
                item => Assert.Equal(ConversationPhase.ToolFinished, item.Phase));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ModuleRegistryRefreshRemovesOldToolsAndRegistersTheReplacement()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"iris-module-registry-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        EventBus events = new();
        SandboxGuard sandbox = new(events,
            new FileAuditTrail(Path.Combine(temp, "audit.jsonl")),
            new PermissionGrantStore(Path.Combine(temp, "permissions.json")));
        await using ModuleHost host = new(new SettingsStore(), events, sandbox, memory: null!);
        try
        {
            await host.RegisterAsync(new TestAssistantModule("plugin:sample", "Sample", "Old tool.",
                [new ModuleTool("sample_old_tool", "Old tool.", ToolTier.Reflex, "{}") ]));
            Assert.True(host.HasTool("sample_old_tool"));

            await host.UnregisterAsync("plugin:sample");
            Assert.False(host.HasTool("sample_old_tool"));

            await host.RegisterAsync(new TestAssistantModule("plugin:sample", "Sample", "Replacement tool.",
                [new ModuleTool("sample_new_tool", "Replacement tool.", ToolTier.Reflex, "{}") ]));
            Assert.False(host.HasTool("sample_old_tool"));
            Assert.True(host.HasTool("sample_new_tool"));
            Assert.Contains(host.Modules, module => module.Id == "plugin:sample");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
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

    [Theory]
    [InlineData("on this play the playlist", true, "play the playlist")]
    [InlineData("on this", true, "")]
    [InlineData("i race add the song", true, "add the song")]
    [InlineData("today the playlist plays", false, "")]
    public void TrainedWakeAliasesAnswerLikeHerName(string transcript, bool expectedMatch,
        string expectedRemainder)
    {
        // ASR heard "iris" as "on this" — the owner captured that mis-hearing in Settings
        // and it became part of her name, exactly like "irys" already was by tolerance.
        Persona persona = new();
        persona.WakeAliases.Add("on this");
        persona.WakeAliases.Add("i race");
        WakePhraseMatcher matcher = new(persona);

        bool matched = matcher.TryMatch(transcript, out string remainder);

        Assert.Equal(expectedMatch, matched);
        Assert.Equal(expectedRemainder, remainder);
    }

    [Fact]
    public void SpeechPlannerDropsListSentencesButKeepsTheVerdict()
    {
        // From today's records: 161 characters of library read aloud costed 5.7 s synthesis
        // plus 9.4 s playback. The verdict is one clause; the inventory belongs on screen.
        string answer = "Sure thing boss. Here are the songs I found, Night Drive, Hollowlight, Solar Wake, " +
                        "Neon Rain, Cold Orbit, Last Ember and Old Signal. Should I start the playlist?";

        string spoken = SpeechPlanner.TrimForSpeech(answer, 240);

        Assert.Equal("Sure thing boss. Should I start the playlist?", spoken);
    }

    [Theory]
    [InlineData("Resumed.", true)]
    [InlineData("Alright boss, playlist resumed.", true)]
    [InlineData("Want me to play any of them?", false)]
    [InlineData("Sure boss. Nothing is playing right now — want me to start something?", false)]
    [InlineData("Done.", true)]
    public void UnbackedClaimsAreCaughtButAnswersThatAdmitNothingHappenedPass(
        string answer, bool expectedClaim)
    {
        Assert.Equal(expectedClaim, SpeechPlanner.LooksLikeUnbackedClaim(answer));
    }

    // The 20:25 run replaced her correct "what am I watching" answer with the receipt
    // rescue, because the video's own words ("ASMR playlist playing …") met the old
    // claim regex. Reality described aloud is information, not a claim of work.
    [Theory]
    [InlineData("You are watching an ASMR playlist playing and some stuff.", false)]
    [InlineData("The video is paused on YouTube.", false)]
    [InlineData("I think the song you're playing is great.", false)]
    [InlineData("Let me check the library, boss.", true)]
    [InlineData("I'll open the file now.", true)]
    [InlineData("I just paused it, boss.", true)]
    public void MediaStatesAreNotClaimsButPromisesWithoutWorkAre(
        string answer, bool expectedClaim)
    {
        Assert.Equal(expectedClaim, SpeechPlanner.LooksLikeUnbackedClaim(answer));
    }

    [Theory]
    [InlineData("play a song from our playlist", true)]
    [InlineData("resume the music", true)]
    [InlineData("whats am i watching rn", false)]
    [InlineData("any", false)]
    [InlineData("whats on your mind", false)]
    public void ClearOrdersAreRecognizedAsActionRequests(string userText, bool expected)
    {
        Assert.Equal(expected, SpeechPlanner.IsActionRequest(userText));
    }

    [Theory]
    [InlineData("You're watching the ASMR one — want me to play that?", true)]
    [InlineData("Which playlist, boss?", true)]
    [InlineData("You are watching an ASMR playlist.", false)]
    [InlineData("Nothing got done that turn.", false)]
    public void PermissionAskingIsSpottedButPlainAnswersPass(string answer, bool expected)
    {
        Assert.Equal(expected, SpeechPlanner.LooksLikeOfferInsteadOfAction(answer));
    }

    [Fact]
    public void LongSpeechCollapsesToTheVerdictAndOneShortTag()
    {
        // The 20:15 records: 151 characters of monologue took nine seconds of speech.
        string answer = "Sure boss, I can play something from your library right now without any problem. " +
                        "If you want anything specific just tell me the name of the song or the artist. " +
                        "I could also create a brand new playlist from the likes you saved yesterday.";

        string spoken = SpeechPlanner.TrimForSpeech(answer, 240);

        Assert.Equal("Sure boss, I can play something from your library right now without any problem.", spoken);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Boss.", true)]
    [InlineData("Boss!", true)]
    [InlineData("Yes boss!", true)]
    [InlineData("okay", true)]
    [InlineData("Sure, thanks boss.", true)]
    [InlineData("Boss, not now.", false)]
    [InlineData("Done.", false)]
    [InlineData("That did not go through — there is no playlist called 'Holo Light'.", false)]
    public void CollapsedRepliesAreCaughtButRealAnswersPass(string answer, bool expectedCollapse)
    {
        Assert.Equal(expectedCollapse, SpeechPlanner.LooksLikeCollapsedReply(answer));
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
    public void VoiceAddressingUsesConversationStateCapturedBeforeSlowRecognition()
    {
        WakePhraseMatcher matcher = new(new Persona());
        bool conversationWasOpenWhenSegmentStarted = true;

        VoiceAddressingDecision decision = VoiceAddressingPolicy.Decide(
            "play the next song", requiresWakePhrase: true, conversationWasOpenWhenSegmentStarted, matcher);

        Assert.Equal(VoiceAddressingOutcome.OpenConversationRequest, decision.Outcome);
        Assert.Equal("play the next song", decision.RequestText);
    }

    [Fact]
    public async Task MissingLocalSpeechModelIsReportedAsUnavailableNotSilence()
    {
        using WhisperNetRecognizer recognizer = new(
            Path.Combine(Path.GetTempPath(), $"missing-iris-whisper-{Guid.NewGuid():N}.bin"), "en");

        SpeechRecognitionResult result = await recognizer.TranscribeAsync([0.1f, -0.1f], CancellationToken.None);

        Assert.Equal(SpeechRecognitionOutcome.Unavailable, result.Outcome);
        Assert.Equal("model_missing", result.FailureCode);
        Assert.Empty(result.Text);
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
    public async Task PreCancelledAudioDoesNotStartPlayback()
    {
        using SpeechAudioPlayer player = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        SpeechAudio clip = new() { Bytes = [1, 2, 3], Format = "wav" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            player.PlayAsync(clip, 1.0, cancellation.Token));
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task CancelledReflexPreparationDoesNotWriteLateAudioToTheProfileCache()
    {
        string cache = Path.Combine(Path.GetTempPath(), $"iris-reflex-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cache);
        using CancellationTokenSource cancellation = new();
        try
        {
            ReflexBank bank = new(Persona.CreateDefault(), VoiceProfile.FromSettings(new CompanionSettings()),
                new CancellingSpeechSynthesizer(cancellation), cache);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bank.PrepareAsync(cancellation.Token));

            Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public void PocketVoicePickerKeepsFourEnglishProfilesWithoutStartingTheEngine()
    {
        CompanionSettings settings = new();
        VoiceGallery gallery = new(settings);

        IReadOnlyList<VoiceCandidate> candidates = gallery.DescribeCandidates();

        Assert.Equal(new[] { "anna", "mary", "vera" },
            candidates.Select(candidate => candidate.VoiceId));
        Assert.All(candidates, candidate => Assert.Equal("pocket", candidate.Engine));
        Assert.Equal("pocket", VoiceProfile.FromSettings(settings).Engine);
        Assert.Equal("vera", VoiceProfile.FromSettings(settings).VoiceId);
    }

    [Fact]
    public void PocketVoiceGalleryProfilesMatchTheConfiguredPocketEngine()
    {
        CompanionSettings settings = new();
        settings.Voice.Engine = "pocket";
        settings.Voice.CandidateVoices = new() { "alba", "anna", "eve", "mary", "vera", "estelle" /* eve is deliberately present: repair removes it */ };
        using VoiceGallery gallery = new(settings);

        IReadOnlyList<VoiceCandidate> candidates = gallery.DescribeCandidates();

        Assert.Equal(new[] { "anna", "mary", "vera" }, candidates.Select(candidate => candidate.VoiceId));
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
            Assert.Equal("vera", loaded.Voice.VoiceId);
            Assert.False(loaded.Voice.ChosenByUser);
            Assert.Equal(new[] { "anna", "mary", "vera" }, loaded.Voice.CandidateVoices);
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
    public void PocketSettingsRemoveAlbaAndEstelleAndKeepAnApprovedVoice()
    {
        string previousRoot = CompanionPaths.Root;
        string testRoot = Path.Combine(Path.GetTempPath(), $"iris-settings-{Guid.NewGuid():N}");
        try
        {
            CompanionPaths.OverrideRoot(testRoot);
            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(CompanionPaths.SettingsFile,
                "{\"Voice\":{\"Engine\":\"pocket\",\"VoiceId\":\"vera\",\"ChosenByUser\":true," +
                "\"CandidateVoices\":[\"alba\",\"anna\",\"eve\",\"mary\",\"vera\",\"estelle\"]}}");

            CompanionSettings loaded = new SettingsStore().Load();

            Assert.Equal("vera", loaded.Voice.VoiceId);
            Assert.True(loaded.Voice.ChosenByUser);
            Assert.Equal(new[] { "anna", "mary", "vera" }, loaded.Voice.CandidateVoices);
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
            VoiceId = "vera",
            PocketEndpoint = $"http://127.0.0.1:{port}",
            PocketMaxThreads = 2,
            PocketLanguage = "english_18m_2026-10"
        };

        await using PocketTtsServerHost host = new(settings);
        using PocketTtsSpeechSynthesizer synthesizer = new(settings);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        Assert.True(await host.StartAsync(timeout.Token));

        foreach (string voiceId in new[] { "anna", "mary", "vera" })
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
    public async Task EpisodesGainSynapsesToTheirPastAndTheirTopics()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"iris-episodes-{Guid.NewGuid():N}.db");
        try
        {
            await using MemoryGraph graph = new(databasePath);
            await graph.InitializeAsync();
            EventBus events = new();
            SynapticMemoryEngine memory = new(graph, new SettingsStore(), events);

            MemoryNode anchor = await graph.UpsertNodeAsync(
                "hollowlight playlist", "Boss' favourite late-night music.", MemoryLayer.Semantic, "conversation");
            RecalledMemory first = await memory.RecordEpisodeAsync(
                "Did: play hollowlight — play music: done", "conversation",
                title: "Did: hollowlight playlist");
            RecalledMemory second = await memory.RecordEpisodeAsync(
                "Did: queue hollowlight again — play music: done", "conversation",
                title: "Did: hollowlight playlist once more");

            IReadOnlyList<MemorySynapse> synapses = graph.SnapshotSynapses();
            Assert.Equal(3, synapses.Count); // two topic anchors + one narrative chain step
            Assert.Contains(synapses, synapse =>
                synapse.FromNodeId == first.NodeId && synapse.ToNodeId == second.NodeId &&
                synapse.Type == SynapseType.After);
            Assert.Contains(synapses, synapse =>
                synapse.FromNodeId == first.NodeId && synapse.ToNodeId == anchor.Id &&
                synapse.Type == SynapseType.RelatedTo);
            Assert.Contains(synapses, synapse =>
                synapse.FromNodeId == second.NodeId && synapse.ToNodeId == anchor.Id &&
                synapse.Type == SynapseType.RelatedTo);

            // A long title is cut at a word boundary, never mid-word: a chopped title
            // on the canvas reads like a glitch, not a diary.
            RecalledMemory longOne = await memory.RecordEpisodeAsync(
                "Did: something", "conversation",
                title: "Did: " + new string('w', 70));
            Assert.EndsWith("…", longOne.Title);
            Assert.True(longOne.Title.Length <= 61);
            Assert.DoesNotContain(" ", longOne.Title[^2..]);
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
    public async Task ToolReceiptDiaryStaysOutOfIntuitionButRemainsSearchable()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"iris-tasklog-{Guid.NewGuid():N}.db");
        try
        {
            await using MemoryGraph graph = new(databasePath);
            await graph.InitializeAsync();
            EventBus events = new();
            SynapticMemoryEngine memory = new(graph, new SettingsStore(), events);

            MemoryNode anchor = await graph.UpsertNodeAsync(
                "hollowlight playlist", "The boss's late-night music.", MemoryLayer.Semantic, "conversation");
            RecalledMemory episode = await memory.RecordEpisodeAsync(
                "Did: play hollowlight — play music: done", SynapticMemoryEngine.TaskLogProvenance,
                title: "Did: play music");

            // RecordEpisodeAsync linked the receipt to the knowledge it is about, so the
            // only way the diary could flood a fresh turn is through exactly that edge.
            IReadOnlyList<MemorySynapse> synapses = graph.SnapshotSynapses();
            Assert.Contains(synapses, synapse =>
                synapse.FromNodeId == episode.NodeId && synapse.ToNodeId == anchor.Id);

            // The automatic intuition activates the knowledge, never the receipt — not
            // even when the walk arrives through the topic edge above.
            ActivationResult activation = await memory.ActivateAsync("hollowlight playlist");
            Assert.Contains(activation.Activated, item => item.Node.Id == anchor.Id);
            Assert.DoesNotContain(activation.Activated,
                item => item.Node.Provenance == SynapticMemoryEngine.TaskLogProvenance);

            // A narration line is not an intuition seed. Whatever lights up can only be
            // real knowledge that shares her genuine words ("late-night music") — the
            // receipt itself must never re-enter the prompt.
            ActivationResult replay = await memory.ActivateAsync("Did: play music");
            Assert.DoesNotContain(replay.Activated,
                item => item.Node.Provenance == SynapticMemoryEngine.TaskLogProvenance);

            // But deliberate recall is exactly where the diary shines: recall_memory is
            // her looking up her own honest history at will.
            IReadOnlyList<RecalledMemory> recall =
                await memory.RecallAsync("Did: play music", 5, CancellationToken.None);
            Assert.Contains(recall, item => item.NodeId == episode.NodeId);
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
    public async Task MemoryRecallExplainsDirectMatchesAndTheAssociationThatActivatedRelatedNodes()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"iris-recall-{Guid.NewGuid():N}.db");
        try
        {
            await using MemoryGraph graph = new(databasePath);
            await graph.InitializeAsync();
            SettingsStore settings = new();
            settings.Current.Memory.SeedCount = 1;
            settings.Current.Memory.IntuitionDepth = 1;
            EventBus events = new();
            List<MemoryRecallEvent> recalls = [];
            using IDisposable subscription = events.Subscribe<MemoryRecallEvent>(recalls.Add);
            SynapticMemoryEngine memory = new(graph, settings, events);

            MemoryNode seed = await graph.UpsertNodeAsync(
                "night drive", "A direct request anchor.", MemoryLayer.Semantic, "user-correction");
            MemoryNode related = await graph.UpsertNodeAsync(
                "ambient tones", "A preference connected to night driving.", MemoryLayer.Semantic, "conversation");
            await graph.LinkAsync(seed.Id, related.Id, SynapseType.Likes, 0.8, "user-correction");

            ActivationResult activation = await memory.ActivateAsync("night drive");
            EventRecord? recallLog = null;
            void CaptureRecallLog(EventRecord record)
            {
                if (record.Area == "Memory" && record.Event == "memory.recall")
                {
                    recallLog = record;
                }
            }
            IrisEvent.EventLogged += CaptureRecallLog;
            try
            {
                memory.PublishRecall("turn-recall", activation, memory.RenderIntuition(activation));
            }
            finally
            {
                IrisEvent.EventLogged -= CaptureRecallLog;
            }

            MemoryRecallEvent recall = Assert.Single(recalls);
            RecalledMemory direct = Assert.Single(recall.Memories, item => item.NodeId == seed.Id);
            RecalledMemory associated = Assert.Single(recall.Memories, item => item.NodeId == related.Id);
            Assert.True(direct.IsDirectMatch);
            Assert.True(direct.ActivationScore > 0);
            Assert.False(associated.IsDirectMatch);
            Assert.Equal(seed.Title, associated.RelatedMemoryTitle);
            Assert.Equal("likes", associated.Relationship);
            Assert.Equal("conversation", associated.Provenance);
            EventRecord capturedRecallLog = Assert.IsType<EventRecord>(recallLog);
            Assert.Equal("turn-recall", capturedRecallLog.CorrelationId);
            IReadOnlyDictionary<string, string?> loggedData =
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(capturedRecallLog.Data);
            string loggedEvidence = loggedData["evidence"]!;
            Assert.Contains(seed.Id, loggedEvidence);
            Assert.Contains(related.Id, loggedEvidence);
            Assert.DoesNotContain(seed.Title, loggedEvidence);
            Assert.DoesNotContain(related.Title, loggedEvidence);
        }
        finally
        {
            foreach (string path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    // Test cleanup is best effort.
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

    private sealed class FakeMediaSessionSensor(params MediaSessionReadResult[] results) : IMediaSessionSensor
    {
        private readonly MediaSessionReadResult[] _results = results;
        private int _readCount;
        private int _pauseCalls;
        private int _playCalls;

        public bool PauseResult { get; set; }
        public bool PlayResult { get; set; }
        public int PauseDelayMilliseconds { get; set; }
        public int PlayDelayMilliseconds { get; set; }
        public int PauseCalls => Volatile.Read(ref _pauseCalls);
        public int PlayCalls => Volatile.Read(ref _playCalls);

        public Task<MediaSessionReadResult> ReadWithStatusAsync()
        {
            int index = Math.Min(Interlocked.Increment(ref _readCount) - 1, _results.Length - 1);
            return Task.FromResult(_results[index]);
        }

        public async Task<bool> TryPauseAsync()
        {
            Interlocked.Increment(ref _pauseCalls);
            if (PauseDelayMilliseconds > 0)
            {
                await Task.Delay(PauseDelayMilliseconds);
            }
            return PauseResult;
        }

        public async Task<bool> TryPlayAsync()
        {
            Interlocked.Increment(ref _playCalls);
            if (PlayDelayMilliseconds > 0)
            {
                await Task.Delay(PlayDelayMilliseconds);
            }
            return PlayResult;
        }
    }

    [Fact]
    public void MicrophoneCaptureChangeClassificationDistinguishesLossFromShutdown()
    {
        (string name, LogLevel level) = VoicePipeline.ClassifyCaptureChange(isOpen: true, intentionalClose: false);
        Assert.Equal("microphone.capture.opened", name);
        Assert.Equal(LogLevel.Info, level);

        (name, level) = VoicePipeline.ClassifyCaptureChange(isOpen: false, intentionalClose: true);
        Assert.Equal("microphone.capture.closed", name);
        Assert.Equal(LogLevel.Info, level);

        (name, level) = VoicePipeline.ClassifyCaptureChange(isOpen: false, intentionalClose: false);
        Assert.Equal("microphone.capture.lost", name);
        Assert.Equal(LogLevel.Warning, level);
    }

    private sealed class TestAssistantModule(string id, string name, string purpose,
        IReadOnlyList<ModuleTool> tools) : IAssistantModule
    {
        public string Id => id;
        public string DisplayName => name;
        public string Purpose => purpose;
        public string CapabilityDescription => string.Join(' ', tools.Select(tool => tool.Description));
        public IReadOnlyList<ModuleTool> Tools => tools;
        public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok("test"));
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class CountingTestModule(string id, string name, IReadOnlyList<ModuleTool> tools) : IAssistantModule
    {
        public int ExecutionCount { get; private set; }
        public string Id => id;
        public string DisplayName => name;
        public string Purpose => "Test tool schema enforcement.";
        public string CapabilityDescription => Purpose;
        public IReadOnlyList<ModuleTool> Tools => tools;
        public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(ToolResult.Ok("executed"));
        }
        public Task ShutdownAsync() => Task.CompletedTask;
    }

    private sealed class CancellingSpeechSynthesizer(CancellationTokenSource cancellation) : ISpeechSynthesizer
    {
        public string EngineName => "test";
        public bool IsAvailable => true;
        public Task<SpeechAudio?> SynthesizeAsync(string text, VoiceProfile profile,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult<SpeechAudio?>(new SpeechAudio { Bytes = [1, 2, 3], Format = "wav" });
        }
        public void Dispose() { }
    }
}
