using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Perception;

/// <summary>
/// Her senses. Three layers, cheapest first:
///
///   1. Ambient awareness (free): the active window, what media is playing, CPU,
///      memory, battery, how long the user has been away. Refreshed on a slow timer
///      and injected into every prompt as a few plain sentences.
///   2. Vision on demand: she looks only when asked, or when a tool needs pixels.
///   3. Live share: a perceptual hash decides whether the screen actually changed
///      before any frame reaches the model, so an idle desktop costs nothing.
///
/// Nothing here decides what to say; it only reports what is true.
/// </summary>
public sealed class PerceptionEngine : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly EventBus _events;
    private readonly WindowsNative.CpuLoadSampler _cpuSampler = new();
    private readonly IMediaSessionSensor _mediaSensor;
    private readonly object _snapshotLock = new();
    private readonly SemaphoreSlim _mediaOwnershipGate = new(1, 1);

    private Timer? _ambientTimer;
    private SituationSnapshot _current = new();
    private ScreenFrame? _lastFrame;
    private string _lastWindowSignature = string.Empty;
    private string _lastMediaSignature = string.Empty;
    private bool _hasAmbientObservation;
    private volatile bool _isRunning;
    // Timers are allowed to fire again while a slow Windows/media call is still in
    // flight.  Never queue a second copy of the same passive observation: stale
    // telemetry is preferable to stealing another worker or I/O slot from a game.
    private int _ambientRefreshInProgress;
    private int _mediaRefreshInProgress;
    private int _mediaPausedForVoice;

    public PerceptionEngine(SettingsStore settings, EventBus events, IMediaSessionSensor? mediaSensor = null)
    {
        _settings = settings;
        _events = events;
        _mediaSensor = mediaSensor ?? new MediaSessionSensor();
    }

    /// <summary>Raised whenever the ambient snapshot changes.</summary>
    public event Action<SituationSnapshot>? SituationUpdated;

    /// <summary>The most recent ambient snapshot.</summary>
    public SituationSnapshot Current
    {
        get { lock (_snapshotLock) { return _current; } }
    }

    /// <summary>
    /// Returns a lightweight, point-in-time account of configured sensing and the
    /// latest evidence from each source. This describes metadata availability; it
    /// never claims that Iris saw screen pixels unless a screen frame was captured.
    /// </summary>
    public IReadOnlyList<PerceptionSourceHealth> GetSourceHealth()
    {
        SituationSnapshot snapshot;
        bool hasAmbientObservation;
        ScreenFrame? lastFrame;
        lock (_snapshotLock)
        {
            snapshot = Clone(_current);
            hasAmbientObservation = _hasAmbientObservation;
            lastFrame = _lastFrame;
        }

        SenseSettings settings = _settings.Current.Senses;
        return BuildSourceHealth(snapshot, hasAmbientObservation, lastFrame, settings, _isRunning,
            DateTimeOffset.Now);
    }

    private static IReadOnlyList<PerceptionSourceHealth> BuildSourceHealth(SituationSnapshot snapshot,
        bool hasAmbientObservation, ScreenFrame? lastFrame, SenseSettings settings, bool running,
        DateTimeOffset now)
    {
        TimeSpan staleAfter = TimeSpan.FromMilliseconds(
            Math.Clamp(settings.StatsIntervalMilliseconds, 1000, 15000) * 3L);
        bool ambientFresh = hasAmbientObservation && running && now - snapshot.CapturedAt <= staleAfter;
        List<PerceptionSourceHealth> sources = new(7)
        {
            BuildAmbientSource("Foreground window", settings.WatchActiveWindow, running,
                hasAmbientObservation, ambientFresh, snapshot.CapturedAt, snapshot.ForegroundAvailable,
                snapshot.ActiveApplicationName.Length == 0 ? "No application name was returned." : snapshot.ActiveApplicationName),
            BuildMediaSource(settings.WatchMediaSession, running, snapshot, now, staleAfter),
            BuildAmbientSource("CPU load", settings.WatchSystemStats, running,
                hasAmbientObservation, ambientFresh, snapshot.CapturedAt, snapshot.CpuAvailable,
                snapshot.CpuAvailable ? $"{snapshot.CpuPercent:0}%" : "CPU probe did not return a reading."),
            BuildAmbientSource("Memory", settings.WatchSystemStats, running,
                hasAmbientObservation, ambientFresh, snapshot.CapturedAt, snapshot.MemoryAvailable,
                snapshot.MemoryAvailable
                    ? $"{snapshot.RamUsedMegabytes / 1024:0.0} / {snapshot.RamTotalMegabytes / 1024:0.0} GB used"
                    : "Memory probe did not return a reading."),
            BuildAmbientSource("Power", settings.WatchSystemStats, running,
                hasAmbientObservation, ambientFresh, snapshot.CapturedAt, snapshot.PowerAvailable,
                snapshot.PowerAvailable
                    ? snapshot.BatteryPercent >= 0
                        ? $"{snapshot.BatteryPercent}% battery; {PowerSourceDescription(snapshot.IsOnAcPower)}"
                        : $"{PowerSourceDescription(snapshot.IsOnAcPower)}; battery percentage unavailable"
                    : "Power probe did not return a reading."),
            BuildAmbientSource("Idle duration", settings.WatchIdleState, running,
                hasAmbientObservation, ambientFresh, snapshot.CapturedAt, snapshot.IdleAvailable,
                snapshot.IdleAvailable ? $"{snapshot.IdleSeconds:0} seconds" : "Idle probe did not return a reading."),
            BuildVisionSource(settings.VisionMode, lastFrame, now)
        };
        return sources;
    }

    private static PerceptionSourceHealth BuildAmbientSource(string name, bool enabled, bool running,
        bool hasObservation, bool fresh, DateTimeOffset observedAt, bool available, string detail)
    {
        if (!enabled)
        {
            return new(name, "Disabled", "Disabled in settings.", null);
        }

        if (!hasObservation)
        {
            return new(name, running ? "Waiting" : "Stopped", "No successful ambient sample in this session.", null);
        }

        if (!running)
        {
            return new(name, "Stopped", $"Last sample: {FormatAge(DateTimeOffset.Now - observedAt)} ago; not current.", observedAt);
        }

        if (!fresh)
        {
            return new(name, "Stale", $"Last sample: {FormatAge(DateTimeOffset.Now - observedAt)} ago; refresh delayed.", observedAt);
        }

        return available
            ? new(name, "Available", detail, observedAt)
            : new(name, "Unavailable", detail, observedAt);
    }

    private static PerceptionSourceHealth BuildMediaSource(bool enabled, bool running, SituationSnapshot snapshot,
        DateTimeOffset now, TimeSpan staleAfter)
    {
        if (!enabled)
        {
            return new("Media session", "Disabled", "Disabled in settings.", null);
        }

        if (snapshot.MediaObservedAt is not { } observedAt)
        {
            return new("Media session", running ? "Waiting" : "Stopped",
                "No successful media read in this session.", null);
        }

        TimeSpan age = now - observedAt;
        if (!snapshot.MediaObservationAvailable)
        {
            return new("Media session", "Unavailable", "Windows media session read failed; the previous track is not current.", observedAt);
        }

        if (!running)
        {
            return new("Media session", "Stopped", $"Last successful read: {FormatAge(age)} ago; not current.", observedAt);
        }

        if (age > staleAfter)
        {
            return new("Media session", "Stale", $"Last successful read: {FormatAge(age)} ago.", observedAt);
        }

        return snapshot.MediaSessionPresent
            ? new("Media session", "Available", string.IsNullOrWhiteSpace(snapshot.MediaTitle)
                ? "An active session did not provide a title."
                : $"{snapshot.MediaTitle}{(string.IsNullOrWhiteSpace(snapshot.MediaArtist) ? string.Empty : $" — {snapshot.MediaArtist}")}", observedAt)
            : new("Media session", "Available", "No active Windows media session was found.", observedAt);
    }

    private static PerceptionSourceHealth BuildVisionSource(string? mode, ScreenFrame? lastFrame, DateTimeOffset now)
    {
        string lastFrameDetail = lastFrame is null
            ? "No screen frame has been captured in this session."
            : $"Last frame captured {FormatAge(now - lastFrame.CapturedAt)} ago; it is not live evidence.";
        return mode?.ToLowerInvariant() switch
        {
            "off" => new("Screen vision", "Disabled", $"Screen capture is off in settings. {lastFrameDetail}", lastFrame?.CapturedAt),
            "live" or "liveshare" or "live-share" => new("Screen vision", "Configured",
                $"Live share is configured; each frame must be captured successfully. {lastFrameDetail}", lastFrame?.CapturedAt),
            _ => new("Screen vision", "On demand",
                $"No continuous screen access. A frame is attached only after a requested capture succeeds. {lastFrameDetail}",
                lastFrame?.CapturedAt)
        };
    }

    /// <summary>The last frame her eyes actually saw, if any.</summary>
    public ScreenFrame? LastFrame
    {
        get { lock (_snapshotLock) { return _lastFrame; } }
    }

    /// <summary>Name of the game currently in front, or empty.</summary>
    public string ForegroundGame { get; private set; } = string.Empty;

    /// <summary>Starts the ambient timers.</summary>
    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RefreshAmbient();
        int interval = Math.Clamp(_settings.Current.Senses.StatsIntervalMilliseconds, 1000, 15000);
        _ambientTimer = new Timer(_ => RefreshAmbient(), null, interval, interval);
        CompanionLog.Info("Senses", "Ambient awareness is running.");
    }

    /// <summary>Stops the timers. Senses can be resumed at any time.</summary>
    public void Stop()
    {
        _isRunning = false;
        _ambientTimer?.Dispose();
        _ambientTimer = null;
    }

    /// <summary>
    /// Looks at the screen. The disabled vision setting always blocks capture;
    /// forceFreshCapture only bypasses unchanged-frame reuse. Returns null when
    /// the setting is off or capture fails.
    /// </summary>
    public async Task<ScreenFrame?> LookAsync(bool forceFreshCapture, CancellationToken cancellationToken = default)
    {
        SenseSettings senses = _settings.Current.Senses;

        if (string.Equals(senses.VisionMode, "off", StringComparison.OrdinalIgnoreCase))
        {
            _events.Publish(new SensesEvent { SenseName = "vision", Summary = "Vision is switched off in settings." });
            return null;
        }

        SituationSnapshot snapshot = Current;
        ScreenFrame? frame = ScreenCapture.Capture(
            senses.CaptureMaxDimension,
            senses.JpegQuality,
            captureBounds: null,
            windowTitle: snapshot.ActiveWindowTitle);

        if (frame is null)
        {
            _events.Publish(new SensesEvent { SenseName = "vision", Summary = "The screen could not be captured." });
            return null;
        }

        ScreenFrame? previousFrame;
        lock (_snapshotLock)
        {
            previousFrame = _lastFrame;
        }

        if (!forceFreshCapture && previousFrame is not null)
        {
            int changedBits = ScreenCapture.CountChangedBits(previousFrame.PerceptualHash, frame.PerceptualHash);
            if (changedBits < Math.Clamp(senses.ChangeSensitivity, 1, 32))
            {
                _events.Publish(new SensesEvent
                {
                    SenseName = "vision",
                    Summary = $"Screen looks unchanged ({changedBits} bits), reusing the last frame."
                });
                return previousFrame;
            }
        }

        lock (_snapshotLock)
        {
            _lastFrame = frame;
        }

        _events.Publish(new SensesEvent
        {
            SenseName = "vision",
            Summary = $"Captured {frame.Width}x{frame.Height}, {frame.EncodedByteCount / 1024} KB, " +
                      $"window '{Shorten(frame.WindowTitle, 60)}'."
        });

        await Task.CompletedTask.ConfigureAwait(false);
        return frame;
    }

    /// <summary>True when a game owns the foreground and gaming mode should engage.</summary>
    public bool IsGameInForeground(out string executableName)
    {
        executableName = ForegroundGame;
        return !string.IsNullOrWhiteSpace(ForegroundGame);
    }

    /// <summary>
    /// Pauses the current Windows media session for a voice interaction. The flag is set only
    /// after Windows accepts the pause, so a later resume can never start something Iris did not
    /// interrupt herself.
    /// </summary>
    public async Task<bool> PauseMediaForVoiceAsync(CancellationToken cancellationToken = default)
    {
        await _mediaOwnershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settings.Current.Voice.PauseMediaWhenListening ||
                Interlocked.CompareExchange(ref _mediaPausedForVoice, 0, 0) != 0)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool paused;
            try
            {
                paused = await _mediaSensor.TryPauseAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Senses", $"Could not pause media for voice: {exception.Message}");
                IrisEvent.Log("Senses", "media.pause_failed", LogLevel.Warning,
                    data: new Dictionary<string, string?> { ["reason"] = "sensor_error" });
                return false;
            }

            if (paused)
            {
                Interlocked.Exchange(ref _mediaPausedForVoice, 1);
                _events.Publish(new SensesEvent { SenseName = "media", Summary = "Paused by Iris while listening." });
                IrisEvent.Log("Senses", "media.paused_for_voice", LogLevel.Info,
                    data: new Dictionary<string, string?> { ["pause_owned"] = "true" });
            }
            else
            {
                IrisEvent.Log("Senses", "media.pause_unconfirmed", LogLevel.Debug);
            }

            return paused;
        }
        finally
        {
            _mediaOwnershipGate.Release();
        }
    }

    /// <summary>Controls the active Windows media session and returns only confirmed outcomes.</summary>
    public async Task<string> ControlMediaAsync(string action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = action.Trim().ToLowerInvariant();
        bool succeeded = normalized switch
        {
            "pause" => await _mediaSensor.TryPauseAsync().ConfigureAwait(false),
            "play" or "resume" => await _mediaSensor.TryPlayAsync().ConfigureAwait(false),
            _ => false
        };

        IrisEvent.Log("Senses", succeeded ? "media.command_succeeded" : "media.command_unconfirmed",
            succeeded ? LogLevel.Info : LogLevel.Warning,
            data: new Dictionary<string, string?> { ["action"] = normalized });

        if (succeeded)
        {
            string verb = normalized switch
            {
                "pause" => "paused",
                "play" or "resume" => "resumed",
                _ => "controlled"
            };
            return $"Windows confirmed that media {verb}.";
        }

        return "Windows did not confirm this media action. There may be no active controllable Windows media session.";
    }

    /// <summary>Returns only media Iris paused for the current voice interaction.</summary>
    public async Task<bool> ResumeMediaAfterVoiceAsync(CancellationToken cancellationToken = default)
    {
        await _mediaOwnershipGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _mediaPausedForVoice, 0, 0) == 0)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_settings.Current.Voice.ResumeMediaAfterVoiceTurn)
            {
                Interlocked.Exchange(ref _mediaPausedForVoice, 0);
                IrisEvent.Log("Senses", "media.resume_skipped", LogLevel.Info,
                    data: new Dictionary<string, string?> { ["reason"] = "resume_disabled" });
                return false;
            }

            bool resumed;
            try
            {
                resumed = await _mediaSensor.TryPlayAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                resumed = false;
                CompanionLog.Warning("Senses", $"Could not resume media after voice: {exception.Message}");
            }

            // The decision is terminal even when Windows cannot confirm playback. Keeping
            // stale ownership could make a later conversation unexpectedly start media.
            Interlocked.Exchange(ref _mediaPausedForVoice, 0);
            if (resumed)
            {
                _events.Publish(new SensesEvent { SenseName = "media", Summary = "Resumed after Iris finished." });
                IrisEvent.Log("Senses", "media.resumed_after_voice", LogLevel.Info,
                    data: new Dictionary<string, string?> { ["pause_owned"] = "true" });
            }
            else
            {
                _events.Publish(new SensesEvent
                {
                    SenseName = "media",
                    Summary = "Iris could not confirm that media resumed. Check the player if it is still paused."
                });
                IrisEvent.Log("Senses", "media.resume_unconfirmed", LogLevel.Warning);
                _events.PublishNotice(
                    "I couldn't confirm that your media resumed. Check the player if it is still paused.",
                    NoticeSeverity.Warning);
            }

            return resumed;
        }
        finally
        {
            _mediaOwnershipGate.Release();
        }
    }

    /// <summary>Teaches her that an executable is a game (or that it is not).</summary>
    public void LearnGameExecutable(string executableName, bool isGame)
    {
        string normalized = NormalizeExecutable(executableName);
        SystemSettings system = _settings.Current.System;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (isGame)
        {
            if (!system.KnownGameExecutables.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                system.KnownGameExecutables.Add(normalized);
            }

            system.GamingExclusions.RemoveAll(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            if (!system.GamingExclusions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                system.GamingExclusions.Add(normalized);
            }

            system.KnownGameExecutables.RemoveAll(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        }

        _settings.Save();
        CompanionLog.Info("Senses", $"{normalized} is now treated as {(isGame ? "a game" : "not a game")}.");
    }

    /// <summary>The plain-English situation block that goes into every prompt.</summary>
    public string DescribeForPrompt(bool userAttachedImage = false) =>
        DescribeForPrompt(userAttachedImage, out _);

    /// <summary>Builds prompt context and its matching source-health snapshot atomically.</summary>
    public string DescribeForPrompt(bool userAttachedImage,
        out IReadOnlyList<PerceptionSourceHealth> sourceHealth)
    {
        SituationSnapshot snapshot;
        bool hasAmbientObservation;
        ScreenFrame? lastFrame;
        lock (_snapshotLock)
        {
            snapshot = Clone(_current);
            hasAmbientObservation = _hasAmbientObservation;
            lastFrame = _lastFrame;
        }

        bool sensorsRunning = _isRunning;
        DateTimeOffset now = DateTimeOffset.Now;
        SenseSettings senses = _settings.Current.Senses;
        sourceHealth = BuildSourceHealth(snapshot, hasAmbientObservation, lastFrame, senses, sensorsRunning, now);
        TimeSpan observationAge = now - snapshot.CapturedAt;
        if (observationAge < TimeSpan.Zero)
        {
            observationAge = TimeSpan.Zero;
        }
        int observationIntervalMilliseconds = Math.Clamp(senses.StatsIntervalMilliseconds, 1000, 15000);
        TimeSpan staleAfter = TimeSpan.FromMilliseconds(observationIntervalMilliseconds * 3L);
        bool ambientIsCurrent = hasAmbientObservation && sensorsRunning && observationAge <= staleAfter;
        List<string> lines = new();

        if (!hasAmbientObservation)
        {
            lines.Add("Ambient machine state is unknown; no observation has been collected in this session.");
        }
        else if (!sensorsRunning)
        {
            lines.Add($"Ambient sensing is stopped; its last sample was {FormatAge(observationAge)} ago and is not current.");
        }
        else if (!ambientIsCurrent)
        {
            lines.Add($"Ambient sensing is stale; its last sample was {FormatAge(observationAge)} ago. Do not treat it as current.");
        }
        else
        {
            lines.Add($"Ambient machine state was checked {FormatAge(observationAge)} ago and is current.");
        }

        if (ambientIsCurrent && senses.WatchActiveWindow && snapshot.ForegroundAvailable)
        {
            string application = string.IsNullOrWhiteSpace(snapshot.ActiveApplicationName)
                ? "an unidentified foreground window"
                : snapshot.ActiveApplicationName;
            lines.Add($"Foreground: {application}" +
                      (string.IsNullOrWhiteSpace(snapshot.ActiveWindowTitle)
                          ? "."
                          : $" — window \"{Shorten(snapshot.ActiveWindowTitle, 90)}\"."));
        }

        if (ambientIsCurrent && senses.WatchActiveWindow && !snapshot.ForegroundAvailable)
        {
            lines.Add("Foreground window information is unavailable.");
        }

        if (senses.WatchMediaSession)
        {
            DescribeMediaState(lines, snapshot, sensorsRunning, staleAfter, DateTimeOffset.Now);
        }

        if (ambientIsCurrent && senses.WatchSystemStats)
        {
            List<string> machineDetails = [];
            machineDetails.Add(snapshot.CpuAvailable
                ? $"CPU {snapshot.CpuPercent:0}%"
                : "CPU load unavailable");
            machineDetails.Add(snapshot.MemoryAvailable
                ? $"RAM {snapshot.RamUsedMegabytes / 1024:0.0} of {snapshot.RamTotalMegabytes / 1024:0.0} GB used"
                : "RAM usage unavailable");
            machineDetails.Add(!snapshot.PowerAvailable
                ? "power status unavailable"
                : snapshot.BatteryPercent >= 0
                    ? $"battery {snapshot.BatteryPercent}% ({PowerSourceDescription(snapshot.IsOnAcPower)})"
                    : snapshot.IsOnAcPower is { } onAcPower
                        ? $"{PowerSourceDescription(onAcPower)}; battery percentage unavailable"
                        : "battery and power-source details unavailable");
            lines.Add($"Machine: {string.Join(", ", machineDetails)}.");
        }

        if (ambientIsCurrent && senses.WatchIdleState && !snapshot.IdleAvailable)
        {
            lines.Add("Idle duration is unavailable.");
        }
        else if (ambientIsCurrent && senses.WatchIdleState && snapshot.IdleSeconds >= 60)
        {
            lines.Add($"Boss has been idle for about {snapshot.IdleSeconds / 60:0} minutes.");
        }

        if (ambientIsCurrent && !string.IsNullOrWhiteSpace(ForegroundGame))
        {
            lines.Add($"A game is running in front: {ForegroundGame}.");
        }

        if (!senses.WatchActiveWindow)
        {
            lines.Add("Foreground window sensing is disabled in settings.");
        }
        if (!senses.WatchMediaSession)
        {
            lines.Add("Media session sensing is disabled in settings.");
        }
        if (!senses.WatchSystemStats)
        {
            lines.Add("System metrics sensing is disabled in settings.");
        }
        if (!senses.WatchIdleState)
        {
            lines.Add("Idle sensing is disabled in settings.");
        }

        lines.Add($"Screen capture setting: {senses.VisionMode}. This describes configuration, not visual evidence.");
        lines.Add(userAttachedImage
            ? "A user-supplied image is attached to this turn. Base visual claims only on that image; it does not provide continuous screen access."
            : "No image is attached to the current user message. Ambient app/window details are not visual inspection. Describe screen pixels only after a successful screen tool result attaches an image.");
        lines.Add($"Local time: {DateTime.Now:HH:mm}.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes >= 1)
        {
            int minutes = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        int seconds = Math.Max(0, (int)Math.Round(age.TotalSeconds));
        return $"{seconds} second{(seconds == 1 ? string.Empty : "s")}";
    }

    private static string PowerSourceDescription(bool? onAcPower) => onAcPower switch
    {
        true => "on mains",
        false => "on battery",
        null => "power source unknown"
    };

    private static void DescribeMediaState(List<string> lines, SituationSnapshot snapshot, bool sensorsRunning,
        TimeSpan staleAfter, DateTimeOffset now)
    {
        if (snapshot.MediaObservedAt is not { } observedAt)
        {
            lines.Add("Media session state is unknown; no successful read has completed.");
            return;
        }

        TimeSpan age = now - observedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (!sensorsRunning)
        {
            lines.Add($"Media sensing is stopped; its last read was {FormatAge(age)} ago and is not current.");
            return;
        }

        if (!snapshot.MediaObservationAvailable)
        {
            lines.Add("Windows media session information is unavailable; do not assume the last known track is current.");
            return;
        }

        if (age > staleAfter)
        {
            lines.Add($"Media session information is stale; its last successful read was {FormatAge(age)} ago.");
            return;
        }

        if (!snapshot.MediaSessionPresent)
        {
            lines.Add($"No active Windows media session was found {FormatAge(age)} ago.");
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.MediaTitle))
        {
            lines.Add("An active Windows media session did not provide a track title.");
            return;
        }

        string state = string.IsNullOrWhiteSpace(snapshot.MediaPlaybackStatus)
            ? string.Empty
            : $" ({snapshot.MediaPlaybackStatus.ToLowerInvariant()})";
        string artist = string.IsNullOrWhiteSpace(snapshot.MediaArtist) ? string.Empty : $" by {snapshot.MediaArtist}";
        lines.Add($"Media: \"{snapshot.MediaTitle}\"{artist}{state}.");
    }

    public void Dispose()
    {
        Stop();
    }

    private void RefreshAmbient()
    {
        if (!_isRunning || Interlocked.Exchange(ref _ambientRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            SenseSettings senses = _settings.Current.Senses;
            SystemSettings system = _settings.Current.System;
            // Game detection needs the foreground process even when the user has
            // chosen not to expose normal window context to Iris.  Otherwise avoid
            // opening the foreground process handle altogether.
            bool needsForegroundWindow = senses.WatchActiveWindow ||
                (system.GamingModeEnabled && system.GamingAutoDetect);
            WindowsNative.ForegroundWindowInfo window = needsForegroundWindow
                ? WindowsNative.ReadForegroundWindow()
                : new WindowsNative.ForegroundWindowInfo();
            double usedRam = 0;
            double totalRam = 0;
            bool memoryAvailable = senses.WatchSystemStats &&
                                   WindowsNative.TryReadMemoryUsage(out usedRam, out totalRam);
            int batteryPercent = -1;
            bool? onAcPower = null;
            bool powerAvailable = senses.WatchSystemStats &&
                                  WindowsNative.TryReadPower(out batteryPercent, out onAcPower);
            double cpuPercent = 0;
            bool cpuAvailable = senses.WatchSystemStats && _cpuSampler.TryReadPercent(out cpuPercent);
            double idleSeconds = 0;
            bool idleAvailable = senses.WatchIdleState && WindowsNative.TryReadIdleSeconds(out idleSeconds);

            string mediaTitle;
            string mediaArtist;
            string mediaApplication;
            string mediaPlaybackStatus;
            bool mediaObservationAvailable;
            bool mediaSessionPresent;
            DateTimeOffset? mediaObservedAt;
            lock (_snapshotLock)
            {
                mediaTitle = _current.MediaTitle;
                mediaArtist = _current.MediaArtist;
                mediaApplication = _current.MediaApplication;
                mediaPlaybackStatus = _current.MediaPlaybackStatus;
                mediaObservationAvailable = _current.MediaObservationAvailable;
                mediaSessionPresent = _current.MediaSessionPresent;
                mediaObservedAt = _current.MediaObservedAt;
            }

            SituationSnapshot snapshot = new()
            {
                ActiveWindowTitle = window.WindowTitle,
                ActiveProcessName = window.ProcessName,
                ActiveApplicationName = string.IsNullOrWhiteSpace(window.ApplicationName)
                    ? window.ProcessName
                    : char.ToUpperInvariant(window.ApplicationName[0]) + window.ApplicationName[1..],
                ForegroundAvailable = !needsForegroundWindow || window.IsAvailable,
                IdleSeconds = idleAvailable ? idleSeconds : 0,
                IdleAvailable = idleAvailable,
                IsUserIdle = idleAvailable && idleSeconds >= 120,
                CpuPercent = cpuPercent,
                CpuAvailable = cpuAvailable,
                RamUsedMegabytes = memoryAvailable ? usedRam : 0,
                RamTotalMegabytes = memoryAvailable ? totalRam : 0,
                MemoryAvailable = memoryAvailable,
                BatteryPercent = batteryPercent,
                IsOnAcPower = onAcPower,
                PowerAvailable = powerAvailable,
                MediaTitle = mediaTitle,
                MediaArtist = mediaArtist,
                MediaApplication = mediaApplication,
                MediaPlaybackStatus = mediaPlaybackStatus,
                MediaObservationAvailable = mediaObservationAvailable,
                MediaSessionPresent = mediaSessionPresent,
                MediaObservedAt = mediaObservedAt,
                VisionMode = senses.VisionMode,
                CapturedAt = DateTimeOffset.Now
            };

            ForegroundGame = DetectGame(window.ProcessName);

            string windowSignature = window.ProcessName + "|" + window.WindowTitle;
            if (windowSignature != _lastWindowSignature)
            {
                _lastWindowSignature = windowSignature;
                _events.Publish(new SensesEvent
                {
                    SenseName = "window",
                    Summary = $"{snapshot.ActiveApplicationName}: {Shorten(window.WindowTitle, 80)}"
                });
            }

            PublishAndStore(snapshot);

            if (senses.WatchMediaSession)
            {
                _ = RefreshMediaAsync();
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Senses", $"Ambient refresh failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _ambientRefreshInProgress, 0);
        }
    }

    private async Task RefreshMediaAsync()
    {
        if (!_isRunning || Interlocked.Exchange(ref _mediaRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            MediaSessionReadResult read = await _mediaSensor.ReadWithStatusAsync().ConfigureAwait(false);
            if (!_isRunning)
            {
                return;
            }

            SituationSnapshot snapshot;
            lock (_snapshotLock)
            {
                snapshot = Clone(_current);
            }

            snapshot.MediaObservedAt = DateTimeOffset.Now;
            snapshot.MediaObservationAvailable = read.IsAvailable;
            snapshot.MediaSessionPresent = read.IsAvailable && read.Session is not null;

            if (!read.IsAvailable)
            {
                PublishAndStore(snapshot);
                return;
            }

            if (read.Session is null || !read.Session.HasContent)
            {
                snapshot.MediaTitle = string.Empty;
                snapshot.MediaArtist = string.Empty;
                snapshot.MediaApplication = string.Empty;
                snapshot.MediaPlaybackStatus = string.Empty;
                PublishAndStore(snapshot);
                return;
            }

            snapshot.MediaTitle = read.Session.Title;
            snapshot.MediaArtist = read.Session.Artist;
            snapshot.MediaApplication = ExtractApplicationName(read.Session.ApplicationId);
            snapshot.MediaPlaybackStatus = read.Session.PlaybackStatus;

            string mediaSignature = snapshot.MediaTitle + "|" + snapshot.MediaPlaybackStatus;
            if (mediaSignature != _lastMediaSignature)
            {
                _lastMediaSignature = mediaSignature;
                _events.Publish(new SensesEvent
                {
                    SenseName = "media",
                    Summary = $"{snapshot.MediaTitle} — {snapshot.MediaApplication} ({snapshot.MediaPlaybackStatus})"
                });
            }

            PublishAndStore(snapshot);
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Media refresh failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _mediaRefreshInProgress, 0);
        }
    }

    private void PublishAndStore(SituationSnapshot snapshot)
    {
        bool changed;
        lock (_snapshotLock)
        {
            changed = _current.ActiveWindowTitle != snapshot.ActiveWindowTitle ||
                      _current.ActiveProcessName != snapshot.ActiveProcessName ||
                      _current.ActiveApplicationName != snapshot.ActiveApplicationName ||
                      _current.MediaTitle != snapshot.MediaTitle ||
                      _current.MediaArtist != snapshot.MediaArtist ||
                      _current.MediaApplication != snapshot.MediaApplication ||
                      _current.MediaPlaybackStatus != snapshot.MediaPlaybackStatus ||
                      _current.ForegroundAvailable != snapshot.ForegroundAvailable ||
                      _current.IdleAvailable != snapshot.IdleAvailable ||
                      _current.CpuAvailable != snapshot.CpuAvailable ||
                      _current.MemoryAvailable != snapshot.MemoryAvailable ||
                      _current.PowerAvailable != snapshot.PowerAvailable ||
                      _current.BatteryPercent != snapshot.BatteryPercent ||
                      _current.IsOnAcPower != snapshot.IsOnAcPower ||
                      _current.MediaObservationAvailable != snapshot.MediaObservationAvailable ||
                      _current.MediaSessionPresent != snapshot.MediaSessionPresent ||
                      Math.Abs(_current.IdleSeconds - snapshot.IdleSeconds) > 30 ||
                      Math.Abs(_current.CpuPercent - snapshot.CpuPercent) >= 1 ||
                      Math.Abs(_current.RamUsedMegabytes - snapshot.RamUsedMegabytes) >= 16;
            _current = snapshot;
            _hasAmbientObservation = true;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            SituationUpdated?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Senses", $"A situation subscriber failed: {exception.Message}");
        }
    }

    private string DetectGame(string processName)
    {
        SystemSettings system = _settings.Current.System;
        if (!system.GamingModeEnabled || !system.GamingAutoDetect)
        {
            return string.Empty;
        }

        string normalized = NormalizeExecutable(processName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (system.GamingExclusions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return system.KnownGameExecutables.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : string.Empty;
    }

    private static string NormalizeExecutable(string executableName)
    {
        string trimmed = (executableName ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.EndsWith(".exe", StringComparison.Ordinal) ? trimmed : trimmed + ".exe";
    }

    private static string ExtractApplicationName(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return "unknown player";
        }

        int lastSlash = applicationId.LastIndexOf('\\');
        string name = lastSlash >= 0 ? applicationId[(lastSlash + 1)..] : applicationId;
        return name.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static SituationSnapshot Clone(SituationSnapshot source) => new()
    {
        ActiveWindowTitle = source.ActiveWindowTitle,
        ActiveProcessName = source.ActiveProcessName,
        ActiveApplicationName = source.ActiveApplicationName,
        ForegroundAvailable = source.ForegroundAvailable,
        IdleSeconds = source.IdleSeconds,
        IdleAvailable = source.IdleAvailable,
        IsUserIdle = source.IsUserIdle,
        CpuPercent = source.CpuPercent,
        CpuAvailable = source.CpuAvailable,
        RamUsedMegabytes = source.RamUsedMegabytes,
        RamTotalMegabytes = source.RamTotalMegabytes,
        MemoryAvailable = source.MemoryAvailable,
        BatteryPercent = source.BatteryPercent,
        IsOnAcPower = source.IsOnAcPower,
        PowerAvailable = source.PowerAvailable,
        MediaTitle = source.MediaTitle,
        MediaArtist = source.MediaArtist,
        MediaApplication = source.MediaApplication,
        MediaPlaybackStatus = source.MediaPlaybackStatus,
        MediaObservationAvailable = source.MediaObservationAvailable,
        MediaSessionPresent = source.MediaSessionPresent,
        MediaObservedAt = source.MediaObservedAt,
        VisionMode = source.VisionMode,
        CapturedAt = source.CapturedAt
    };

    private static string Shorten(string text, int maximumLength) =>
        string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= maximumLength ? text : text[..maximumLength].TrimEnd() + "…";
}

/// <summary>The ambient state of the machine at one moment.</summary>
public sealed class SituationSnapshot
{
    public string ActiveWindowTitle { get; set; } = string.Empty;
    public string ActiveProcessName { get; set; } = string.Empty;
    public string ActiveApplicationName { get; set; } = string.Empty;
    public bool ForegroundAvailable { get; set; }
    public double IdleSeconds { get; set; }
    public bool IdleAvailable { get; set; }
    public bool IsUserIdle { get; set; }
    public double CpuPercent { get; set; }
    public bool CpuAvailable { get; set; }
    public double RamUsedMegabytes { get; set; }
    public double RamTotalMegabytes { get; set; }
    public bool MemoryAvailable { get; set; }
    public int BatteryPercent { get; set; } = -1;
    public bool? IsOnAcPower { get; set; }
    public bool PowerAvailable { get; set; }
    public string MediaTitle { get; set; } = string.Empty;
    public string MediaArtist { get; set; } = string.Empty;
    public string MediaApplication { get; set; } = string.Empty;
    public string MediaPlaybackStatus { get; set; } = string.Empty;
    public bool MediaObservationAvailable { get; set; }
    public bool MediaSessionPresent { get; set; }
    public DateTimeOffset? MediaObservedAt { get; set; }
    public string VisionMode { get; set; } = "ondemand";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>One sensing source's configured state and most recent evidence.</summary>
public sealed record PerceptionSourceHealth(
    string Name,
    string Status,
    string Detail,
    DateTimeOffset? LastObservedAt);
