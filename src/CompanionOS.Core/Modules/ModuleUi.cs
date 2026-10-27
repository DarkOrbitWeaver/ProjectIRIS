using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Modules;

/// <summary>The tone of a module's one-line notice. The Ui renders the icon from this.</summary>
public enum ModuleNoteKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// The scripting spec for one floating feedback card. A module states what it is
/// doing; the shell draws it in her visual language — icon, spinner while on the job,
/// a green check or a red alert at the end, then a quiet fade. Modules do not paint;
/// they narrate, through events only.
/// </summary>
public sealed class ModuleTaskSpec
{
    /// <summary>The card's headline, e.g. "Downloading song". May be retitled live.</summary>
    public required string Title { get; init; }

    /// <summary>Shell icon key (verified names or "png:<local file>"). Null uses her icon.</summary>
    public string? IconKey { get; init; }

    /// <summary>The first step line, e.g. the file being fetched right now.</summary>
    public string? Detail { get; init; }

    /// <summary>True only when the module can report honest percentages.</summary>
    public bool ShowsProgress { get; init; }

    /// <summary>How long a finished card stays so the user reads the outcome. Default 6 s.</summary>
    public double HoldAfterFinishSeconds { get; init; } = 6;
}

/// <summary>A live feedback card a module drives step by step. Thread-safe.</summary>
public interface IModuleTaskHandle
{
    /// <summary>The card id, also the correlation id in her log.</summary>
    string Id { get; }

    /// <summary>Changes the headline while running ("Downloading song" → "Downloading Hollowlight").</summary>
    void Retitle(string title);

    /// <summary>Replaces the detail line with what is happening right now.</summary>
    void Step(string detail);

    /// <summary>Honest progress. Total null means "current out of unknown".</summary>
    void Progress(double current, double? total = null);

    /// <summary>The work is done. The card shows the green check and this summary, then fades.</summary>
    void Complete(string summary);

    /// <summary>The work did not go through. The card shows the red alert and this reason.</summary>
    void Fail(string summary);

    /// <summary>Stopped on request (timeout, user, shutdown) — stated as what it is.</summary>
    void Cancel(string summary);
}

/// <summary>
/// The Ui scripting surface every module receives in its ModuleContext. Keeping the
/// language small and honest: narrate tasks, post finished notices. No widget tree,
/// no markup, nothing executable in what cards display.
/// </summary>
public interface IModuleUi
{
    /// <summary>Shows a feedback card for a piece of real work and returns its handle.</summary>
    IModuleTaskHandle BeginTask(ModuleTaskSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Posts one finished notice ("No user found") that fades on its own.</summary>
    void Note(ModuleNoteKind kind, string text, string? iconKey = null);
}

/// <summary>
/// The event-backed implementation. Each module gets a view bound to its identity, so
/// cards are always attributable, and every transition is written to her log receipts —
/// the durable history a faded card leaves behind.
/// </summary>
public sealed class ModuleUiService
{
    private readonly EventBus _events;

    public ModuleUiService(EventBus events) => _events = events;

    /// <summary>Binds a module's identity into its Ui surface.</summary>
    public IModuleUi ForModule(string moduleId, string moduleName, string defaultIconKey = "body") =>
        new ModuleUiView(_events, moduleId, moduleName,
            string.IsNullOrWhiteSpace(defaultIconKey) ? "body" : defaultIconKey);

    private sealed class ModuleUiView : IModuleUi
    {
        private readonly EventBus _events;
        private readonly string _moduleId;
        private readonly string _moduleName;
        private readonly string _defaultIconKey;

        public ModuleUiView(EventBus events, string moduleId, string moduleName, string defaultIconKey)
        {
            _events = events;
            _moduleId = moduleId;
            _moduleName = moduleName;
            _defaultIconKey = defaultIconKey;
        }

        public IModuleTaskHandle BeginTask(ModuleTaskSpec spec, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            return ModuleTask.Start(_events, _moduleId, _moduleName,
                string.IsNullOrWhiteSpace(spec.IconKey) ? _defaultIconKey : spec.IconKey!, spec);
        }

        public void Note(ModuleNoteKind kind, string text, string? iconKey = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            double hold = kind switch
            {
                ModuleNoteKind.Info => 4_000,
                ModuleNoteKind.Success => 4_000,
                ModuleNoteKind.Warning => 6_000,
                ModuleNoteKind.Error => 8_000,
                _ => 4_000
            };
            _events.Publish(new ModuleTaskFeedbackEvent
            {
                TaskId = "note-" + Guid.NewGuid().ToString("N")[..8],
                ModuleId = _moduleId,
                ModuleName = _moduleName,
                IconKey = string.IsNullOrWhiteSpace(iconKey) ? _defaultIconKey : iconKey!,
                Kind = ModuleFeedbackKind.Note,
                Title = text,
                HoldMilliseconds = hold,
                NoteKind = kind
            });
            CompanionLog.Info("Modules", $"Note ({kind}): {text}");
        }
    }

    /// <summary>One running task: publishes live events and honest terminal states exactly once.</summary>
    private sealed class ModuleTask : IModuleTaskHandle
    {
        private readonly EventBus _events;
        private readonly string _moduleId;
        private readonly string _moduleName;
        private readonly string _iconKey;
        private readonly double _hold;

        /// <summary>True once the card reached a terminal state; late updates are dropped,
        /// never rewritten — a finished task stays as it finished.</summary>
        private volatile bool _terminal;

        private readonly object _fieldLock = new();
        private string _title;
        private string _detail;
        private double? _progress;
        private double? _total;
        private readonly long _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        private ModuleTask(EventBus events, string moduleId, string moduleName, string iconKey,
            ModuleTaskSpec spec)
        {
            _events = events;
            _moduleId = moduleId;
            _moduleName = moduleName;
            _iconKey = iconKey;
            _title = spec.Title;
            _detail = spec.Detail ?? string.Empty;
            _hold = Math.Clamp(spec.HoldAfterFinishSeconds, 0, 60) * 1000;
        }

        public string Id { get; } = "uifeedback-" + Guid.NewGuid().ToString("N")[..8];

        public static ModuleTask Start(EventBus events, string moduleId, string moduleName,
            string iconKey, ModuleTaskSpec spec)
        {
            ModuleTask task = new(events, moduleId, moduleName, iconKey, spec);
            events.Publish(new ModuleTaskFeedbackEvent
            {
                TaskId = task.Id,
                ModuleId = moduleId,
                ModuleName = moduleName,
                IconKey = iconKey,
                Kind = ModuleFeedbackKind.TaskStarted,
                Title = task._title,
                Detail = task._detail,
                HoldMilliseconds = task._hold
            });
            IrisEvent.Log("Modules", "ui.task.begin", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["module"] = moduleId,
                    ["title"] = task._title,
                    ["detail"] = task._detail,
                    ["task_id"] = task.Id
                });
            return task;
        }

        public void Retitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _title = title;
            }

            Publish(ModuleFeedbackKind.TaskRunning, summary: null);
        }

        public void Step(string detail)
        {
            if (detail is null)
            {
                return;
            }

            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _detail = detail;
            }

            Publish(ModuleFeedbackKind.TaskRunning, summary: null);
            IrisEvent.Log("Modules", "ui.task.step", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["module"] = _moduleId,
                    ["detail"] = detail,
                    ["task_id"] = Id
                });
        }

        public void Progress(double current, double? total = null)
        {
            if (current < 0 || (total is { } known && known <= 0))
            {
                return;
            }

            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _progress = current;
                _total = total ?? _total;
            }

            Publish(ModuleFeedbackKind.TaskRunning, summary: null);
        }

        public void Complete(string summary)
        {
            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Publish(ModuleFeedbackKind.TaskCompleted, summary);
            IrisEvent.Log("Modules", "ui.task.done", LogLevel.Info,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["module"] = _moduleId,
                    ["summary"] = summary,
                    ["task_id"] = Id
                });
        }

        public void Fail(string summary)
        {
            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Publish(ModuleFeedbackKind.TaskFailed, summary);
            IrisEvent.Log("Modules", "ui.task.failed", LogLevel.Warning,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["module"] = _moduleId,
                    ["summary"] = summary,
                    ["task_id"] = Id
                });
        }

        public void Cancel(string summary)
        {
            lock (_fieldLock)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Publish(ModuleFeedbackKind.TaskCancelled, summary);
            IrisEvent.Log("Modules", "ui.task.cancelled", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["module"] = _moduleId,
                    ["summary"] = summary,
                    ["task_id"] = Id
                });
        }

        /// <summary>
        /// Publishes a coherent frame: title/detail/progress are snapshot under the same
        /// lock the mutators take, so a Step racing a Complete can never ship half-hold
        /// state across the event bus (2026-10-27 audit: the "Thread-safe." comment was
        /// aspirational).
        /// </summary>
        private void Publish(ModuleFeedbackKind kind, string? summary)
        {
            string title;
            string detail;
            double? progress;
            double? total;
            lock (_fieldLock)
            {
                title = _title;
                detail = _detail;
                progress = _progress;
                total = _total;
            }

            _events.Publish(new ModuleTaskFeedbackEvent
            {
                TaskId = Id,
                ModuleId = _moduleId,
                ModuleName = _moduleName,
                IconKey = _iconKey,
                Kind = kind,
                Title = title,
                Detail = detail,
                Progress = progress,
                Total = total,
                Summary = summary,
                HoldMilliseconds = _hold
            });
        }
    }
}
