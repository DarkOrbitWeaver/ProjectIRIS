using System.Diagnostics;
using System.Text;
using CompanionOS.Core.Common;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Modules.BuiltIn;

/// <summary>
/// Everyday hands: timers, notes and launching programs.
///
/// The tiers here matter. Notes and timers are harmless and run instantly. Launching
/// an application is system-level, so it stops and asks the user first — every time,
/// unless they have granted that module permanent permission.
/// </summary>
public sealed class SystemControlModule : IAssistantModule, IModuleSurfaceProvider, IDisposable
{
    private readonly List<CompanionTimer> _timers = new();
    private readonly object _timerLock = new();
    private readonly ActiveTimerModel _activeTimer = new();
    private ModuleContext? _context;
    private Timer? _surfaceTimer;
    private int _timerSequence;

    public string Id => "system";

    public string DisplayName => "System Control";

    public string Purpose => "Timers, quick notes, app launching and machine status.";

    public string CapabilityDescription =>
        "timer alarm remind countdown note memo remember write down open launch start app program " +
        "status battery memory cpu how long find file where is locate download document";

    public IReadOnlyList<ModuleTool> Tools { get; } = new List<ModuleTool>
    {
        new(
            "set_timer",
            "Set a timer that speaks when it finishes.",
            ToolTier.Reflex,
            """{"type":"object","properties":{"minutes":{"type":"number"},"label":{"type":"string"}},"required":["minutes"]}""",
            "timer", "remind", "minutes", "countdown"),

        new("list_timers", "List the timers currently running.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "timers", "what is running"),

        new("cancel_timers", "Cancel all running timers.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "cancel timer", "stop timer"),

        new("cancel_timer", "Cancel one running timer by its label.", ToolTier.Reflex,
            """{"type":"object","properties":{"label":{"type":"string"}},"required":["label"]}""",
            "cancel my", "cancel the", "stop my timer"),

        new("snooze_timer", "Add minutes to the next timer that is about to finish.", ToolTier.Reflex,
            """{"type":"object","properties":{"minutes":{"type":"number"}},"required":[]}""",
            "snooze timer", "snooze", "add time"),

        new("show_timer_overlay", "Show the small timer controls while a timer is running.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "show timer", "show countdown"),

        new("hide_timer_overlay", "Hide the small timer controls without cancelling the timer.", ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""", "hide timer", "minimize timer"),

        new(
            "write_note",
            "Append a note to her notebook in the workspace.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"text":{"type":"string"},"title":{"type":"string"}},"required":["text"]}""",
            "note", "write down", "memo", "save this"),

        new("read_notes", "Read back the notes in her notebook.", ToolTier.Scoped,
            """{"type":"object","properties":{},"required":[]}""", "read notes", "notebook"),

        new(
            "open_application",
            "Launch an application by name from the allowed set: calculator, notepad, paint, " +
            "chrome, edge, explorer, task manager, terminal, cmd. Use the app's name, not a path.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"name":{"type":"string","description":"Application name, e.g. calculator"}},"required":["name"]}""",
            "open app", "launch", "start program", "open calculator", "open the calculator"),

        new(
            "system_status",
            "Report CPU, memory, battery and what the machine is doing.",
            ToolTier.Reflex,
            """{"type":"object","properties":{},"required":[]}""",
            "status", "ram", "cpu", "battery", "how is my pc"),

        new(
            "find_files",
            "Find files on the user's own folders by name. Read-only search.",
            ToolTier.Scoped,
            """{"type":"object","properties":{"query":{"type":"string","description":"Part of the file name"},"folder":{"type":"string","description":"One of: desktop, documents, downloads, music, pictures, videos. Empty searches all of them."}},"required":["query"]}""",
            "find file", "where is", "locate", "my file", "did i save", "show me the file")
        { Access = ToolAccess.Reads }
    };

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _surfaceTimer = new Timer(_ => UpdateTimerSurface(), null, Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public IReadOnlyList<ModuleSurface> GetSurfaces() => new[]
    {
        new ModuleSurface
        {
            Id = "system.timer",
            Title = "Timer",
            Slot = ShellSlot.Overlay,
            Model = _activeTimer
        }
    };

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        switch (invocation.Tool.Name)
        {
            case "set_timer":
                return Task.FromResult(StartTimer(invocation));

            case "list_timers":
                return Task.FromResult(ListTimers());

            case "cancel_timers":
                CancelAllTimers();
                return Task.FromResult(ToolResult.Ok("All timers cancelled.", "Timers cleared."));

            case "cancel_timer":
                return Task.FromResult(CancelTimer(invocation.GetString("label")));

            case "snooze_timer":
                return Task.FromResult(SnoozeNextTimer(ParseDouble(invocation.GetString("minutes"), 5)));

            case "show_timer_overlay":
                return Task.FromResult(SetTimerOverlayVisible(true));

            case "hide_timer_overlay":
                return Task.FromResult(SetTimerOverlayVisible(false));

            case "write_note":
                return Task.FromResult(WriteNote(invocation, cancellationToken));

            case "read_notes":
                return Task.FromResult(ReadNotes());

            case "open_application":
                return Task.FromResult(OpenApplication(invocation));

            case "system_status":
                return Task.FromResult(ToolResult.Ok(
                    _context?.Host.DescribeSituation() ?? "No sensor data available."));

            case "find_files":
                return Task.FromResult(FindFiles(invocation, cancellationToken));

            default:
                return Task.FromResult(ToolResult.Fail($"Unknown system tool '{invocation.Tool.Name}'."));
        }
    }

    public Task ShutdownAsync()
    {
        CancelAllTimers();
        _surfaceTimer?.Dispose();
        _surfaceTimer = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CancelAllTimers();
        _surfaceTimer?.Dispose();
    }

    private string NotebookPath =>
        Path.Combine(_context?.WorkspaceDirectory ?? Path.GetTempPath(), "notebook.md");

    /// <summary>
    /// Read-only name search across the user's real folders. No indexer, no database:
    /// a wildcard walk that stops at 15 hits, so it stays instant on ordinary folders
    /// and honest about what it did not find.
    /// </summary>
    private ToolResult FindFiles(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string query = invocation.GetString("query").Trim();
        if (query.Length == 0)
        {
            return ToolResult.Fail("Tell me part of the file name to look for.");
        }

        List<(string Root, string Label)> roots = CollectSearchRoots(invocation.GetString("folder").Trim());
        if (roots.Count == 0 || query.Length < 2)
        {
            return ToolResult.Fail("Name the folder to search: desktop, documents, downloads, music, " +
                                   "pictures or videos (or clear it to search all of them).");
        }

        List<string> hits = new();
        foreach ((string root, string label) in roots)
        {
            if (hits.Count >= 15)
            {
                break;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory
                             .EnumerateFiles(root, $"*{query}*", SearchOption.AllDirectories))
                {
                    // The caller's token is honoured between entries: when the 45-second
                    // tool ceiling or a cancelled turn stops the wait, this sweep stops
                    // walking the disk too, instead of carrying on with nobody
                    // listening (2026-10-27 audit).
                    cancellationToken.ThrowIfCancellationRequested();
                    hits.Add(file);
                    if (hits.Count >= 15)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Deliberate stop - rethrow so the module host records the cancellation
                // as exactly that, not as a failed search.
                throw;
            }
            catch (Exception exception)
            {
                CompanionLog.Trace("System", $"Skipping '{label}' while searching: {exception.Message}");
            }
        }

        string searched = string.Join(", ", roots.Select(rootLabel => rootLabel.Label));
        if (hits.Count == 0)
        {
            return ToolResult.Ok($"Nothing named '{query}' in: {searched}.");
        }

        string listing = string.Join("\n", hits);
        return ToolResult.Ok(
            $"{hits.Count} file(s) found{(hits.Count == 15 ? " (stopped at 15)" : "")}:\n{listing}",
            spokenText: hits.Count == 1 ? $"Found it: {Path.GetFileName(hits[0])}." : null,
            displayText: listing);
    }

    private static List<(string Root, string Label)> CollectSearchRoots(string folderName)
    {
        List<(string Root, string Label)> all = new()
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "documents"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "downloads"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "music"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "videos")
        };

        all = all.Where(pair => !string.IsNullOrWhiteSpace(pair.Root)).ToList();
        string requested = folderName.ToLowerInvariant();
        return requested.Length == 0
            ? all
            : all.Where(pair => pair.Label.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private ToolResult StartTimer(ToolInvocation invocation)
    {
        double minutes = Math.Max(0.05, ParseDouble(invocation.GetString("minutes"), 5));
        string label = invocation.GetString("label").Trim();
        if (label.Length == 0)
        {
            label = "Your timer";
        }

        int timerNumber = Interlocked.Increment(ref _timerSequence);
        CompanionTimer timer = new()
        {
            Number = timerNumber,
            Label = label,
            FinishesAt = DateTimeOffset.Now.AddMinutes(minutes)
        };

        timer.Handle = new Timer(_ => OnTimerElapsed(timer), null,
            TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);

        lock (_timerLock)
        {
            _timers.Add(timer);
        }
        UpdateTimerSurface();
        _surfaceTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));

        string spoken = minutes < 1
            ? $"Timer set for {Math.Round(minutes * 60)} seconds."
            : $"Timer set for {minutes:0.#} minutes.";

        return ToolResult.Ok($"{spoken} Label: {label}.", spoken);
    }

    private ToolResult ListTimers()
    {
        List<CompanionTimer> active;
        lock (_timerLock)
        {
            active = _timers.Where(timer => !timer.IsFinished).ToList();
        }

        if (active.Count == 0)
        {
            return ToolResult.Ok("No timers are running.");
        }

        string listing = string.Join("; ", active.Select(timer =>
            $"{timer.Label} in {Math.Max(0, (timer.FinishesAt - DateTimeOffset.Now).TotalMinutes):0.#} min"));
        return ToolResult.Ok(listing, displayText: listing);
    }

    private void OnTimerElapsed(CompanionTimer timer)
    {
        lock (_timerLock)
        {
            timer.IsFinished = true;
            _timers.Remove(timer);
        }

        timer.Handle?.Dispose();
        UpdateTimerSurface();

        if (_context is null)
        {
            return;
        }

        _context.Events.PublishNotice($"{timer.Label} finished.", NoticeSeverity.Success);
        _ = SpeakTimerCompletionAsync(timer);
    }

    /// <summary>
    /// The timer's spoken announcement is observed: the notice already went out, so a
    /// failed SPEECH is a cosmetic loss - but it is logged, not vanished silently into
    /// a discarded task (2026-10-27 audit).
    /// </summary>
    private async Task SpeakTimerCompletionAsync(CompanionTimer timer)
    {
        ModuleContext? context = _context;
        if (context is null)
        {
            return;
        }

        try
        {
            await context.Host.SpeakAsync($"{timer.Label} is done, Boss.", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("System", $"Could not speak the timer completion: {exception.Message}");
        }
    }

    private void CancelAllTimers()
    {
        List<CompanionTimer> toCancel;
        lock (_timerLock)
        {
            toCancel = _timers.ToList();
            _timers.Clear();
        }

        foreach (CompanionTimer timer in toCancel)
        {
            timer.IsFinished = true;
            timer.Handle?.Dispose();
        }

        UpdateTimerSurface();
    }

    private ToolResult CancelTimer(string label)
    {
        string query = label.Trim();
        if (query.Length == 0)
        {
            return ToolResult.Fail("Tell me which timer to cancel, or say cancel all timers.");
        }

        CompanionTimer? timer;
        lock (_timerLock)
        {
            timer = _timers.FirstOrDefault(candidate =>
                candidate.Label.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (timer is not null)
            {
                timer.IsFinished = true;
                _timers.Remove(timer);
            }
        }

        if (timer is null)
        {
            return ToolResult.Fail($"I could not find a running timer called '{label}'.");
        }

        timer.Handle?.Dispose();
        UpdateTimerSurface();
        return ToolResult.Ok($"Cancelled '{timer.Label}'.", $"Cancelled {timer.Label}.");
    }

    private ToolResult SnoozeNextTimer(double minutes)
    {
        minutes = Math.Clamp(minutes, 0.05, 120);
        CompanionTimer? timer;
        lock (_timerLock)
        {
            timer = _timers.Where(candidate => !candidate.IsFinished)
                .OrderBy(candidate => candidate.FinishesAt).FirstOrDefault();
            if (timer is not null)
            {
                timer.FinishesAt = timer.FinishesAt.AddMinutes(minutes);
                TimeSpan due = timer.FinishesAt - DateTimeOffset.Now;
                timer.Handle?.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        if (timer is null)
        {
            return ToolResult.Fail("There is no running timer to snooze.");
        }

        UpdateTimerSurface();
        return ToolResult.Ok($"Added {minutes:0.#} minutes to '{timer.Label}'.",
            $"Okay, {timer.Label} has {minutes:0.#} more minutes.");
    }

    private ToolResult SetTimerOverlayVisible(bool visible)
    {
        if (_context is null)
        {
            return ToolResult.Fail("Timer controls are not ready yet.");
        }

        _context.MutateSettings(settings =>
        {
            if (settings.Interface.ShowTimerOverlay == visible)
            {
                return false;
            }

            settings.Interface.ShowTimerOverlay = visible;
            return true;
        });
        return ToolResult.Ok(visible ? "Timer overlay shown." : "Timer overlay hidden; the timer keeps running.",
            visible ? "Timer controls are back." : "Timer controls hidden. The timer keeps running.");
    }

    private void UpdateTimerSurface()
    {
        CompanionTimer? next;
        lock (_timerLock)
        {
            next = _timers.Where(timer => !timer.IsFinished)
                .OrderBy(timer => timer.FinishesAt).FirstOrDefault();
        }

        if (next is null)
        {
            _activeTimer.IsActive = false;
            _activeTimer.Label = "No timer running";
            _activeTimer.RemainingText = string.Empty;
            _activeTimer.RemainingSeconds = 0;
            _surfaceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        TimeSpan remaining = next.FinishesAt - DateTimeOffset.Now;
        int seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        _activeTimer.IsActive = true;
        _activeTimer.Label = next.Label;
        _activeTimer.RemainingSeconds = seconds;
        _activeTimer.RemainingText = FormatRemaining(seconds);
    }

    private static string FormatRemaining(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds % 3600 / 60:D2}:{seconds % 60:D2} remaining"
        : $"{seconds / 60}:{seconds % 60:D2} remaining";

    private ToolResult WriteNote(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string text = invocation.GetString("text").Trim();
        if (text.Length == 0)
        {
            return ToolResult.Fail("There was nothing to write down.");
        }

        string title = invocation.GetString("title").Trim();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotebookPath)!);
            StringBuilder entry = new();
            entry.AppendLine($"## {DateTimeOffset.Now:yyyy-MM-dd HH:mm}" +
                             (title.Length > 0 ? $" — {title}" : string.Empty));
            entry.AppendLine(text);
            entry.AppendLine();
            File.AppendAllText(NotebookPath, entry.ToString());
            return ToolResult.Ok("Note saved.", "Written down.");
        }
        catch (Exception exception)
        {
            return ToolResult.Fail($"The note could not be saved: {exception.Message}");
        }
    }

    private ToolResult ReadNotes()
    {
        try
        {
            if (!File.Exists(NotebookPath))
            {
                return ToolResult.Ok("The notebook is empty.");
            }

            string[] lines = File.ReadAllLines(NotebookPath);
            string tail = string.Join(Environment.NewLine, lines.TakeLast(40));
            return ToolResult.Ok(tail, displayText: tail);
        }
        catch (Exception exception)
        {
            return ToolResult.Fail($"The notebook could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Names the shell resolves to real applications, so "calculator" opens Calculator
    /// instead of failing on an all-numeric command name. The lower-case keys are also
    /// how the tool description advertises itself, keeping the model and the launcher
    /// in agreement about what is openable.
    /// </summary>
    private static readonly Dictionary<string, string> AppAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["calculator"] = "calc.exe",
            ["calc"] = "calc.exe",
            ["notepad"] = "notepad.exe",
            ["paint"] = "mspaint.exe",
            ["mspaint"] = "mspaint.exe",
            ["chrome"] = "chrome",
            ["edge"] = "msedge",
            ["explorer"] = "explorer.exe",
            ["files"] = "explorer.exe",
            ["task manager"] = "taskmgr.exe",
            ["taskmanager"] = "taskmgr.exe",
            ["terminal"] = "wt.exe",
            ["cmd"] = "cmd.exe"
        };

    /// <summary>
    /// Launches only what the allow-list names. Per the design the owner set: opening an
    /// app is harmless and reversible, so it runs on his word alone — but launching an
    /// arbitrary path would let a misread sentence run anything, so anything the list
    /// does not cover is refused honestly.
    /// </summary>
    private ToolResult OpenApplication(ToolInvocation invocation)
    {
        string name = invocation.GetString("name").Trim();
        if (name.Length == 0)
        {
            return ToolResult.Fail("Which application should I open?");
        }

        if (!AppAliases.TryGetValue(name, out string? executable))
        {
            return ToolResult.Fail(
                $"I can only open applications from my allowed set: {string.Join(", ",
                    AppAliases.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}. " +
                $"I did not open anything.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            return ToolResult.Ok($"Opened {name}.", $"Opening {name}.");
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("System", $"Could not launch '{name}' ({executable}): {exception.Message}");
            return ToolResult.Fail($"I could not open {name}.");
        }
    }

    private static double ParseDouble(string text, double fallback)
    {
        // Culture is not an opinion here: on a comma-decimal OS locale the old
        // culture-sensitive parse read "5.5" as 55 - a five-and-a-half-minute timer
        // confidently announced as an hour (2026-10-27 audit). Parse invariant, but
        // accept BOTH decimal marks, because spoken text and typed text both arrive.
        return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    /// <summary>One running timer.</summary>
    private sealed class CompanionTimer
    {
        public int Number { get; init; }
        public string Label { get; init; } = string.Empty;
        public DateTimeOffset FinishesAt { get; set; }
        public bool IsFinished { get; set; }
        public Timer? Handle { get; set; }
    }
}

/// <summary>The next timer, exposed as one live model so every surface reads the same truth.</summary>
public sealed class ActiveTimerModel : ObservableObject
{
    private bool _isActive;
    private string _label = "No timer running";
    private string _remainingText = string.Empty;
    private int _remainingSeconds;

    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string RemainingText { get => _remainingText; set => SetProperty(ref _remainingText, value); }
    public int RemainingSeconds { get => _remainingSeconds; set => SetProperty(ref _remainingSeconds, value); }
}
