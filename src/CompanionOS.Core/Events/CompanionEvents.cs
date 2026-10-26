using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Events;

/// <summary>
/// Base class for everything that happens inside her.
/// All events are immutable descriptions of something that already happened,
/// which keeps subscribers simple and thread-safe.
/// </summary>
public abstract class CompanionEvent
{
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now;
}

/// <summary>Her mind state changed (drives the orb, title bar and overlay text).</summary>
public sealed class MindStateChangedEvent : CompanionEvent
{
    public MindStateChangedEvent(MindState previous, MindState current, string reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    public MindState Previous { get; }
    public MindState Current { get; }
    public string Reason { get; }
}

/// <summary>Awake, Sleeping or Gaming changed.</summary>
public sealed class OperatingModeChangedEvent : CompanionEvent
{
    public OperatingModeChangedEvent(OperatingMode previous, OperatingMode current, string reason)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    public OperatingMode Previous { get; }
    public OperatingMode Current { get; }
    public string Reason { get; }
}

/// <summary>Which part of a conversation turn just changed.</summary>
public enum ConversationPhase
{
    UserMessage,
    AssistantStarted,
    AssistantDelta,
    AssistantCompleted,
    ToolStarted,
    ToolFinished,
    TurnFailed,
    TurnCancelled
}

/// <summary>
/// One stream of conversation updates. The interface batches deltas at 30 fps;
/// the brain only publishes what actually happened.
/// </summary>
public sealed class ConversationEvent : CompanionEvent
{
    public required ConversationPhase Phase { get; init; }
    public required string TurnId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public string ModuleId { get; init; } = string.Empty;
    public string IconKey { get; init; } = "body";
}

/// <summary>A memory node was created, strengthened or forgotten.</summary>
public sealed class MemoryChangedEvent : CompanionEvent
{
    public required string NodeId { get; init; }
    public required string Title { get; init; }
    public required string Change { get; init; }
}

/// <summary>What memory contributed to one turn. Message carries the chips.</summary>
public sealed class MemoryRecallEvent : CompanionEvent
{
    public MemoryRecallEvent(string turnId, IReadOnlyList<RecalledMemory> memories, string intuitionText)
    {
        TurnId = turnId;
        Memories = memories;
        IntuitionText = intuitionText;
    }

    public string TurnId { get; }
    public IReadOnlyList<RecalledMemory> Memories { get; }
    public string IntuitionText { get; }
}

/// <summary>One memory node as shown in the chat recall chip.</summary>
public sealed class RecalledMemory
{
    public required string NodeId { get; init; }
    public required string Title { get; init; }
    public required int Layer { get; init; }
    public required double Strength { get; init; }
    public string Provenance { get; init; } = string.Empty;
    public double ActivationScore { get; init; }
    public bool IsDirectMatch { get; init; }
    public string RelatedMemoryTitle { get; init; } = string.Empty;
    public string Relationship { get; init; } = string.Empty;
}

/// <summary>Raised whenever she starts or stops talking.</summary>
public sealed class SpeechEvent : CompanionEvent
{
    public required bool IsSpeaking { get; init; }
    public string Text { get; init; } = string.Empty;
    /// <summary>The originating conversation or voice interaction, when one exists.</summary>
    public string TurnId { get; init; } = string.Empty;
}

/// <summary>Live microphone or speaker level, 0.0 to 1.0, used by the orb waveform.</summary>
public sealed class AudioLevelEvent : CompanionEvent
{
    public required double Level { get; init; }
    public required bool IsMicrophone { get; init; }
}

/// <summary>A recognised or manually typed line shown in the transcript.</summary>
public sealed class TranscriptEvent : CompanionEvent
{
    public required string Text { get; init; }
    public required bool IsFinal { get; init; }
    public required bool IsUser { get; init; }
    /// <summary>Correlation key linking recognized speech to the conversation it starts.</summary>
    public string TurnId { get; init; } = string.Empty;
}

/// <summary>
/// A short-lived, user-initiated moment of attention. The overlay uses this to appear and glow
/// when Iris is called without turning ordinary passive sensing into a distracting HUD.
/// </summary>
public sealed class AttentionRequestedEvent : CompanionEvent
{
    public required string Reason { get; init; }
    public TimeSpan HoldFor { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>The voice interaction that asked Iris to become attentive, when available.</summary>
    public string TurnId { get; init; } = string.Empty;
}

/// <summary>Model availability changed: loading, ready, unloaded, failed.</summary>
public sealed class ModelStatusEvent : CompanionEvent
{
    public required ModelStatus Status { get; init; }
    public required string ProfileName { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public enum ModelStatus
{
    Unloaded,
    Loading,
    Ready,
    Failed
}

/// <summary>A sense reported new information (window changed, song changed, frame captured).</summary>
public sealed class SensesEvent : CompanionEvent
{
    public required string SenseName { get; init; }
    public required string Summary { get; init; }
}

/// <summary>A tier-3 action is waiting for the user's decision.</summary>
public sealed class PermissionRequestedEvent : CompanionEvent
{
    public required PermissionRequest Request { get; init; }
}

/// <summary>Something worth telling the user in a small toast.</summary>
public sealed class NoticeEvent : CompanionEvent
{
    public required string Message { get; init; }
    public NoticeSeverity Severity { get; init; } = NoticeSeverity.Info;
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
}

public enum NoticeSeverity
{
    Info,
    Success,
    Warning,
    Danger
}

/// <summary>
/// Real-time word from her ears: what the recognizer is doing with a segment —
/// heard and addressed to her, heard but not addressed, open on purpose, closed.
/// The shell shows this live so the boss can always see the ears working; these
/// lines are display-only and never journaled (transcripts stay conversation-only).
/// </summary>
public sealed class EarsEvent : CompanionEvent
{
    /// <summary>Short state word: not_addressed, addressed, open, closed.</summary>
    public required string State { get; init; }

    /// <summary>Transcript or hint, shown live only.</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>A module was installed, removed, enabled or disabled.</summary>
public sealed class ModuleRegistryChangedEvent : CompanionEvent
{
    public required string ModuleId { get; init; }
    public required string Change { get; init; }
}

/// <summary>
/// A module reported a real intermediate step while one of its tools is running.
/// Progress is optional: many useful actions cannot estimate a percentage honestly.
/// Consumers should treat the message as untrusted display text and the turn id as
/// the correlation key for the foreground turn or background task.
/// </summary>
public sealed class ModuleProgressEvent : CompanionEvent
{
    public required string ModuleId { get; init; }
    public required string ModuleName { get; init; }
    public required string ToolName { get; init; }
    public required string TurnId { get; init; }
    public required string Message { get; init; }
    public string IconKey { get; init; } = "body";
    public double? Progress { get; init; }
    public double? Total { get; init; }
}

/// <summary>One background module task changed state or reported a real progress step.</summary>
public sealed class BackgroundTaskChangedEvent : CompanionEvent
{
    public required BackgroundTaskSnapshot Task { get; init; }
}

/// <summary>What one module feedback card is doing. Written by ModuleUi scripting.</summary>
public enum ModuleFeedbackKind
{
    /// <summary>The card shows itself with a spinner and begins its story.</summary>
    TaskStarted,

    /// <summary>A real step landed; the card's line updates so the user can follow.</summary>
    TaskRunning,

    /// <summary>Green check: worked, with its summary line; then the card fades.</summary>
    TaskCompleted,

    /// <summary>Red alert: did not work, with the reason; the history keeps it all.</summary>
    TaskFailed,

    /// <summary>Stopped by cancellation, stated rather than passed off as done.</summary>
    TaskCancelled,

    /// <summary>A single finished notice with no spinner; it fades out on its own.</summary>
    Note
}

/// <summary>
/// One module-authored floating feedback card — the "downloading song … ✓ saved" bar
/// of agentic work. Everything a card shows comes from these events, and every event
/// lands in her log, so a dismissed card is never a lost truth.
/// </summary>
public sealed class ModuleTaskFeedbackEvent : CompanionEvent
{
    public required string TaskId { get; init; }
    public required string ModuleId { get; init; }
    public required string ModuleName { get; init; }
    public string IconKey { get; init; } = "body";
    public required ModuleFeedbackKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = string.Empty;

    /// <summary>Determinate progress when the module can honestly estimate it.</summary>
    public double? Progress { get; init; }
    public double? Total { get; init; }

    /// <summary>The completion/failure line replacing the detail while the card fades.</summary>
    public string? Summary { get; init; }

    /// <summary>How long a finished card stays on screen before fading, in ms.</summary>
    public double HoldMilliseconds { get; init; }

    /// <summary>For Note cards: the tone the module states for itself.</summary>
    public ModuleNoteKind NoteKind { get; init; } = ModuleNoteKind.Info;
}

/// <summary>A validated state patch for one element in a module's declared surface.</summary>
public sealed class ModuleSurfaceStateChangedEvent : CompanionEvent
{
    public required string ModuleId { get; init; }
    public required string SurfaceId { get; init; }
    public required string ElementId { get; init; }
    /// <summary>Foreground turn or background task that produced this UI update.</summary>
    public string TurnId { get; init; } = string.Empty;
    /// <summary>Optional task-scoped visibility for the whole overlay surface.</summary>
    public bool? IsSurfaceVisible { get; init; }
    public string? Label { get; init; }
    public string? Value { get; init; }
    public bool? IsVisible { get; init; }
    public bool? IsEnabled { get; init; }
    public IReadOnlyList<ModuleSurfaceOption>? Options { get; init; }
}

/// <summary>Live resource numbers for the status strip.</summary>
public sealed class TelemetryEvent : CompanionEvent
{
    public double UsedVramMegabytes { get; init; }
    public double TotalVramMegabytes { get; init; }
    public double ProcessRamMegabytes { get; init; }
    public double ContextFillPercent { get; init; }
}

/// <summary>Settings were saved; live screens should re-read them.</summary>
public sealed class SettingsChangedEvent : CompanionEvent
{
}

/// <summary>
/// The runtime finished booting (brain ready, memory repaired, boot.done logged). The
/// shell gates typed input on this: before it lands, a message would only 503 against a
/// half-loaded cortex.
/// </summary>
public sealed class RuntimeReadyEvent : CompanionEvent
{
}
