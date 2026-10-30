using System.Text;

namespace CompanionOS.Core.Prompts;

/// <summary>
/// The three shapes of question Iris can put to the user. Every module, and the permission
/// guard itself, uses these. Nobody builds their own dialog.
/// </summary>
public enum PromptKind
{
    /// <summary>Approve or deny one action. Buttons: Deny, Allow once, optionally more.</summary>
    Confirm,

    /// <summary>Two to four short buttons: "MP3", "MP4", "Cancel".</summary>
    Choice,

    /// <summary>A longer list shown with numbers, so the user can just say "two".</summary>
    Numbered
}

/// <summary>
/// How careful the voice path must be. Normal means a plain spoken answer is fine.
/// Sensitive means the action acts on the user's behalf (click, type, send, delete, buy)
/// and a bare "yes" is NOT enough: the user must say a clear phrase such as "approve".
/// </summary>
public enum PromptRisk
{
    Normal,
    Sensitive
}

/// <summary>Where an answer came from. Whichever arrives first wins.</summary>
public enum PromptSource
{
    Button,
    Voice,
    Typed,
    Remembered,
    Timeout,
    Cancelled
}

/// <summary>What an option means, for styling and for the voice matcher.</summary>
public enum PromptOptionRole
{
    Normal,
    Primary,
    Approve,
    Deny
}

/// <summary>Well-known option ids used by confirm prompts.</summary>
public static class PromptIds
{
    public const string Deny = "deny";
    public const string AllowOnce = "allow_once";
    public const string AllowSession = "allow_session";
    public const string AllowTurn = "allow_turn";
    public const string AllowAlways = "allow_always";
    public const string Yes = "yes";
    public const string No = "no";
}

/// <param name="Id">Stable id returned to the caller.</param>
/// <param name="Label">What the button or list row says.</param>
/// <param name="Detail">Optional second line (duration, channel, size).</param>
/// <param name="Role">Styling and voice meaning.</param>
/// <param name="Aliases">Extra phrases the voice matcher accepts for this option.</param>
/// <param name="VoiceAllowed">False for options that must be pressed, never spoken
/// ("Always allow" can only be given with a button).</param>
public sealed record PromptOption(
    string Id,
    string Label,
    string? Detail = null,
    PromptOptionRole Role = PromptOptionRole.Normal,
    IReadOnlyList<string>? Aliases = null,
    bool VoiceAllowed = true);

/// <summary>The result handed back to whoever asked.</summary>
public sealed record PromptAnswer(string OptionId, PromptSource Source, string? SpokenText = null)
{
    public bool TimedOut => Source == PromptSource.Timeout;
    public bool WasCancelled => Source == PromptSource.Cancelled;

    /// <summary>True only when a real person picked something.</summary>
    public bool IsDeliberate => Source is PromptSource.Button or PromptSource.Voice or PromptSource.Typed or PromptSource.Remembered;
}

/// <summary>What a module sends to ask something. Iris decides how it is shown and heard.</summary>
public sealed class PromptRequest
{
    public required string Question { get; init; }

    /// <summary>Small caption above the question. Defaults by kind.</summary>
    public string? Title { get; init; }

    /// <summary>Leave empty for a simple Yes / No question.</summary>
    public IReadOnlyList<PromptOption> Options { get; init; } = Array.Empty<PromptOption>();

    /// <summary>Null picks Confirm, Choice or Numbered from the options.</summary>
    public PromptKind? Kind { get; init; }

    public PromptRisk Risk { get; init; } = PromptRisk.Normal;

    /// <summary>Null uses the shared policy: 30 s for approvals, 60 s for choices.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Option picked automatically when time runs out. Null means "no answer".</summary>
    public string? TimeoutOptionId { get; init; }

    /// <summary>Read the question and options aloud when she is awake.</summary>
    public bool SpeakAloud { get; init; } = true;

    /// <summary>Show an explicit "Remember this choice" toggle for short choice prompts.</summary>
    public bool AllowRemembering { get; init; }
}

/// <summary>
/// One live question. The interface renders it, the voice matcher answers it, buttons
/// answer it. The first answer wins and every other path is dismissed.
/// </summary>
public sealed class IrisPrompt
{
    public const int NumberedPageSize = 5;

    private readonly TaskCompletionSource<PromptAnswer> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _revision;
    private string _hint = string.Empty;
    private int _pageIndex;

    public IrisPrompt(PromptKind kind, string title, string question, IReadOnlyList<PromptOption> options,
        PromptRisk risk, TimeSpan timeout, string? timeoutOptionId, bool speakAloud,
        string ownerId, string ownerName, string iconKey)
    {
        Kind = kind;
        Title = title;
        Question = question;
        Options = options;
        Risk = risk;
        Timeout = timeout;
        TimeoutOptionId = timeoutOptionId;
        SpeakAloud = speakAloud;
        OwnerId = ownerId;
        OwnerName = ownerName;
        IconKey = iconKey;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public PromptKind Kind { get; }
    public string Title { get; }
    public string Question { get; }
    public IReadOnlyList<PromptOption> Options { get; }
    public PromptRisk Risk { get; }
    public TimeSpan Timeout { get; }
    public string? TimeoutOptionId { get; }
    public bool SpeakAloud { get; }
    public string OwnerId { get; }
    public string OwnerName { get; }
    public string IconKey { get; }
    public bool CanRememberChoice { get; init; }
    public string? RememberChoiceKey { get; init; }
    public bool RememberChoice { get; private set; }

    /// <summary>Optional small line under the question: "Access: browser.act".</summary>
    public string? Detail { get; init; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    /// <summary>Set when this prompt reaches the front of the queue; the clock starts here.</summary>
    public DateTimeOffset ActivatedAt { get; internal set; } = DateTimeOffset.MinValue;

    public DateTimeOffset ExpiresAt => ActivatedAt == DateTimeOffset.MinValue
        ? DateTimeOffset.MaxValue
        : ActivatedAt + Timeout;

    /// <summary>Completes with the user's answer. Never faults.</summary>
    public Task<PromptAnswer> Completion => _answer.Task;

    public bool IsResolved => _answer.Task.IsCompleted;

    /// <summary>Raised whenever something visible changes (hint, page). Views poll it.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>One line the card shows under the options: "Didn't catch that. Say 1, 2 or 3."</summary>
    public string Hint
    {
        get => _hint;
        private set
        {
            _hint = value;
            Interlocked.Increment(ref _revision);
        }
    }

    public int PageIndex => _pageIndex;

    public int PageCount => Kind == PromptKind.Numbered
        ? Math.Max(1, (Options.Count + NumberedPageSize - 1) / NumberedPageSize)
        : 1;

    /// <summary>The options on screen right now. Numbered lists page by five.</summary>
    public IReadOnlyList<PromptOption> VisibleOptions => Kind == PromptKind.Numbered
        ? Options.Skip(_pageIndex * NumberedPageSize).Take(NumberedPageSize).ToList()
        : Options;

    /// <summary>The number shown beside the first visible option, counting from 1.</summary>
    public int FirstVisibleNumber => Kind == PromptKind.Numbered ? _pageIndex * NumberedPageSize + 1 : 1;

    public void SetHint(string hint) => Hint = hint;

    public void SetRememberChoice(bool remember) => RememberChoice = remember;

    /// <summary>Moves a numbered list by one page. Returns false at either end.</summary>
    public bool Page(int delta)
    {
        if (Kind != PromptKind.Numbered)
        {
            return false;
        }

        int target = Math.Clamp(_pageIndex + delta, 0, PageCount - 1);
        if (target == _pageIndex)
        {
            return false;
        }

        _pageIndex = target;
        _hint = string.Empty;
        Interlocked.Increment(ref _revision);
        return true;
    }

    /// <summary>Completes the prompt. The first call wins; later calls return false.</summary>
    public bool TryAnswer(PromptAnswer answer) => _answer.TrySetResult(answer);

    public PromptOption? FindOption(string optionId) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, optionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The option a spoken or timed-out "no" should pick, if the prompt has one.</summary>
    public PromptOption? DenyOption => Options.FirstOrDefault(option => option.Role == PromptOptionRole.Deny);

    /// <summary>What the user is told when they hear the question. Short on purpose.</summary>
    public string DescribeForSpeech()
    {
        StringBuilder text = new();
        text.Append(Question.Trim());
        if (text.Length > 0 && text[^1] is not ('.' or '?' or '!'))
        {
            text.Append('.');
        }

        switch (Kind)
        {
            case PromptKind.Confirm:
                text.Append(Risk == PromptRisk.Sensitive
                    ? " Say approve, or deny."
                    : " Say yes, or no.");
                break;

            case PromptKind.Choice:
                for (int index = 0; index < Options.Count; index++)
                {
                    text.Append($" {index + 1}, {Options[index].Label}.");
                }

                text.Append(" Say a number or the name.");
                break;

            default:
                IReadOnlyList<PromptOption> page = VisibleOptions;
                for (int index = 0; index < page.Count; index++)
                {
                    text.Append($" {FirstVisibleNumber + index}, {page[index].Label}.");
                }

                text.Append(PageIndex < PageCount - 1
                    ? " Say a number, or say more."
                    : " Say a number.");
                break;
        }

        return text.ToString();
    }
}
