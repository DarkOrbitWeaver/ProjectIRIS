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
    private readonly MediaSessionSensor _mediaSensor = new();
    private readonly object _snapshotLock = new();

    private Timer? _ambientTimer;
    private SituationSnapshot _current = new();
    private ScreenFrame? _lastFrame;
    private string _lastWindowSignature = string.Empty;
    private string _lastMediaSignature = string.Empty;
    private bool _isRunning;
    // Timers are allowed to fire again while a slow Windows/media call is still in
    // flight.  Never queue a second copy of the same passive observation: stale
    // telemetry is preferable to stealing another worker or I/O slot from a game.
    private int _ambientRefreshInProgress;
    private int _mediaRefreshInProgress;
    private int _mediaPausedForVoice;

    public PerceptionEngine(SettingsStore settings, EventBus events)
    {
        _settings = settings;
        _events = events;
    }

    /// <summary>Raised whenever the ambient snapshot changes.</summary>
    public event Action<SituationSnapshot>? SituationUpdated;

    /// <summary>The most recent ambient snapshot.</summary>
    public SituationSnapshot Current
    {
        get { lock (_snapshotLock) { return _current; } }
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
    /// Looks at the screen. Honors the vision mode and skips unchanged frames.
    /// Returns the frame she should reason about, or null when she cannot see.
    /// </summary>
    public async Task<ScreenFrame?> LookAsync(bool forceFreshCapture, CancellationToken cancellationToken = default)
    {
        SenseSettings senses = _settings.Current.Senses;

        if (string.Equals(senses.VisionMode, "off", StringComparison.OrdinalIgnoreCase) && !forceFreshCapture)
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
        if (!_settings.Current.Voice.PauseMediaWhenListening ||
            Interlocked.CompareExchange(ref _mediaPausedForVoice, 0, 0) != 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool paused = await _mediaSensor.TryPauseAsync().ConfigureAwait(false);
        if (paused)
        {
            Interlocked.Exchange(ref _mediaPausedForVoice, 1);
            _events.Publish(new SensesEvent { SenseName = "media", Summary = "Paused by Iris while listening." });
        }

        return paused;
    }

    /// <summary>Returns only media Iris paused for the current voice interaction.</summary>
    public async Task<bool> ResumeMediaAfterVoiceAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.Voice.ResumeMediaAfterVoiceTurn ||
            Interlocked.Exchange(ref _mediaPausedForVoice, 0) == 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool resumed = await _mediaSensor.TryPlayAsync().ConfigureAwait(false);
        if (resumed)
        {
            _events.Publish(new SensesEvent { SenseName = "media", Summary = "Resumed after Iris finished." });
        }

        return resumed;
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
    public string DescribeForPrompt()
    {
        SituationSnapshot snapshot = Current;
        SenseSettings senses = _settings.Current.Senses;
        List<string> lines = new();

        if (senses.WatchActiveWindow)
        {
            string application = string.IsNullOrWhiteSpace(snapshot.ActiveApplicationName)
                ? "the desktop"
                : snapshot.ActiveApplicationName;
            lines.Add($"Foreground: {application}" +
                      (string.IsNullOrWhiteSpace(snapshot.ActiveWindowTitle)
                          ? "."
                          : $" — window \"{Shorten(snapshot.ActiveWindowTitle, 90)}\"."));
        }

        if (senses.WatchMediaSession && !string.IsNullOrWhiteSpace(snapshot.MediaTitle))
        {
            string state = string.IsNullOrWhiteSpace(snapshot.MediaPlaybackStatus)
                ? string.Empty
                : $" ({snapshot.MediaPlaybackStatus.ToLowerInvariant()})";
            string artist = string.IsNullOrWhiteSpace(snapshot.MediaArtist) ? string.Empty : $" by {snapshot.MediaArtist}";
            lines.Add($"Media: \"{snapshot.MediaTitle}\"{artist}{state}.");
        }

        if (senses.WatchSystemStats && snapshot.RamTotalMegabytes > 0)
        {
            lines.Add($"Machine: CPU {snapshot.CpuPercent:0}%, RAM {snapshot.RamUsedMegabytes / 1024:0.0} of " +
                      $"{snapshot.RamTotalMegabytes / 1024:0.0} GB used, battery {snapshot.BatteryPercent}% " +
                      $"({(snapshot.IsOnAcPower ? "on mains" : "on battery")}).");
        }

        if (senses.WatchIdleState && snapshot.IdleSeconds >= 60)
        {
            lines.Add($"Boss has been idle for about {snapshot.IdleSeconds / 60:0} minutes.");
        }

        if (!string.IsNullOrWhiteSpace(ForegroundGame))
        {
            lines.Add($"A game is running in front: {ForegroundGame}.");
        }

        lines.Add($"Vision is {_settings.Current.Senses.VisionMode}; local time {DateTime.Now:HH:mm}.");
        return string.Join(Environment.NewLine, lines);
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
            (double usedRam, double totalRam) = senses.WatchSystemStats
                ? WindowsNative.ReadMemoryUsage()
                : (0, 0);
            (int batteryPercent, bool onAcPower) = senses.WatchSystemStats
                ? WindowsNative.ReadPower()
                : (100, true);
            double cpuPercent = senses.WatchSystemStats ? _cpuSampler.ReadPercent() : 0;
            double idleSeconds = senses.WatchIdleState ? WindowsNative.ReadIdleSeconds() : 0;

            string mediaTitle;
            string mediaArtist;
            string mediaApplication;
            string mediaPlaybackStatus;
            lock (_snapshotLock)
            {
                mediaTitle = _current.MediaTitle;
                mediaArtist = _current.MediaArtist;
                mediaApplication = _current.MediaApplication;
                mediaPlaybackStatus = _current.MediaPlaybackStatus;
            }

            SituationSnapshot snapshot = new()
            {
                ActiveWindowTitle = window.WindowTitle,
                ActiveProcessName = window.ProcessName,
                ActiveApplicationName = string.IsNullOrWhiteSpace(window.ApplicationName)
                    ? window.ProcessName
                    : char.ToUpperInvariant(window.ApplicationName[0]) + window.ApplicationName[1..],
                IdleSeconds = idleSeconds,
                IsUserIdle = idleSeconds >= 120,
                CpuPercent = cpuPercent,
                RamUsedMegabytes = usedRam,
                RamTotalMegabytes = totalRam,
                BatteryPercent = batteryPercent,
                IsOnAcPower = onAcPower,
                MediaTitle = mediaTitle,
                MediaArtist = mediaArtist,
                MediaApplication = mediaApplication,
                MediaPlaybackStatus = mediaPlaybackStatus,
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
            MediaSessionSnapshot? media = await _mediaSensor.ReadAsync().ConfigureAwait(false);
            if (!_isRunning)
            {
                return;
            }

            SituationSnapshot snapshot;
            lock (_snapshotLock)
            {
                snapshot = Clone(_current);
            }

            if (media is null || !media.HasContent)
            {
                snapshot.MediaTitle = string.Empty;
                snapshot.MediaArtist = string.Empty;
                snapshot.MediaApplication = string.Empty;
                snapshot.MediaPlaybackStatus = string.Empty;
                PublishAndStore(snapshot);
                return;
            }

            snapshot.MediaTitle = media.Title;
            snapshot.MediaArtist = media.Artist;
            snapshot.MediaApplication = ExtractApplicationName(media.ApplicationId);
            snapshot.MediaPlaybackStatus = media.PlaybackStatus;

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
                      _current.MediaTitle != snapshot.MediaTitle ||
                      _current.MediaPlaybackStatus != snapshot.MediaPlaybackStatus ||
                      Math.Abs(_current.IdleSeconds - snapshot.IdleSeconds) > 30;
            _current = snapshot;
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
        IdleSeconds = source.IdleSeconds,
        IsUserIdle = source.IsUserIdle,
        CpuPercent = source.CpuPercent,
        RamUsedMegabytes = source.RamUsedMegabytes,
        RamTotalMegabytes = source.RamTotalMegabytes,
        BatteryPercent = source.BatteryPercent,
        IsOnAcPower = source.IsOnAcPower,
        MediaTitle = source.MediaTitle,
        MediaArtist = source.MediaArtist,
        MediaApplication = source.MediaApplication,
        MediaPlaybackStatus = source.MediaPlaybackStatus,
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
    public double IdleSeconds { get; set; }
    public bool IsUserIdle { get; set; }
    public double CpuPercent { get; set; }
    public double RamUsedMegabytes { get; set; }
    public double RamTotalMegabytes { get; set; }
    public int BatteryPercent { get; set; } = 100;
    public bool IsOnAcPower { get; set; } = true;
    public string MediaTitle { get; set; } = string.Empty;
    public string MediaArtist { get; set; } = string.Empty;
    public string MediaApplication { get; set; } = string.Empty;
    public string MediaPlaybackStatus { get; set; } = string.Empty;
    public string VisionMode { get; set; } = "ondemand";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
}
