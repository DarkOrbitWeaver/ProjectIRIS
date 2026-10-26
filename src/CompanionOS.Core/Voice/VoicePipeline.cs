using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Voice;

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
    private readonly SpeechAudioPlayer _player = new();
    private readonly WakePhraseMatcher _wakeMatcher;
    private readonly PocketTtsServerHost _pocketHost;
    private readonly object _segmentLock = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);

    private List<float> _speechSegment = new();
    private bool _isSegmentOpen;
    private int _silenceMilliseconds;
    private double _noiseFloor = 0.004;
    private int _microphoneDiagnosticActive;
    private long _microphoneDiagnosticRmsBits;
    private DateTimeOffset _nextVoiceReprobe = DateTimeOffset.MinValue;
    private DateTimeOffset _conversationOpenUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLevelPublished = DateTimeOffset.MinValue;
    private bool _isDisposed;

    public VoicePipeline(SettingsStore settings, EventBus events, PersonaStore personaStore)
    {
        _settings = settings;
        _events = events;
        _personaStore = personaStore;
        _wakeMatcher = new WakePhraseMatcher(personaStore.Current);
        _pocketHost = new PocketTtsServerHost(settings.Current.Voice);

        Profile = VoiceProfile.FromSettings(settings.Current);
        Synthesizer = SpeechSynthesizerFactory.Create(settings.Current.Voice);
        Recognizer = CreateRecognizer(settings.Current);
        WakeGate = CreateWakeGate();
        ReflexBank = new ReflexBank(personaStore.Current, Profile, Synthesizer, CompanionPaths.CacheDirectory);

        // Her voice plays through the speakers the user picked, or the system default.
        _player.DeviceNumber = settings.Current.Voice.OutputDeviceNumber;

        CompanionLog.Info("Voice", $"Voice engine: {VoiceEndpoint(settings.Current.Voice)}.");

        _microphone.SamplesAvailable += OnMicrophoneSamples;
        _microphone.LevelChanged += OnMicrophoneLevel;
        _microphone.CaptureStateChanged += isCapturing =>
        {
            IsListening = isCapturing;
            if (!isCapturing)
            {
                CloseConversationWindow();
            }
        };
        _player.AmplitudeChanged += OnSpokenAmplitude;

        // Re-reading the output device on every play keeps the speakers setting truthful
        // without a restart: what the picker wrote is what plays next.
        events.Subscribe<SettingsChangedEvent>(_ => _player.DeviceNumber =
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

    /// <summary>When true, nothing is spoken aloud but listening still works.</summary>
    public bool IsMuted { get; set; }

    /// <summary>True when the user opened the microphone explicitly, so no wake word is needed.</summary>
    public bool IsConversationOpen => DateTimeOffset.Now < _conversationOpenUntil;

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
        bool opened = alreadyOpen || _microphone.Start(_settings.Current.Ears.InputDeviceNumber);
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
                _microphone.Stop();
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
    public event Action<string>? UserRequested;

    /// <summary>Raised the moment the user speaks while she is talking, for barge-in.</summary>
    public event Action? UserInterrupted;

    /// <summary>Raised when the user only called her name.</summary>
    public event Action? UserCalledHer;

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
            IsListening = _microphone.Start(_settings.Current.Ears.InputDeviceNumber);
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
            try
            {
                await ProbeVoiceAsync(cancellationToken).ConfigureAwait(false);
                if (IsVoiceOnline)
                {
                    await ReflexBank.PrepareAsync(cancellationToken).ConfigureAwait(false);
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
    private void ScheduleVoiceReprobe()
    {
        if (DateTimeOffset.Now < _nextVoiceReprobe || IsVoiceOnline)
        {
            return;
        }

        _nextVoiceReprobe = DateTimeOffset.Now.AddSeconds(60);
        _ = Task.Run(async () =>
        {
            if (await EnsureVoiceEngineAsync(CancellationToken.None).ConfigureAwait(false))
            {
                IsVoiceOnline = true;
                _events.PublishNotice("Her voice engine came online mid-session. She can speak again.",
                    NoticeSeverity.Success);
                await ReflexBank.PrepareAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });
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

            bool opened = _microphone.Start(_settings.Current.Ears.InputDeviceNumber);
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
            _microphone.Stop();
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
        _microphone.Stop();
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
        Recognizer = CreateRecognizer(_settings.Current);
        WakeGate = CreateWakeGate();

        if (_settings.Current.Ears.Enabled && Recognizer.IsAvailable)
        {
            IsListening = _microphone.Start(_settings.Current.Ears.InputDeviceNumber);
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
        ISpeechSynthesizer replacement = SpeechSynthesizerFactory.Create(_settings.Current.Voice);
        ISpeechSynthesizer previous = Synthesizer;

        Profile = profile;
        Synthesizer = replacement;
        ReflexBank = new ReflexBank(_personaStore.Current, profile, replacement, CompanionPaths.CacheDirectory);

        previous.Dispose();
        _wakeMatcher.Refresh(_personaStore.Current);

        // The voice itself is live the moment the profile lands. The reflex cache, though,
        // renders phrase by phrase through the engine — offline or slow, that used to hang
        // the swap for minutes while the picker sat on "Swapping her voice…". So the render
        // runs in the background exactly like boot does: straight through when she can
        // speak, deferred until the next successful probe when she cannot — SpeakAsync
        // guards actual speech either way, so nothing is ever faked.
        CancellationToken renderToken = cancellationToken;
        _ = Task.Run(async () =>
        {
            if (!IsVoiceOnline)
            {
                IrisEvent.Log("Voice", "reflex.render_deferred", LogLevel.Trace,
                    "The engine is offline; reflex phrases render when she can speak again.");
                return;
            }

            try
            {
                await ReflexBank.PrepareAsync(renderToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down or another voice swap took over; nothing to do.
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Voice", $"Reflex render failed: {exception.Message}");
            }
        }, renderToken);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the conversation open for a while after she is addressed, so a follow-up
    /// ("and make it louder") does not need her name again. This is how people talk.
    /// </summary>
    public void OpenConversationWindow(int seconds = 25) =>
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

        string turnId = IrisEvent.CurrentCorrelationId ?? string.Empty;
        _events.Publish(new SpeechEvent { IsSpeaking = true, Text = text, TurnId = turnId });

        try
        {
            foreach (string sentence in sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SpeechAudio? audio = await Synthesizer
                    .SynthesizeAsync(sentence, Profile, cancellationToken)
                    .ConfigureAwait(false);

                if (audio is null)
                {
                    IrisEvent.Log("Voice", "synth.failed", LogLevel.Warning,
                        message: "The engine returned no audio for a sentence.",
                        data: new Dictionary<string, string?>
                        {
                            ["chars"] = sentence.Length.ToString(),
                            ["endpoint"] = VoiceEndpoint(_settings.Current.Voice)
                        });
                    continue;
                }

                IrisEvent.Log("Voice", "synth.done", LogLevel.Debug,
                    data: new Dictionary<string, string?>
                    {
                        ["chars"] = sentence.Length.ToString(),
                        ["bytes"] = audio.Bytes.Length.ToString(),
                        ["format"] = audio.Format
                    });

                await _player.PlayAsync(audio, Profile.Volume, cancellationToken).ConfigureAwait(false);
                IrisEvent.Log("Voice", "speech.played", LogLevel.Trace, data: new Dictionary<string, string?>
                {
                    ["bytes"] = audio.Bytes.Length.ToString()
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupted on purpose; the caller already knows.
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Speaking failed: {exception.Message}");
        }
        finally
        {
            _events.Publish(new SpeechEvent { IsSpeaking = false, Text = text, TurnId = turnId });
        }
    }

    /// <summary>Plays a cached phrase immediately. Returns false when that category is not ready.</summary>
    public async Task<bool> SpeakReflexAsync(string category, CancellationToken cancellationToken = default)
    {
        if (IsMuted || !IsVoiceOnline || !_settings.Current.Voice.ReflexPhrasesEnabled)
        {
            ScheduleVoiceReprobe();
            return false;
        }

        SpeechAudio? phrase = ReflexBank.TryPick(category);
        if (phrase is null)
        {
            return false;
        }

        await _player.PlayAsync(phrase, Profile.Volume, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Stops her mid-sentence. Used by Escape, barge-in and sleep.</summary>
    public void StopSpeaking() => _player.Stop();

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _microphone.SamplesAvailable -= OnMicrophoneSamples;
        _microphone.LevelChanged -= OnMicrophoneLevel;
        _player.AmplitudeChanged -= OnSpokenAmplitude;
        _microphone.Dispose();
        _player.Dispose();
        WakeGate.Dispose();
        Synthesizer.Dispose();
        Recognizer.Dispose();
        _recognitionGate.Dispose();
        await _pocketHost.DisposeAsync().ConfigureAwait(false);
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

        // Barge-in: if she is talking and the user starts speaking, she stops at once.
        if (_player.IsPlaying && _settings.Current.Voice.BargeInEnabled && level > CurrentThreshold() * 1.4)
        {
            StopSpeaking();
            UserInterrupted?.Invoke();
            OpenConversationWindow();
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
                return;
            }

            if (!_isSegmentOpen)
            {
                _speechSegment.Clear();
                return;
            }

            _speechSegment.AddRange(samples);
            _silenceMilliseconds += (int)(samples.Length / 16.0);

            if (_silenceMilliseconds >= _settings.Current.Ears.SilenceMilliseconds)
            {
                float[] finishedSegment = _speechSegment.ToArray();
                _speechSegment.Clear();
                _isSegmentOpen = false;
                _silenceMilliseconds = 0;

                int minimumSamples = 16000 * _settings.Current.Ears.MinimumSpeechMilliseconds / 1000;
                if (finishedSegment.Length >= minimumSamples)
                {
                    _ = ProcessSegmentAsync(finishedSegment);
                }
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
        await _recognitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The cheap question first. While she is expecting an answer, everything is
            // transcribed; the rest of the time a sentence has to look worth the CPU.
            bool expectsAnswer = !_settings.Current.Ears.RequireWakePhrase || IsConversationOpen;
            if (!expectsAnswer && WakeGate.IsAvailable &&
                WakeGate.Check(segment, CancellationToken.None) == WakeGateVerdict.Skip)
            {
                CompanionLog.Trace("Voice", "Wake gate rejected a segment before transcription.");
                return;
            }

            long transcribeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            string transcript = await Recognizer.TranscribeAsync(segment, CancellationToken.None)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                IrisEvent.Log("Voice", "transcribe.empty", LogLevel.Trace);
                return;
            }

            IrisEvent.Log("Voice", "transcribe.done", LogLevel.Trace,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(transcribeStarted).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["chars"] = transcript.Length.ToString(),
                    ["recognized"] = Recognizer.EngineName
                });

            _events.Publish(new TranscriptEvent
            {
                Text = transcript,
                IsFinal = true,
                IsUser = true,
                TurnId = IrisEvent.CurrentCorrelationId ?? string.Empty
            });
            // Speech text belongs in the visible conversation, not persistent diagnostics.
            CompanionLog.Info("Voice", $"Recognized speech ({transcript.Length} characters; transcript kept in conversation only).");

            EarsSettings ears = _settings.Current.Ears;
            bool conversationOpen = IsConversationOpen;

            if (!ears.RequireWakePhrase || conversationOpen)
            {
                OpenConversationWindow();
                UserRequested?.Invoke(transcript);
                return;
            }

            if (!_wakeMatcher.TryMatch(transcript, out string remainder))
            {
                // Not addressed to her; stay silent and let the audio decay.
                IrisEvent.Log("Voice", "wake.miss", LogLevel.Trace,
                    data: new Dictionary<string, string?> { ["chars"] = transcript.Length.ToString() });
                return;
            }

            IrisEvent.Log("Voice", "wake.hit", LogLevel.Trace);

            OpenConversationWindow();
            if (string.IsNullOrWhiteSpace(remainder) || _wakeMatcher.IsGreetingOnly(remainder))
            {
                UserCalledHer?.Invoke();
                return;
            }

            UserRequested?.Invoke(remainder);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"A spoken segment could not be handled: {exception.Message}");
        }
        finally
        {
            _recognitionGate.Release();
        }
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

        string endpoint = settings.HttpEndpoint;
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

        return await ProbeSpeechServerAsync(voice, cancellationToken).ConfigureAwait(false);
    }

    private static string VoiceEndpoint(VoiceSettings settings) =>
        settings.Engine.Equals("pocket", StringComparison.OrdinalIgnoreCase)
            ? settings.PocketEndpoint
            : settings.HttpEndpoint;

    /// <summary>
    /// Builds the gate that sits between voice activity detection and transcription.
    ///
    /// Today it is the model-free shape check. Replacing this one method with a
    /// keyword spotter (Porcupine, openWakeWord) upgrades the whole pipeline without
    /// touching it, because everything downstream only knows about IWakeGate.
    /// </summary>
    private IWakeGate CreateWakeGate() => new UtteranceShapeWakeGate(_settings);

    /// <summary>Chooses the ears: local Whisper when its model exists, otherwise the HTTP server.</summary>
    private static ISpeechRecognizer CreateRecognizer(CompanionSettings settings)
    {
        EarsSettings ears = settings.Ears;
        WhisperNetRecognizer whisper = new(CompanionPaths.Model(ears.WhisperModelFile), ears.Language,
            ears.MaxRecognitionThreads);
        HttpSpeechRecognizer http = new(ears);

        if (ears.Recognizer.Equals("whisper", StringComparison.OrdinalIgnoreCase))
        {
            http.Dispose();
            return whisper;
        }

        if (ears.Recognizer.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            whisper.Dispose();
            return http;
        }

        // "auto": a local model file is more reliable than an endpoint that may not run.
        // The runner-up is always disposed, so no client outlives the choice.
        if (whisper.IsAvailable)
        {
            http.Dispose();
            return whisper;
        }

        if (http.IsAvailable)
        {
            whisper.Dispose();
            return http;
        }

        // Neither is available: keep the whisper shape so callers still get honest empty
        // transcripts, and release the HTTP client that would otherwise live forever.
        http.Dispose();
        return whisper;
    }
}
