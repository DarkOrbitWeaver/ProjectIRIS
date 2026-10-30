using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Prompts;

namespace CompanionOS.Core.Voice;

/// <summary>A recognized utterance and the stable ID that follows it through the companion.</summary>
public sealed record RecognizedVoiceRequest(string Text, string InteractionId);

/// <summary>
/// Her ears and her mouth, wired together.
///
/// This class owns the loop that makes conversation feel human:
///   microphone -> voice activity detection -> transcription -> wake check -> brain
///   brain answer -> sentence splitting -> synthesis -> speaker -> orb breathing with it
///
/// Everything runs on the CPU, so it survives Sleep and Gaming Mode. That is the whole
/// point of the brainstem design: the cortex can leave, her senses never do.
/// </summary>
public sealed class VoicePipeline : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly EventBus _events;
    private readonly PersonaStore _personaStore;
    private readonly MicrophoneCapture _microphone = new();
    private int _bargeInEnergyFrames;

    /// <summary>
    /// Barge-in confirmation, in capture frames (30 ms each). Two frames was a cough's
    /// width — her own voice leaking back through the speakers cleared it and she
    /// chopped her own sentences mid-word. Eight frames (~240 ms) means someone is
    /// really committing to speak over her (2026-10-27 audit).
    /// </summary>
    private const int BargeInFrames = 8;

    /// <summary>
    /// Loudness of her own speech as the microphone hears it — the self-echo. Updated
    /// by the playback callback, Interlocked-read (volatile) from the capture thread,
    /// and used as the floor barge-in must clear above her own voice.
    /// </summary>
    private long _spokenLevelBits;

    // Dead-capture watchdog state. Capture frames are 30 ms, so the counter below
    // reaches its threshold after ~10 seconds of pure digital silence.
    private int _silentCaptureFrames;
    private int _captureRestartAttempts;
    private bool _silentCaptureNoticeFired;
    private readonly SpeechAudioPlayer _player = new();
    private readonly WakePhraseMatcher _wakeMatcher;
    private readonly PocketTtsServerHost _pocketHost;
    private readonly KokoroTtsServerHost _kokoroHost;
    private readonly IDisposable _settingsChangedSubscription;
    private readonly SpeechOutputArbiter _speechOutputArbiter = new();
    private readonly object _wakeSampleLock = new();
    private TaskCompletionSource<string>? _wakeSampleCapture;
    private readonly SemaphoreSlim _reflexPreparationGate = new(1, 1);
    private readonly CancellationTokenSource _voiceLifetime = new();
    private readonly object _segmentLock = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);

    private List<float> _speechSegment = new();
    private bool _isSegmentOpen;
    private int _silenceMilliseconds;
    private double _noiseFloor = 0.004;
    private int _microphoneDiagnosticActive;
    private long _microphoneDiagnosticRmsBits;
    private int _microphoneStopRequested;
    private string? _microphoneSessionId;
    private DateTimeOffset _nextVoiceReprobe = DateTimeOffset.MinValue;
    private DateTimeOffset _conversationOpenUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLevelPublished = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRecognitionFailureNotice = DateTimeOffset.MinValue;
    /// <summary>
    /// An open segment is force-closed at this duration. The VAD only closes segments
    /// on silence, so music, applause or her own TTS leaking into the microphone held
    /// the level above threshold forever and grew one segment unbounded — a ~20-minute
    /// utterance shipped to Whisper as a single request (2026-10-27 audit). 20 seconds
    /// is longer than any natural spoken exchange; closing keeps her listening for the
    /// next burst instead of batching the whole room into one transcript.
    /// </summary>
    private const int MaximumUtteranceMilliseconds = 20000;

    /// <summary>
    /// A short mid-sentence pause is not a reliable turn boundary. Keep at least this
    /// much trailing silence even when an older saved setting asks for less; unlike a
    /// streaming recognizer, this pipeline only submits audio after the segment closes.
    /// </summary>
    private const int MinimumUtteranceSilenceMilliseconds = 1500;

    /// <summary>The pipeline's fixed capture format: 16 kHz mono.</summary>
    private const int CaptureSampleRate = 16000;

    /// <summary>
    /// Transcription workers and the queue in front of them. The old counter dropped a
    /// finished sentence the instant its budget was hit, so a follow-up spoken while
    /// the previous utterance was still transcribing was simply lost (2026-10-27
    /// audit). Two workers bound the CPU the ears may take; up to two more segments
    /// wait in the backlog. Everything either starts, queues or drops WITH a receipt,
    /// so the day's records always say what happened to the sentence.
    /// </summary>
    private const int MaximumSegmentWorkers = 2;
    private const int MaximumSegmentBacklog = 2;
    private readonly Queue<float[]> _segmentBacklog = new();
    private readonly object _backlogLock = new();
    private int _activeSegmentWorkers;
    private DateTimeOffset _nextSpeechFailureNotice = DateTimeOffset.MinValue;
    private CancellationTokenSource? _reflexPreparationCancellation;
    private Task _reflexPreparationTask = Task.CompletedTask;
    private bool _isDisposed;

    public VoicePipeline(SettingsStore settings, EventBus events, PersonaStore personaStore)
    {
        _settings = settings;
        _events = events;
        _personaStore = personaStore;
        _wakeMatcher = new WakePhraseMatcher(personaStore.Current);
        // Voice hosts, synthesizer and recognizer read LIVE settings (2026-10-27
        // audit): SettingsStore.Update replaces Current wholesale, and everything the
        // senses consult must follow the new values instead of pinning boot-time ones.
        _pocketHost = new PocketTtsServerHost(() => settings.Current.Voice);
        _kokoroHost = new KokoroTtsServerHost(() => settings.Current.Voice);

        Profile = VoiceProfile.FromSettings(settings.Current);
        Synthesizer = SpeechSynthesizerFactory.Create(() => settings.Current.Voice);
        Recognizer = CreateRecognizer(() => settings.Current.Ears);
        WakeGate = CreateWakeGate();
        ReflexBank = new ReflexBank(personaStore.Current, Profile, Synthesizer, CompanionPaths.CacheDirectory);

        // Her voice plays through the speakers the user picked, or the system default.
        _player.DeviceNumber = settings.Current.Voice.OutputDeviceNumber;

        CompanionLog.Info("Voice", $"Voice engine: {VoiceEndpoint(settings.Current.Voice)}.");

        _microphone.SamplesAvailable += OnMicrophoneSamples;
        _microphone.LevelChanged += OnMicrophoneLevel;
        _microphone.CaptureStateChanged += isCapturing =>
        {
            bool intentionalClose = Volatile.Read(ref _microphoneStopRequested) != 0;
            using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(_microphoneSessionId);
            IsListening = isCapturing;
            (string captureEvent, LogLevel captureLevel) = ClassifyCaptureChange(isCapturing, intentionalClose);
            IrisEvent.Log("Voice", captureEvent, captureLevel,
                data: new Dictionary<string, string?>
                {
                    ["capture_open"] = isCapturing.ToString(),
                    ["intentional_close"] = intentionalClose.ToString()
                });
            if (!isCapturing)
            {
                CloseConversationWindow();
                _microphoneSessionId = null;
                if (!intentionalClose)
                {
                    _events.PublishNotice(
                        "The microphone connection ended. Check the device and reopen Iris's ears in Settings.",
                        NoticeSeverity.Warning);
                }
            }
        };
        _player.AmplitudeChanged += OnSpokenAmplitude;

        // Re-reading the output device on every play keeps the speakers setting truthful
        // without a restart: what the picker wrote is what plays next.
        _settingsChangedSubscription = events.Subscribe<SettingsChangedEvent>(_ => _player.DeviceNumber =
            _settings.Current.Voice.OutputDeviceNumber);
    }

    /// <summary>Her locked voice identity.</summary>
    public VoiceProfile Profile { get; private set; }

    /// <summary>The engine that currently has a mouth.</summary>
    public ISpeechSynthesizer Synthesizer { get; private set; }

    /// <summary>The ears, or a recogniser that reports itself unavailable.</summary>
    public ISpeechRecognizer Recognizer { get; private set; }

    /// <summary>Instant cached phrases.</summary>
    public ReflexBank ReflexBank { get; private set; }

    /// <summary>The cheap check between voice activity detection and transcription.</summary>
    public IWakeGate WakeGate { get; private set; }

    /// <summary>
    /// True while her ears are deliberately shut: gaming mode by default, and during
    /// sleep when the whole process is being wound down. A suspended pipeline holds no
    /// microphone and, importantly, no speech model in memory.
    /// </summary>
    public bool AreEarsSuspended { get; private set; }

    /// <summary>True while the microphone is open.</summary>
    public bool IsListening { get; private set; }

    /// <summary>True while audio is playing.</summary>
    public bool IsSpeaking => _player.IsPlaying;

    /// <summary>The provider Kokoro actually initialized, or Unknown before its worker reports health.</summary>
    public string KokoroActiveProvider => _kokoroHost.ActiveProvider;

    /// <summary>When true, nothing is spoken aloud but listening still works.</summary>
    public bool IsMuted { get; set; }

    /// <summary>True when the user opened the microphone explicitly, so no wake word is needed.</summary>
    public bool IsConversationOpen => DateTimeOffset.Now < _conversationOpenUntil;

    /// <summary>Re-reads her wake name and trained aliases from the persona file, so a
    /// phrase taught in Settings is answered to on the very next utterance.</summary>
    public void RefreshEarTraining() => _wakeMatcher.Refresh(_personaStore.Current);

    /// <summary>Returns the state of the whole audio chain without waking any expensive
    /// component. This is for diagnostics and first-run guidance, not a pretend test.</summary>
    public VoiceReadiness GetReadiness()
    {
        EarsSettings ears = _settings.Current.Ears;
        VoiceSettings voice = _settings.Current.Voice;
        IReadOnlyList<string> microphones = MicrophoneCapture.ListInputDevices();
        bool microphoneConfigured = ears.InputDeviceNumber < 0
            ? microphones.Count > 0
            : ears.InputDeviceNumber < microphones.Count;
        string microphoneDetail = ears.InputDeviceNumber < 0
            ? microphones.Count > 0 ? "Windows default input" : "No recording device detected"
            : microphoneConfigured ? microphones[ears.InputDeviceNumber] : "The selected microphone is disconnected";
        bool outputConfigured = SpeechAudioPlayer.IsDeviceAvailable(voice.OutputDeviceNumber);
        string outputDetail = voice.OutputDeviceNumber < 0
            ? "Windows default output"
            : outputConfigured ? $"Output device {voice.OutputDeviceNumber}" : "The selected speaker device is disconnected";

        return new VoiceReadiness
        {
            EarsEnabled = ears.Enabled,
            IsMicrophoneConfigured = microphoneConfigured,
            MicrophoneDetail = microphoneDetail,
            IsRecognizerAvailable = Recognizer.IsAvailable,
            RecognizerDetail = Recognizer.IsAvailable
                ? $"{Recognizer.EngineName} is available"
                : $"{Recognizer.EngineName} is unavailable; no audio is captured",
            IsVoiceEngineOnline = IsVoiceOnline,
            VoiceEngineDetail = IsVoiceOnline
                ? $"Connected to {VoiceEndpoint(voice)}"
                : $"No verified voice engine at {VoiceEndpoint(voice)}",
            IsOutputConfigured = outputConfigured,
            OutputDetail = outputDetail
        };
    }

    /// <summary>
    /// One training capture for her ears: the next finished utterance is transcribed and
    /// handed back verbatim, and she deliberately does not wake, reply or act on it. The
    /// caller owns cancelling; a sample that never arrives simply times out quietly.
    /// </summary>
    public async Task<string?> CaptureWakeSampleAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<string> capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_wakeSampleLock)
        {
            // A stale armed capture (the previous attempt never heard anything) is
            // cancelled rather than left dangling forever.
            _wakeSampleCapture?.TrySetCanceled();
            _wakeSampleCapture = capture;
        }

        try
        {
            return await capture.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_wakeSampleLock)
            {
                if (ReferenceEquals(_wakeSampleCapture, capture))
                {
                    _wakeSampleCapture = null;
                }
            }
        }
    }

    /// <summary>
    /// Explicitly asks the configured local voice service whether it is reachable. This is never
    /// called by Settings refreshes: the owner must press Verify, so a diagnostic cannot wake or
    /// contend with another application unexpectedly.
    /// </summary>
    public async Task<VoiceDiagnosticResult> VerifyVoiceEngineAsync(CancellationToken cancellationToken = default)
    {
        bool online = await EnsureVoiceEngineAsync(cancellationToken).ConfigureAwait(false);
        IsVoiceOnline = online;
        _nextVoiceReprobe = DateTimeOffset.Now.AddSeconds(60);

        return new VoiceDiagnosticResult
        {
            Succeeded = online,
            Detail = online
                ? $"Voice engine answered at {VoiceEndpoint(_settings.Current.Voice)}."
                : $"Nothing answered at {VoiceEndpoint(_settings.Current.Voice)}. Check the local voice runtime and model files, then verify again."
        };
    }

    /// <summary>
    /// Measures the selected microphone briefly, whether it was already open or not. Samples
    /// stay in memory for the callback and only the loudness is retained; the diagnostic never
    /// feeds them into speech recognition.
    /// </summary>
    public async Task<VoiceDiagnosticResult> TestMicrophoneAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        VoiceReadiness readiness = GetReadiness();
        if (!readiness.IsMicrophoneConfigured)
        {
            VoiceDiagnosticResult unavailable = new()
            {
                Succeeded = false,
                Detail = readiness.MicrophoneDetail
            };
            LogMicrophoneTest(unavailable, readiness.MicrophoneDetail, captureOpen: false,
                rmsLevel: 0, started);
            return unavailable;
        }

        if (AreEarsSuspended)
        {
            VoiceDiagnosticResult suspended = new()
            {
                Succeeded = false,
                Detail = "Ears are suspended by Gaming or Sleep Mode. Resume Iris before testing the microphone."
            };
            LogMicrophoneTest(suspended, readiness.MicrophoneDetail, captureOpen: false,
                rmsLevel: 0, started);
            return suspended;
        }

        if (Interlocked.CompareExchange(ref _microphoneDiagnosticActive, 1, 0) != 0)
        {
            VoiceDiagnosticResult alreadyRunning = new()
            {
                Succeeded = false,
                Detail = "A microphone test is already running. Wait for it to finish before testing again."
            };
            LogMicrophoneTest(alreadyRunning, readiness.MicrophoneDetail, _microphone.IsCapturing,
                rmsLevel: 0, started);
            return alreadyRunning;
        }

        bool alreadyOpen = _microphone.IsCapturing;
        Interlocked.Exchange(ref _microphoneDiagnosticRmsBits, BitConverter.DoubleToInt64Bits(0));
        bool opened = alreadyOpen || StartMicrophone();
        if (!opened)
        {
            Interlocked.Exchange(ref _microphoneDiagnosticActive, 0);
            VoiceDiagnosticResult couldNotOpen = MicrophoneSignalAssessment.Evaluate(
                readiness.MicrophoneDetail, captureOpen: false, rmsLevel: 0);
            LogMicrophoneTest(couldNotOpen, readiness.MicrophoneDetail, captureOpen: false,
                rmsLevel: 0, started);
            return couldNotOpen;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
            double rmsLevel = BitConverter.Int64BitsToDouble(
                Interlocked.Read(ref _microphoneDiagnosticRmsBits));
            bool captureStillOpen = _microphone.IsCapturing;
            VoiceDiagnosticResult result = MicrophoneSignalAssessment.Evaluate(
                readiness.MicrophoneDetail, captureStillOpen, rmsLevel);
            LogMicrophoneTest(result, readiness.MicrophoneDetail, captureStillOpen, rmsLevel, started);
            return result;
        }
        catch (OperationCanceledException)
        {
            VoiceDiagnosticResult cancelled = new() { Succeeded = false, Detail = "Microphone test cancelled." };
            LogMicrophoneTest(cancelled, readiness.MicrophoneDetail, _microphone.IsCapturing,
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref _microphoneDiagnosticRmsBits)), started);
            return cancelled;
        }
        finally
        {
            if (!alreadyOpen)
            {
                StopMicrophone();
            }
            Interlocked.Exchange(ref _microphoneDiagnosticActive, 0);
        }
    }

    private static void LogMicrophoneTest(VoiceDiagnosticResult result, string deviceName,
        bool captureOpen, double rmsLevel, long started)
    {
        IrisEvent.Log("Voice", "microphone.test", result.Succeeded ? LogLevel.Info : LogLevel.Warning,
            result.Detail, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new Dictionary<string, string?>
            {
                ["device"] = deviceName,
                ["capture_open"] = captureOpen.ToString(),
                ["rms"] = double.IsFinite(rmsLevel) ? rmsLevel.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : "invalid",
                ["success"] = result.Succeeded.ToString()
            });
    }

    /// <summary>
    /// A deliberate end-to-end voice check: it asks the local TTS service for a short clip then
    /// submits it to the selected Windows output. It makes sound only when this method is called.
    /// </summary>
    public async Task<VoiceDiagnosticResult> TestVoiceOutputAsync(CancellationToken cancellationToken = default)
    {
        VoiceDiagnosticResult engine = await VerifyVoiceEngineAsync(cancellationToken).ConfigureAwait(false);
        if (!engine.Succeeded)
        {
            return engine;
        }

        if (!SpeechAudioPlayer.IsDeviceAvailable(_settings.Current.Voice.OutputDeviceNumber))
        {
            return new VoiceDiagnosticResult
            {
                Succeeded = false,
                Detail = "The selected speaker device is unavailable. Choose another output in Settings."
            };
        }

        SpeechAudio? audio = await Synthesizer.SynthesizeAsync("This is Iris. Can you hear me?", Profile,
            cancellationToken).ConfigureAwait(false);
        if (audio is null)
        {
            return new VoiceDiagnosticResult
            {
                Succeeded = false,
                Detail = "The voice engine answered but did not return playable audio. Check its model and voice name."
            };
        }

        bool accepted = await _player.PlayAsync(audio, _settings.Current.Voice.Volume, cancellationToken)
            .ConfigureAwait(false);
        return new VoiceDiagnosticResult
        {
            Succeeded = accepted,
            Detail = accepted
                ? "Test audio was sent to the selected Windows output. Confirm that you heard Iris."
                : "Windows could not start playback on the selected output. Choose another speaker device."
        };
    }

    /// <summary>Raised with a finished recognised request. Handled on a background thread.</summary>
    public event Action<RecognizedVoiceRequest>? UserRequested;

    /// <summary>Raised the moment the user speaks while she is talking, for barge-in.</summary>
    public event Action? UserInterrupted;

    /// <summary>Raised when the user only called her name.</summary>
    public event Action<string>? UserCalledHer;

    private bool StartMicrophone()
    {
        if (_microphone.IsCapturing)
        {
            return true;
        }

        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope();
        _microphoneSessionId = IrisEvent.CurrentCorrelationId;
        _silentCaptureFrames = 0;
        int deviceNumber = _settings.Current.Ears.InputDeviceNumber;
        bool opened = _microphone.Start(deviceNumber);
        if (!opened)
        {
            _microphoneSessionId = null;
            IrisEvent.Log("Voice", "microphone.capture.open_failed", LogLevel.Warning,
                data: new Dictionary<string, string?>
                {
                    ["capture_open"] = "false",
                    ["device_index"] = deviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = "device_unavailable_or_rejected"
                });
        }

        return opened;
    }

    /// <summary>
    /// Names a capture-state change. A close we did not request means the device went
    /// away underneath her, so it stays a warning; a close during shutdown is labelled
    /// deliberate instead of being logged as if her ears had died.
    /// </summary>
    internal static (string Name, LogLevel Level) ClassifyCaptureChange(bool isOpen, bool intentionalClose)
    {
        return (isOpen, intentionalClose) switch
        {
            (true, _) => ("microphone.capture.opened", LogLevel.Info),
            (false, true) => ("microphone.capture.closed", LogLevel.Info),
            (false, false) => ("microphone.capture.lost", LogLevel.Warning)
        };
    }

    private void StopMicrophone()
    {
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(_microphoneSessionId);
        Interlocked.Exchange(ref _microphoneStopRequested, 1);
        try
        {
            _microphone.Stop();
        }
        finally
        {
            Interlocked.Exchange(ref _microphoneStopRequested, 0);
            if (!_microphone.IsCapturing)
            {
                _microphoneSessionId = null;
            }
        }
    }

    /// <summary>
    /// Reopens a capture stream that only ever produced digital silence.
    /// NAudio must not be stopped from inside its own data callback, so this runs on a
    /// worker thread with a settle pause between stop and start.
    /// </summary>
    private void ReopenDeadCapture()
    {
        StopMicrophone();
        Thread.Sleep(400);
        bool reopened = StartMicrophone();
        IsListening = reopened;
        IrisEvent.Log("Voice", "microphone.capture.restart", reopened ? LogLevel.Info : LogLevel.Warning,
            message: "The capture stream carried only digital silence; reopened it to repair a stuck headset.",
            data: new Dictionary<string, string?>
            {
                ["attempt"] = _captureRestartAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reopened"] = reopened.ToString()
            });

        if (!reopened && _captureRestartAttempts >= 2 && !_silentCaptureNoticeFired)
        {
            _silentCaptureNoticeFired = true;
            _events.PublishNotice(
                "Her microphone could not be reopened after a silent stream. Check Windows microphone privacy " +
                "settings, and the device chosen in Settings → Ears.",
                NoticeSeverity.Warning);
        }
    }

    /// <summary>Prepares engines and starts listening if the ears are enabled.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (AreEarsSuspended)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        if (_settings.Current.Ears.Enabled && Recognizer.IsAvailable)
        {
            IsListening = StartMicrophone();
            if (!IsListening)
            {
                VoiceReadiness readiness = GetReadiness();
                _events.PublishNotice(
                    $"Her ears are ready, but {readiness.MicrophoneDetail.ToLowerInvariant()}. " +
                    "Text chat remains available; reconnect or choose a microphone in Settings.",
                    NoticeSeverity.Info);
            }
        }
        else if (!Recognizer.IsAvailable)
        {
            CompanionLog.Warning("Voice", "No speech recogniser is available, so the microphone stays closed.");
            _events.PublishNotice(
                $"Ears are offline: {CompanionPaths.Model(_settings.Current.Ears.WhisperModelFile)} is missing and no " +
                "transcription server is configured, so she cannot hear speech. The microphone stays closed until " +
                "one of them exists. Nothing is faked in the meantime.",
                NoticeSeverity.Warning);
        }

        // Probing the voice server and rendering the reflex bank both take seconds the
        // first time; neither belongs on the boot path. The probe result decides once,
        // loudly, whether she has a mouth this session.
        _ = Task.Run(async () =>
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _voiceLifetime.Token);
            try
            {
                await ProbeVoiceAsync(lifetime.Token).ConfigureAwait(false);
                if (IsVoiceOnline)
                {
                    await ScheduleReflexPreparationAsync(lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down; nothing to do.
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Voice", $"Voice start-up probe failed: {exception.Message}");
            }
        }, cancellationToken);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// True when the neural engine has answered this session. Nothing speaks unless
    /// this is set: there is no second voice hiding behind it that would quietly
    /// pretend she is fine.
    /// </summary>
    public bool IsVoiceOnline { get; private set; }

    /// <summary>
    /// When the engine was down at boot, do not give up on it forever: if the user
    /// starts the speech server five minutes in, her voice recovers on the next
    /// attempt. One cheap probe per minute at most.
    /// </summary>
    private async Task ScheduleReflexPreparationAsync(CancellationToken cancellationToken)
    {
        await _reflexPreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isDisposed) return;
            await CancelReflexPreparationCoreAsync().ConfigureAwait(false);
            StartReflexPreparationCore(cancellationToken);
        }
        finally
        {
            _reflexPreparationGate.Release();
        }
    }

    /// <summary>Starts preparation for the current bank while holding the lifecycle gate.</summary>
    private void StartReflexPreparationCore(CancellationToken cancellationToken)
    {
        if (_isDisposed) return;
        CancellationTokenSource preparation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _voiceLifetime.Token);
        CancellationToken preparationToken = preparation.Token;
        ReflexBank bank = ReflexBank;
        _reflexPreparationCancellation = preparation;
        _reflexPreparationTask = Task.Run(
            () => PrepareReflexBankAsync(bank, preparationToken), preparationToken);
    }

    private async Task PrepareReflexBankAsync(ReflexBank bank, CancellationToken cancellationToken)
    {
        if (!IsVoiceOnline)
        {
            IrisEvent.Log("Voice", "reflex.render_deferred", LogLevel.Trace,
                "The engine is offline; reflex phrases render when she can speak again.");
            return;
        }

        try
        {
            await bank.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A profile replaced this bank or the voice pipeline is shutting down.
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Reflex render failed: {exception.Message}");
        }
    }

    /// <summary>Stops the prior render and waits before its synthesizer can be disposed.</summary>
    private async Task CancelReflexPreparationCoreAsync()
    {
        CancellationTokenSource? previous = _reflexPreparationCancellation;
        if (previous is null) return;

        previous.Cancel();
        try
        {
            await _reflexPreparationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The cancellation is the requested replacement/shutdown path.
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Could not stop reflex rendering cleanly: {exception.Message}");
        }
        finally
        {
            previous.Dispose();
            _reflexPreparationCancellation = null;
            _reflexPreparationTask = Task.CompletedTask;
        }
    }

    private void ScheduleVoiceReprobe()
    {
        if (_isDisposed || DateTimeOffset.Now < _nextVoiceReprobe || IsVoiceOnline)
        {
            return;
        }

        _nextVoiceReprobe = DateTimeOffset.Now.AddSeconds(60);
        _ = Task.Run(async () =>
        {
            try
            {
                if (await EnsureVoiceEngineAsync(_voiceLifetime.Token).ConfigureAwait(false))
                {
                    if (_isDisposed) return;
                    IsVoiceOnline = true;
                    _events.PublishNotice("Her voice engine came online mid-session. She can speak again.",
                        NoticeSeverity.Success);
                    await ScheduleReflexPreparationAsync(_voiceLifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_voiceLifetime.IsCancellationRequested)
            {
                // Runtime shutdown owns this cancellation.
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Voice", $"Voice recovery probe failed: {exception.Message}");
            }
        }, _voiceLifetime.Token);
    }

    /// <summary>
    /// Checks her one voice and reports the result honestly, in both directions.
    /// No fallback: if the server is down she stays silent, on purpose, visibly.
    /// </summary>
    private async Task ProbeVoiceAsync(CancellationToken cancellationToken)
    {
        string endpoint = VoiceEndpoint(_settings.Current.Voice);
        VoiceDiagnosticResult verification = await VerifyVoiceEngineAsync(cancellationToken).ConfigureAwait(false);
        bool isOnline = verification.Succeeded;
        IrisEvent.Log("Voice", isOnline ? "voice.online" : "voice.offline", LogLevel.Info,
            durationMs: null,
            data: new Dictionary<string, string?> { ["endpoint"] = endpoint });
        if (isOnline)
        {
            _events.PublishNotice($"Her voice is online: the local neural engine at {endpoint}.",
                NoticeSeverity.Success);
            CompanionLog.Info("Voice", "Voice engine answered; reflexes will render in her own voice.");
            return;
        }

        _events.PublishNotice(
            $"She cannot speak: the local voice engine is unavailable at {endpoint}. There is no substitute voice, so she stays " +
            "silent until the runtime and model are ready. Chat, memory and hearing are unaffected.",
            NoticeSeverity.Warning);
        CompanionLog.Warning("Voice",
            $"Voice engine offline at {endpoint}. She will not speak this session until it comes up.");
    }

    /// <summary>Opens or closes the microphone by hand (the mic toggle in the interface).</summary>
    public void SetListening(bool shouldListen)
    {
        if (shouldListen && AreEarsSuspended)
        {
            // Asking to listen while her ears are off means "turn them back on first";
            // silently ignoring the request would look like a broken button.
            ResumeListening();
        }

        if (shouldListen)
        {
            if (!Recognizer.IsAvailable)
            {
                IsListening = false;
                _events.PublishNotice(
                    "Her ears are unavailable: install the speech model or configure the local transcription server first.",
                    NoticeSeverity.Warning);
                return;
            }

            bool opened = StartMicrophone();
            if (!opened)
            {
                _events.PublishNotice(
                    "No microphone is available right now. Iris stays usable by text; plug one in and try again.",
                    NoticeSeverity.Info);
                CloseConversationWindow();
                IsListening = false;
                return;
            }
            OpenConversationWindow();
        }
        else
        {
            StopMicrophone();
            CloseConversationWindow();
        }

        IsListening = _microphone.IsCapturing;
    }

    /// <summary>
    /// Closes her ears completely and releases everything they hold: the microphone
    /// device, the recording buffer and the speech model in memory.
    ///
    /// This is what Gaming Mode does by default and what Sleep is built on. The
    /// difference between this and a closed microphone matters: a closed microphone
    /// still keeps Whisper's weights resident, and that is hundreds of megabytes the
    /// machine wants back.
    /// </summary>
    /// <param name="announce">
    /// True when the user asked for this (so she says so); false for automatic modes,
    /// which already explain themselves through their own notice.
    /// </param>
    public void SuspendListening(bool announce = false)
    {
        if (AreEarsSuspended)
        {
            return;
        }

        AreEarsSuspended = true;
        StopSpeaking();
        StopMicrophone();
        CloseConversationWindow();

        lock (_segmentLock)
        {
            _speechSegment.Clear();
            _isSegmentOpen = false;
            _silenceMilliseconds = 0;
        }

        // Dropping the recogniser is the point of the exercise: it is the only way to
        // hand her speech model's memory back to the operating system.
        Recognizer.Dispose();
        Recognizer = new SuspendedRecognizer();
        IsListening = false;

        CompanionLog.Info("Voice", "Ears suspended: microphone closed and speech model unloaded.");
        if (announce)
        {
            _events.PublishNotice("Ears off. Nothing is listening until you wake me.", NoticeSeverity.Info);
        }
    }

    /// <summary>Rebuilds the ears and reopens the microphone. Safe to call when not suspended.</summary>
    public void ResumeListening()
    {
        if (!AreEarsSuspended)
        {
            return;
        }

        AreEarsSuspended = false;
        Recognizer = CreateRecognizer(() => _settings.Current.Ears);
        WakeGate = CreateWakeGate();

        if (_settings.Current.Ears.Enabled && Recognizer.IsAvailable)
        {
            IsListening = StartMicrophone();
        }

        CompanionLog.Info("Voice", $"Ears resumed using {Recognizer.EngineName}.");
    }

    /// <summary>
    /// Applies a newly chosen voice without restarting her.
    ///
    /// Used by the first-run picker: the user hears a voice, approves it, and the next
    /// sentence she says is already in that voice. The reflex cache is rebuilt for the
    /// new profile, because replaying yesterday's timbre inside today's answer is the
    /// one thing a locked voice must never do.
    /// </summary>
    public async Task ApplyVoiceProfileAsync(VoiceProfile profile, CancellationToken cancellationToken = default)
    {
        await _reflexPreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CancelReflexPreparationCoreAsync().ConfigureAwait(false);
            await _speechOutputArbiter.RunAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                ISpeechSynthesizer replacement = SpeechSynthesizerFactory.Create(() => _settings.Current.Voice);
                ISpeechSynthesizer previous = Synthesizer;

                Profile = profile;
                Synthesizer = replacement;
                ReflexBank = new ReflexBank(_personaStore.Current, profile, replacement, CompanionPaths.CacheDirectory);
                previous.Dispose();
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            _wakeMatcher.Refresh(_personaStore.Current);
            StartReflexPreparationCore(CancellationToken.None);
        }
        catch
        {
            // If the swap is cancelled before it commits, rebuild the current profile's
            // cache after stopping its previous render.
            StartReflexPreparationCore(CancellationToken.None);
            throw;
        }
        finally
        {
            _reflexPreparationGate.Release();
        }
    }

    /// <summary>
    /// Tracks the recent exchange for diagnostic receipts. Wake phrase protection still
    /// applies to every utterance, so nearby conversation is never treated as a reply.
    /// </summary>
    public void OpenConversationWindow(int seconds = 45) =>
        _conversationOpenUntil = DateTimeOffset.Now.AddSeconds(seconds);

    /// <summary>Requires her name again for the next request.</summary>
    public void CloseConversationWindow() => _conversationOpenUntil = DateTimeOffset.MinValue;

    /// <summary>Speaks a whole answer, sentence by sentence, and waits until finished.</summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (IsMuted || string.IsNullOrWhiteSpace(text) || !IsVoiceOnline)
        {
            ScheduleVoiceReprobe();
            IrisEvent.Log("Voice", "speech.skipped", LogLevel.Info,
                message: "No sound was played for an answer.",
                data: new Dictionary<string, string?>
                {
                    ["muted"] = IsMuted.ToString(),
                    ["voice_online"] = IsVoiceOnline.ToString(),
                    ["chars"] = (text ?? string.Empty).Length.ToString()
                });
            return;
        }

        IReadOnlyList<string> sentences = SpeechTextSanitizer.SplitIntoSentences(
            text, _settings.Current.Brain.MinimumSpokenSentenceCharacters);

        if (sentences.Count == 0)
        {
            return;
        }

        try
        {
            await _speechOutputArbiter.RunAsync(
                token => SpeakExclusiveAsync(text, sentences, token), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IrisEvent.Log("Voice", "speech.cancelled_while_queued", LogLevel.Trace,
                data: new Dictionary<string, string?> { ["chars"] = text.Length.ToString() });
        }
    }

    private async Task SpeakExclusiveAsync(string text, IReadOnlyList<string> sentences,
        CancellationToken cancellationToken)
    {
        if (IsMuted || !IsVoiceOnline)
        {
            ScheduleVoiceReprobe();
            IrisEvent.Log("Voice", "speech.skipped", LogLevel.Info,
                message: "Speech was skipped because voice became unavailable while waiting for the output device.",
                data: new Dictionary<string, string?>
                {
                    ["muted"] = IsMuted.ToString(),
                    ["voice_online"] = IsVoiceOnline.ToString(),
                    ["chars"] = text.Length.ToString()
                });
            return;
        }

        // Answer-speed economics: one receipt per spoken answer, so any day's record can
        // answer "how long did she take, and where did the seconds go?"
        int spokenCharacters = 0;
        int spokenSentences = 0;
        double synthesisTotalMs = 0;
        double playbackTotalMs = 0;
        long speakingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        bool wasInterrupted = false;

        string turnId = IrisEvent.CurrentCorrelationId ?? string.Empty;
        _events.Publish(new SpeechEvent { IsSpeaking = true, Text = text, TurnId = turnId });

        // Synthesis is pipelined: while one sentence plays, the next synthesizes
        // alongside. The serial loop spent a whole engine pass (~2-4 s of dead air)
        // before every sentence boundary — the stutter the boss heard all evening.
        (Task<SpeechAudio?> Task, long Started) prefetch = default;
        try
        {
            for (int index = 0; index < sentences.Count; index++)
            {
                string sentence = sentences[index];
                cancellationToken.ThrowIfCancellationRequested();

                if (Synthesizer is IStreamingSpeechSynthesizer streamingSynthesizer)
                {
                    long streamingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    using SpeechAudioStream? audioStream = await streamingSynthesizer
                        .OpenSpeechStreamAsync(sentence, Profile, cancellationToken).ConfigureAwait(false);
                    if (audioStream is null)
                    {
                        IrisEvent.Log("Voice", "synth.failed", LogLevel.Warning,
                            data: new Dictionary<string, string?>
                            {
                                ["chars"] = sentence.Length.ToString(),
                                ["engine"] = Synthesizer.EngineName
                            });
                        NotifySpeechUnavailable();
                        continue;
                    }

                    double streamOpenMs = System.Diagnostics.Stopwatch.GetElapsedTime(streamingStarted).TotalMilliseconds;
                    StreamingPlaybackResult streamingPlayback = await _player
                        .PlayStreamingWavAsync(audioStream.Content, Profile.Volume, cancellationToken)
                        .ConfigureAwait(false);
                    IrisEvent.Log("Voice", "synth.stream.done", LogLevel.Debug,
                        durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(streamingStarted).TotalMilliseconds,
                        data: new Dictionary<string, string?>
                        {
                            ["chars"] = sentence.Length.ToString(),
                            ["pcm_bytes"] = streamingPlayback.PcmBytes.ToString(),
                            ["first_audio_ms"] = (streamOpenMs + streamingPlayback.FirstAudioMilliseconds).ToString("0.0",
                                System.Globalization.CultureInfo.InvariantCulture)
                        });

                    if (!streamingPlayback.Accepted)
                    {
                        IrisEvent.Log("Voice", "speech.playback_failed", LogLevel.Warning,
                            data: new Dictionary<string, string?>
                            {
                                ["pcm_bytes"] = streamingPlayback.PcmBytes.ToString(),
                                ["output_device"] = _settings.Current.Voice.OutputDeviceNumber.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)
                            });
                        NotifySpeechUnavailable();
                        break;
                    }

                    _nextSpeechFailureNotice = DateTimeOffset.MinValue;
                    IrisEvent.Log("Voice", "speech.playback_accepted", LogLevel.Trace,
                        durationMs: streamingPlayback.TotalMilliseconds,
                        data: new Dictionary<string, string?>
                        {
                            ["pcm_bytes"] = streamingPlayback.PcmBytes.ToString(),
                            ["streaming"] = "true"
                        });
                    spokenCharacters += sentence.Length;
                    spokenSentences++;
                    synthesisTotalMs += streamOpenMs;
                    playbackTotalMs += streamingPlayback.TotalMilliseconds;
                    continue;
                }

                if (prefetch.Task is null)
                {
                    // First sentence on the file path (or re-entry after a failed one).
                    prefetch = (Synthesizer.SynthesizeAsync(sentence, Profile, cancellationToken),
                        System.Diagnostics.Stopwatch.GetTimestamp());
                }

                SpeechAudio? audio = await prefetch.Task.ConfigureAwait(false);
                double synthesisMs = System.Diagnostics.Stopwatch.GetElapsedTime(prefetch.Started).TotalMilliseconds;

                if (index + 1 < sentences.Count)
                {
                    // Hand the engine the next sentence before this one makes a sound:
                    // the gap between spoken sentences collapses from a whole synthesis
                    // pass to near zero.
                    prefetch = (Synthesizer.SynthesizeAsync(sentences[index + 1], Profile, cancellationToken),
                        System.Diagnostics.Stopwatch.GetTimestamp());
                }
                else
                {
                    prefetch = default;
                }

                if (audio is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IrisEvent.Log("Voice", "synth.failed", LogLevel.Warning,
                        message: "The engine returned no audio for a sentence.",
                        data: new Dictionary<string, string?>
                        {
                            ["chars"] = sentence.Length.ToString(),
                            ["engine"] = Synthesizer.EngineName
                        });
                    NotifySpeechUnavailable();
                    continue;
                }

                IrisEvent.Log("Voice", "synth.done", LogLevel.Debug,
                    durationMs: synthesisMs,
                    data: new Dictionary<string, string?>
                    {
                        ["chars"] = sentence.Length.ToString(),
                        ["bytes"] = audio.Bytes.Length.ToString(),
                        ["format"] = audio.Format
                    });
                synthesisTotalMs += synthesisMs;

                long playbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                bool outputAccepted = await _player.PlayAsync(audio, Profile.Volume, cancellationToken)
                    .ConfigureAwait(false);
                if (!outputAccepted)
                {
                    IrisEvent.Log("Voice", "speech.playback_failed", LogLevel.Warning,
                        data: new Dictionary<string, string?>
                        {
                            ["bytes"] = audio.Bytes.Length.ToString(),
                            ["output_device"] = _settings.Current.Voice.OutputDeviceNumber.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)
                        });
                    NotifySpeechUnavailable();
                    break;
                }

                _nextSpeechFailureNotice = DateTimeOffset.MinValue;
                IrisEvent.Log("Voice", "speech.playback_accepted", LogLevel.Trace,
                    durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(playbackStarted).TotalMilliseconds,
                    data: new Dictionary<string, string?> { ["bytes"] = audio.Bytes.Length.ToString() });
                spokenCharacters += sentence.Length;
                spokenSentences++;
                playbackTotalMs += System.Diagnostics.Stopwatch.GetElapsedTime(playbackStarted).TotalMilliseconds;
            }
        }
        catch (OperationCanceledException)
        {
            wasInterrupted = true;
            // Interrupted on purpose; the caller already knows.
        }
        catch (Exception exception)
        {
            IrisEvent.Log("Voice", "speech.output_failed", LogLevel.Warning,
                data: new Dictionary<string, string?>
                {
                    ["error_type"] = exception.GetType().Name
                });
            NotifySpeechUnavailable();
            CompanionLog.Warning("Voice", $"Speaking failed: {exception.Message}");
        }
        finally
        {
            // Never leave an engine request unobserved: on interruption it is already
            // cancelled; after a stopped turn its task is drained quietly.
            if (prefetch.Task is not null)
            {
                try
                {
                    await prefetch.Task.ConfigureAwait(false);
                }
                catch
                {
                    // Drained: cancellation or a fault we chose not to use.
                }
            }

            IrisEvent.Log("Voice", "speech.turn_timing", LogLevel.Trace,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(speakingStarted).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["chars"] = spokenCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["sentences"] = spokenSentences.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["synth_ms"] = synthesisTotalMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    ["playback_ms"] = playbackTotalMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    ["engine"] = Synthesizer.EngineName,
                    ["interrupted"] = wasInterrupted.ToString()
                });
            _events.Publish(new SpeechEvent { IsSpeaking = false, Text = text, TurnId = turnId });
        }
    }

    /// <summary>Plays a cached phrase immediately; false means it was unavailable or output did not start.</summary>
    public Task<bool> SpeakReflexAsync(string category, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMuted || !IsVoiceOnline || !_settings.Current.Voice.ReflexPhrasesEnabled)
        {
            ScheduleVoiceReprobe();
            return Task.FromResult(false);
        }

        return _speechOutputArbiter.RunAsync(
            token => SpeakReflexExclusiveAsync(category, token), cancellationToken);
    }

    private async Task<bool> SpeakReflexExclusiveAsync(string category, CancellationToken cancellationToken)
    {
        if (IsMuted || !IsVoiceOnline || !_settings.Current.Voice.ReflexPhrasesEnabled)
        {
            ScheduleVoiceReprobe();
            return false;
        }

        SpeechAudio? phrase = ReflexBank.TryPick(category);
        if (phrase is null) return false;

        string turnId = IrisEvent.CurrentCorrelationId ?? string.Empty;
        _events.Publish(new SpeechEvent { IsSpeaking = true, TurnId = turnId });
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            bool accepted = await _player.PlayAsync(phrase, Profile.Volume, cancellationToken)
                .ConfigureAwait(false);
            if (!accepted)
            {
                IrisEvent.Log("Voice", "speech.reflex.playback_failed", LogLevel.Warning,
                    message: "The selected output device did not accept a cached voice phrase.",
                    data: new Dictionary<string, string?>
                    {
                        ["category"] = category,
                        ["output_device"] = _settings.Current.Voice.OutputDeviceNumber.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    });
                NotifySpeechUnavailable();
                return false;
            }

            _nextSpeechFailureNotice = DateTimeOffset.MinValue;
            IrisEvent.Log("Voice", "speech.reflex.playback_accepted", LogLevel.Trace,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                data: new Dictionary<string, string?> { ["category"] = category });
            return true;
        }
        catch (OperationCanceledException)
        {
            IrisEvent.Log("Voice", "speech.reflex.cancelled", LogLevel.Trace,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                data: new Dictionary<string, string?> { ["category"] = category });
            throw;
        }
        catch (Exception exception)
        {
            IrisEvent.Log("Voice", "speech.reflex.output_failed", LogLevel.Warning,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["category"] = category,
                    ["error_type"] = exception.GetType().Name
                });
            CompanionLog.Warning("Voice", $"Cached phrase playback failed: {exception.Message}");
            NotifySpeechUnavailable();
            return false;
        }
        finally
        {
            _events.Publish(new SpeechEvent { IsSpeaking = false, TurnId = turnId });
        }
    }

    /// <summary>Stops her mid-sentence. Used by Escape, barge-in and sleep.</summary>
    public void StopSpeaking()
    {
        _player.Stop();
        _speechOutputArbiter.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _speechOutputArbiter.Cancel();
        StopSpeaking();
        _voiceLifetime.Cancel();
        await _reflexPreparationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelReflexPreparationCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _reflexPreparationGate.Release();
        }
        await _speechOutputArbiter.DrainAsync().ConfigureAwait(false);
        _settingsChangedSubscription.Dispose();
        _microphone.SamplesAvailable -= OnMicrophoneSamples;
        _microphone.LevelChanged -= OnMicrophoneLevel;
        _player.AmplitudeChanged -= OnSpokenAmplitude;
        // Disposing directly would close the capture handle without setting the
        // deliberate-stop flag, and every clean shutdown would be logged as a lost
        // microphone. Route the close through the intentional stop first.
        StopMicrophone();
        _microphone.Dispose();
        _player.Dispose();
        WakeGate.Dispose();
        Synthesizer.Dispose();
        Recognizer.Dispose();
        _recognitionGate.Dispose();
        await _pocketHost.DisposeAsync().ConfigureAwait(false);
        await _kokoroHost.DisposeAsync().ConfigureAwait(false);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnMicrophoneLevel(double level)
    {
        // Throttle to the UI refresh rate so the orb does not drown the event bus.
        if (DateTimeOffset.Now - _lastLevelPublished < TimeSpan.FromMilliseconds(50))
        {
            return;
        }

        _lastLevelPublished = DateTimeOffset.Now;
        _events.Publish(new AudioLevelEvent { Level = level, IsMicrophone = true });
    }

    private void OnSpokenAmplitude(double level)
    {
        // Publish the self-echo level for the barge-in floor BEFORE anything else:
        // her voice arriving back through the microphone must never count as a user
        // interrupting her (2026-10-27 audit).
        Volatile.Write(ref _spokenLevelBits, BitConverter.DoubleToInt64Bits(level));
        _events.Publish(new AudioLevelEvent { Level = level, IsMicrophone = false });
    }

    private void OnMicrophoneSamples(float[] samples)
    {
        if (_isDisposed)
        {
            return;
        }

        double level = CalculateLevel(samples);

        // The explicit mic check reports a peak level only. It must not alter the
        // conversation buffer, wake Iris, trigger barge-in, or reach Whisper.
        if (Volatile.Read(ref _microphoneDiagnosticActive) != 0)
        {
            long candidate = BitConverter.DoubleToInt64Bits(level);
            while (true)
            {
                long current = Interlocked.Read(ref _microphoneDiagnosticRmsBits);
                if (level <= BitConverter.Int64BitsToDouble(current) ||
                    Interlocked.CompareExchange(ref _microphoneDiagnosticRmsBits, candidate, current) == current)
                {
                    break;
                }
            }

            return;
        }

        // Dead-capture watchdog. A wireless headset can hand Windows a capture stream
        // that opens cleanly and then carries pure digital silence forever — she would
        // "listen" without ever hearing and look like she ignores her name. A fresh
        // capture stream repairs it. Two tries per session are free; beyond that the
        // device or its Windows gain is the problem, and she says so exactly once
        // instead of restarting forever.
        if (level <= 0.000001)
        {
            _silentCaptureFrames++;
            if (_silentCaptureFrames >= 330)
            {
                _silentCaptureFrames = 0;
                if (_captureRestartAttempts < 2)
                {
                    _captureRestartAttempts++;
                    // Reopening must not happen on NAudio's callback thread; hop to a worker.
                    _ = Task.Run(ReopenDeadCapture);
                    return;
                }

                if (!_silentCaptureNoticeFired)
                {
                    _silentCaptureNoticeFired = true;
                    _events.PublishNotice(
                        "Your microphone streams only silence. Two fresh streams stayed dead — check the Windows " +
                        "input level for this device, or its on-mic mute switch.",
                        NoticeSeverity.Warning);
                }
            }
        }
        else
        {
            _silentCaptureFrames = 0;
        }

        // Barge-in: if she is talking and the user starts speaking, she stops at once.
        // Eight sustained above-threshold frames (~240 ms) confirm the interruption,
        // and the floor must clear her own voice's loudness too — speech leaking
        // through the speakers used to make her interrupt herself and open VAD
        // segments of her own words. A quiet headset still interrupts her, because
        // the floor follows the room's noise floor.
        if (_player.IsPlaying && _settings.Current.Voice.BargeInEnabled)
        {
            double spokenEcho = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _spokenLevelBits));
            double bargeInLevel = Math.Max(CurrentThreshold() * 1.2, Math.Max(_noiseFloor * 3.0, spokenEcho * 1.5));
            if (level > bargeInLevel)
            {
                _bargeInEnergyFrames++;
            }
            else
            {
                _bargeInEnergyFrames = 0;
            }

            if (_bargeInEnergyFrames >= BargeInFrames)
            {
                _bargeInEnergyFrames = 0;
                IrisEvent.Log("Voice", "speech.barge_in", LogLevel.Info,
                    message: "User speech overrode her voice mid-sentence.",
                    data: new Dictionary<string, string?>
                    {
                        ["level"] = level.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        ["threshold"] = bargeInLevel.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    });
                StopSpeaking();
                UserInterrupted?.Invoke();
                OpenConversationWindow();
            }
        }
        else
        {
            _bargeInEnergyFrames = 0;
        }

        if (!_microphone.IsCapturing)
        {
            return;
        }

        // Track the room's noise floor slowly, so a fan or a hum never wakes her.
        _noiseFloor = (_noiseFloor * 0.995) + (level * 0.005);
        double threshold = CurrentThreshold();

        lock (_segmentLock)
        {
            if (level >= threshold)
            {
                _isSegmentOpen = true;
                _silenceMilliseconds = 0;
                _speechSegment.AddRange(samples);

                // A loud room (music, applause, her own TTS leaking in) held the level
                // above threshold forever and grew this segment unbounded — a 20-minute
                // utterance shipped as one giant transcription request with no close in
                // sight (2026-10-27 audit). Force-close at the ceiling and keep
                // listening: the next loud burst opens a fresh segment.
                if (_speechSegment.Count >= CaptureSampleRate * MaximumUtteranceMilliseconds / 1000)
                {
                    CloseSegmentLocked();
                }
                return;
            }

            if (!_isSegmentOpen)
            {
                _speechSegment.Clear();
                return;
            }

            _speechSegment.AddRange(samples);
            _silenceMilliseconds += (int)(samples.Length / 16.0);

            int silenceThreshold = ResolveUtteranceSilenceThreshold(_settings.Current.Ears.SilenceMilliseconds);

            if (_silenceMilliseconds >= silenceThreshold)
            {
                CloseSegmentLocked();
            }
        }
    }

    /// <summary>
    /// Turns the open segment into bytes for the brain. Must be called under
    /// <see cref="_segmentLock"/> so both close paths (silence and the duration
    /// ceiling) filter and submit exactly the same way.
    /// </summary>
    private void CloseSegmentLocked()
    {
        float[] finishedSegment = _speechSegment.ToArray();
        _speechSegment.Clear();
        _isSegmentOpen = false;
        _silenceMilliseconds = 0;

        int minimumSamples = CaptureSampleRate * _settings.Current.Ears.MinimumSpeechMilliseconds / 1000;
        if (finishedSegment.Length >= minimumSamples)
        {
            SubmitSegment(finishedSegment);
        }
    }

    internal static int ResolveUtteranceSilenceThreshold(int configuredMilliseconds) =>
        Math.Max(MinimumUtteranceSilenceMilliseconds, configuredMilliseconds);

    /// <summary>
    /// Queues one finished utterance for transcription instead of dropping it the
    /// moment a worker is busy. Two workers cap the CPU the ears may take; up to two
    /// more segments wait in the backlog, so a follow-up spoken while the previous
    /// utterance is still transcribing gets processed after it instead of vanishing.
    /// Every outcome — started, queued, dropped — is logged with the same receipt.
    /// </summary>
    private void SubmitSegment(float[] segment)
    {
        bool queued = false;
        bool started = false;
        lock (_backlogLock)
        {
            if (_activeSegmentWorkers < MaximumSegmentWorkers)
            {
                _activeSegmentWorkers++;
                started = true;
            }
            else if (_segmentBacklog.Count < MaximumSegmentBacklog)
            {
                _segmentBacklog.Enqueue(segment);
                queued = true;
            }
        }

        if (started)
        {
            _ = RunSegmentWorkerAsync(segment);
        }
        else
        {
            LogSegmentDecision(queued ? "segment_queued_backlog" : "segment_dropped_backlog", "queue",
                _settings.Current.Ears.RequireWakePhrase, IsConversationOpen, WakeGate.IsAvailable, 0);
        }
    }

    /// <summary>
    /// One transcription slot. When the work ends, a queued segment takes the slot in
    /// this same task — so the worker count and the queue are changed under one lock
    /// and a submit racing a completion can never strand either.
    /// </summary>
    private async Task RunSegmentWorkerAsync(float[] segment)
    {
        try
        {
            await ProcessSegmentAsync(segment).ConfigureAwait(false);
        }
        finally
        {
            float[]? next;
            lock (_backlogLock)
            {
                next = _segmentBacklog.Count > 0 ? _segmentBacklog.Dequeue() : null;
                if (next is null)
                {
                    _activeSegmentWorkers--;
                }
            }

            if (next is not null)
            {
                await RunSegmentWorkerAsync(next).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Transcribes one finished utterance and decides whether it was for her.</summary>
    private async Task ProcessSegmentAsync(float[] segment)
    {
        // This same ID flows through the callback into AssistantBrain, so the logs can
        // connect recognition, wake matching, tool work, and the spoken reply without
        // ever persisting the transcript or microphone samples.
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope();
        string interactionId = IrisEvent.CurrentCorrelationId!;
        await _recognitionGate.WaitAsync().ConfigureAwait(false);
        string stage = "wake_gate";
        int recognizedCharacters = 0;
        try
        {
            // The cheap question first. With wake phrases enabled, every utterance must
            // pass the shape gate before transcription, even during a recent exchange.
            EarsSettings ears = _settings.Current.Ears;
            bool conversationWasOpen = IsConversationOpen;
            bool wakeGateAvailable = WakeGate.IsAvailable;
            bool capturingForTraining;
            lock (_wakeSampleLock)
            {
                capturingForTraining = _wakeSampleCapture is not null;
            }
            if (!Recognizer.IsAvailable)
            {
                LogSegmentDecision("recognizer_unavailable", "preflight", ears.RequireWakePhrase,
                    conversationWasOpen, wakeGateAvailable, recognizedCharacters,
                    failureCode: Recognizer.EngineName switch
                    {
                        "whisper" => "model_missing",
                        "http" => "endpoint_not_configured",
                        _ => "recognizer_suspended"
                    });
                NotifyRecognitionUnavailable();
                return;
            }

            bool expectsAnswer = !ears.RequireWakePhrase || capturingForTraining;
            if (!expectsAnswer && wakeGateAvailable &&
                WakeGate.Check(segment, CancellationToken.None) == WakeGateVerdict.Skip)
            {
                LogSegmentDecision("wake_gate_rejected", stage, ears.RequireWakePhrase,
                    conversationWasOpen, wakeGateAvailable, recognizedCharacters);
                return;
            }

            stage = "transcription";
            long transcribeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            SpeechRecognitionResult recognition = await Recognizer.TranscribeAsync(segment, CancellationToken.None)
                .ConfigureAwait(false);
            double transcribeDurationMs = System.Diagnostics.Stopwatch.GetElapsedTime(transcribeStarted).TotalMilliseconds;
            string transcript = recognition.Text;

            if (recognition.Outcome != SpeechRecognitionOutcome.Recognized || string.IsNullOrWhiteSpace(transcript))
            {
                string outcome = recognition.Outcome switch
                {
                    SpeechRecognitionOutcome.NoSpeech => "no_speech",
                    SpeechRecognitionOutcome.Unavailable => "recognizer_unavailable",
                    SpeechRecognitionOutcome.Failed => "recognizer_failed",
                    SpeechRecognitionOutcome.Cancelled => "recognizer_cancelled",
                    _ => "recognizer_returned_empty"
                };
                LogSegmentDecision(outcome, stage, ears.RequireWakePhrase, conversationWasOpen,
                    wakeGateAvailable, recognizedCharacters, transcribeDurationMs, recognition.FailureCode);
                if (recognition.Outcome is SpeechRecognitionOutcome.Unavailable or SpeechRecognitionOutcome.Failed)
                {
                    NotifyRecognitionUnavailable();
                }
                return;
            }

            _nextRecognitionFailureNotice = DateTimeOffset.MinValue;
            recognizedCharacters = transcript.Length;
            _captureRestartAttempts = 0;
            _silentCaptureNoticeFired = false;

            // Ear training: this segment was captured for the settings screens, so its
            // words go to the trainer verbatim and she pointedly does not react.
            TaskCompletionSource<string>? sampleCapture;
            lock (_wakeSampleLock)
            {
                sampleCapture = _wakeSampleCapture;
                if (sampleCapture is not null)
                {
                    _wakeSampleCapture = null;
                }
            }
            if (sampleCapture is not null)
            {
                sampleCapture.TrySetResult(transcript);
                LogSegmentDecision("wake_training_sample", stage, ears.RequireWakePhrase,
                    conversationWasOpen, wakeGateAvailable, recognizedCharacters, transcribeDurationMs);
                return;
            }

            stage = "addressing";
            VoiceAddressingDecision addressing = VoiceAddressingPolicy.Decide(
                transcript, ears.RequireWakePhrase, conversationWasOpen, _wakeMatcher);
            if (addressing.Outcome != VoiceAddressingOutcome.WakeMiss)
            {
                _events.Publish(new TranscriptEvent
                {
                    Text = transcript,
                    IsFinal = true,
                    IsUser = true,
                    TurnId = interactionId
                });
                // Speech text belongs in the visible conversation, not persistent diagnostics.
                CompanionLog.Info("Voice", $"Recognized speech ({transcript.Length} characters; transcript kept in conversation only).");
            }

            switch (addressing.Outcome)
            {
                case VoiceAddressingOutcome.WakeMiss:
                    // The boss cannot read a wall: what the ears heard and why the segment
                    // was ignored goes live to the shell. Display only — nothing is
                    // persisted here, transcripts stay conversation-only.
                    _events.Publish(new EarsEvent
                    {
                        State = "not_addressed",
                        Text = string.IsNullOrWhiteSpace(transcript)
                            ? "heard something short — waiting for her name"
                            : $"heard \u201C{transcript.Trim()}\u201D — not addressed to her"
                    });
                    LogSegmentDecision("wake_miss", stage, ears.RequireWakePhrase, conversationWasOpen,
                        wakeGateAvailable, recognizedCharacters, transcribeDurationMs);
                    return;
                case VoiceAddressingOutcome.CalledWithoutRequest:
                    OpenConversationWindow();
                    LogSegmentDecision("called_without_request", stage, ears.RequireWakePhrase,
                        conversationWasOpen, wakeGateAvailable, recognizedCharacters, transcribeDurationMs);
                    UserCalledHer?.Invoke(interactionId);
                    return;
                case VoiceAddressingOutcome.OpenConversationRequest:
                    OpenConversationWindow();
                    LogSegmentDecision("request_in_open_conversation", stage, ears.RequireWakePhrase,
                        conversationWasOpen, wakeGateAvailable, recognizedCharacters, transcribeDurationMs);
                    UserRequested?.Invoke(new RecognizedVoiceRequest(addressing.RequestText, interactionId));
                    return;
                case VoiceAddressingOutcome.WakeRequest:
                    OpenConversationWindow();
                    LogSegmentDecision("wake_request", stage, ears.RequireWakePhrase, conversationWasOpen,
                        wakeGateAvailable, recognizedCharacters, transcribeDurationMs);
                    UserRequested?.Invoke(new RecognizedVoiceRequest(addressing.RequestText, interactionId));
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(addressing.Outcome), addressing.Outcome,
                        "Unknown voice addressing outcome.");
            }
        }
        catch (Exception exception)
        {
            IrisEvent.Log("Voice", "voice.segment.failed", LogLevel.Warning,
                data: new Dictionary<string, string?>
                {
                    ["stage"] = stage,
                    ["error_type"] = exception.GetType().Name,
                    ["recognized_chars"] = recognizedCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            CompanionLog.Warning("Voice", $"A spoken segment could not be handled: {exception.Message}");
        }
        finally
        {
            _recognitionGate.Release();
        }
    }

    /// <summary>Records the outcome of a speech segment without retaining its transcript or audio.</summary>
    private void LogSegmentDecision(string outcome, string stage, bool requiresWakePhrase,
        bool conversationWasOpen, bool wakeGateAvailable, int recognizedCharacters, double? durationMs = null,
        string failureCode = "")
    {
        IrisEvent.Log("Voice", "voice.segment.decision", LogLevel.Trace, durationMs: durationMs,
            data: new Dictionary<string, string?>
            {
                ["outcome"] = outcome,
                ["stage"] = stage,
                ["requires_wake_phrase"] = requiresWakePhrase.ToString(),
                ["conversation_was_open"] = conversationWasOpen.ToString(),
                ["wake_gate_available"] = wakeGateAvailable.ToString(),
                ["recognizer"] = Recognizer.EngineName,
                ["recognizer_available"] = Recognizer.IsAvailable.ToString(),
                ["recognition_attempted"] = (stage is "transcription" or "addressing").ToString(),
                ["capture_session_id"] = _microphoneSessionId ?? string.Empty,
                ["failure_code"] = failureCode,
                ["recognized_chars"] = recognizedCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    private void NotifyRecognitionUnavailable()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (now < _nextRecognitionFailureNotice)
        {
            return;
        }

        _nextRecognitionFailureNotice = now.AddSeconds(30);
        _events.PublishNotice(
            "I couldn't transcribe that. Check Settings → Voice to see whether the speech recognizer is available.",
            NoticeSeverity.Warning);
    }

    private void NotifySpeechUnavailable()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (now < _nextSpeechFailureNotice)
        {
            return;
        }

        _nextSpeechFailureNotice = now.AddSeconds(30);
        _events.PublishNotice(
            "I couldn't play my spoken reply. The text is still here; check Settings → Voice and the selected output device.",
            NoticeSeverity.Warning);
    }

    private double CurrentThreshold()
    {
        EarsSettings ears = _settings.Current.Ears;
        double configured = Math.Clamp(ears.VoiceThreshold, 0.005, 0.5);
        // Never let the adaptive floor push the threshold so high that quiet speech is lost.
        return Math.Min(Math.Max(configured, _noiseFloor * 2.2), configured * 4);
    }

    private static double CalculateLevel(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double energy = 0;
        foreach (float sample in samples)
        {
            energy += sample * sample;
        }

        return Math.Sqrt(energy / samples.Length);
    }

    /// <summary>
    /// Chooses the mouth. There is exactly one: the local neural engine. A missing or
    /// crashed engine is reported by <see cref="ProbeVoiceAsync"/> and she stays silent
    /// until it is up — she never borrows a stranger's voice to look alive.
    /// </summary>
    private static async Task<bool> ProbeSpeechServerAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using HttpClient pocketProbe = new() { Timeout = TimeSpan.FromSeconds(1) };
                using HttpResponseMessage health = await pocketProbe.GetAsync(
                    settings.PocketEndpoint.TrimEnd('/') + "/health", cancellationToken).ConfigureAwait(false);
                return health.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        string endpoint = settings.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase)
            ? settings.KokoroEndpoint
            : settings.HttpEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        try
        {
            using HttpClient probeClient = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
            using HttpResponseMessage response = await probeClient
                .GetAsync(endpoint.TrimEnd('/') + "/models", cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // A number of otherwise OpenAI-compatible local TTS servers expose only
            // /audio/speech, not /models. Calling those servers "offline" made a
            // functioning voice permanently silent. A tiny real synthesis request is
            // the authoritative fallback: headers are enough; the body is never played.
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound &&
                response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            using HttpRequestMessage speechProbe = new(HttpMethod.Post,
                endpoint.TrimEnd('/') + "/audio/speech")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    model = settings.HttpModel,
                    input = ".",
                    voice = settings.VoiceId,
                    response_format = "wav"
                }), System.Text.Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                speechProbe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", settings.ApiKey);
            }

            using HttpResponseMessage speechResponse = await probeClient.SendAsync(speechProbe,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return speechResponse.IsSuccessStatusCode;
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Voice", $"No speech server at {endpoint}: {exception.Message}");
            return false;
        }
    }

    /// <summary>Starts Iris's managed Pocket worker if selected, then verifies the local endpoint.</summary>
    public async Task<bool> EnsureVoiceEngineAsync(CancellationToken cancellationToken = default)
    {
        VoiceSettings voice = _settings.Current.Voice;
        if (voice.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase) &&
            !await _pocketHost.StartAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        if (voice.Engine.Equals("kokoro", StringComparison.OrdinalIgnoreCase) &&
            !await _kokoroHost.StartAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await ProbeSpeechServerAsync(voice, cancellationToken).ConfigureAwait(false);
    }

    private static string VoiceEndpoint(VoiceSettings settings) => settings.GetEffectiveEndpoint();

    /// <summary>
    /// Builds the gate that sits between voice activity detection and transcription.
    ///
    /// Today it is the model-free shape check. Replacing this one method with a
    /// keyword spotter (Porcupine, openWakeWord) upgrades the whole pipeline without
    /// touching it, because everything downstream only knows about IWakeGate.
    /// </summary>
    private IWakeGate CreateWakeGate() => new UtteranceShapeWakeGate(_settings);

    /// <summary>Chooses the ears from a LIVE settings source, so a rebuild (suspension
    /// or a settings change) always consults the current values. Whisper and the HTTP
    /// ear keep re-reading their own options per transcribe call; Parakeet takes a
    /// snapshot here because its selected model path and native recognizer are fixed
    /// for the lifetime of the microphone session.</summary>
    private static ISpeechRecognizer CreateRecognizer(Func<EarsSettings> ears)
    {
        EarsSettings options = ears();
        ParakeetRecognizer ggufParakeet = new(options);
        ParakeetOnnxRecognizer onnxParakeet = new(options);
        ISpeechRecognizer parakeet = ggufParakeet.IsAvailable ? ggufParakeet : onnxParakeet;
        if (parakeet == ggufParakeet)
        {
            onnxParakeet.Dispose();
        }
        else
        {
            ggufParakeet.Dispose();
        }
        WhisperNetRecognizer whisper = new(ears);
        HttpSpeechRecognizer http = new(ears);

        // Explicit choices leave no doubt about which engine runs.
        if (options.Recognizer.Equals("whisper", StringComparison.OrdinalIgnoreCase))
        {
            http.Dispose();
            parakeet.Dispose();
            return whisper;
        }

        if (options.Recognizer.Equals("parakeet", StringComparison.OrdinalIgnoreCase))
        {
            http.Dispose();
            whisper.Dispose();
            return parakeet;
        }

        if (options.Recognizer.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            whisper.Dispose();
            parakeet.Dispose();
            return http;
        }

        // "auto": Parakeet is the production ears and wins when either its GGUF
        // transcribe.cpp stack or configured Sherpa-ONNX model is installed. A local
        // whisper model is the fallback, and HTTP is last because it needs a running
        // server. The runner-ups are always disposed, so no client outlives the choice.
        if (parakeet.IsAvailable)
        {
            http.Dispose();
            whisper.Dispose();
            return parakeet;
        }

        if (whisper.IsAvailable)
        {
            http.Dispose();
            parakeet.Dispose();
            return whisper;
        }

        if (http.IsAvailable)
        {
            whisper.Dispose();
            parakeet.Dispose();
            return http;
        }

        // Neither is available: keep the whisper shape so callers still get honest empty
        // transcripts, and release the HTTP client that would otherwise live forever.
        http.Dispose();
        parakeet.Dispose();
        return whisper;
    }
}
