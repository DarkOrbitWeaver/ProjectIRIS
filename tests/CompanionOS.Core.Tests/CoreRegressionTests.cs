using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Voice;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CompanionOS.Core.Tests;

public sealed class CoreRegressionTests
{
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
