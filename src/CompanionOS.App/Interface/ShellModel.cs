using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Common;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Prompts;

namespace CompanionOS.App.Interface;

/// <summary>Who wrote a line in the conversation.</summary>
public enum ChatRole
{
    User,
    Assistant,
    System,
    Tool
}

/// <summary>One bubble in the chat. Mutable because answers stream in.</summary>
public sealed class ChatEntry : ObservableObject
{
    private string _text = string.Empty;
    private string _detail = string.Empty;
    private string _toolStatus = string.Empty;
    private bool _isStreaming;
    private bool _isToolRunning;
    private IReadOnlyList<RecalledMemory> _recalledMemories = Array.Empty<RecalledMemory>();

    public ChatRole Role { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
    /// <summary>Turn that produced this line, for linking conversation and activity state.</summary>
    public string TurnId { get; init; } = string.Empty;
    public string IconKey { get; init; } = "body";

    public string ToolStatus
    {
        get => _toolStatus;
        set => SetProperty(ref _toolStatus, value);
    }

    /// <summary>
    /// Memory evidence that contributed to this turn, shown as inspectable chips.
    /// Assignments raise a change: the recall event used to land AFTER the streaming
    /// entry existed and nothing ever re-rendered the row, silently (2026-10-27 audit).
    /// </summary>
    public IReadOnlyList<RecalledMemory> RecalledMemories
    {
        get => _recalledMemories;
        set => SetProperty(ref _recalledMemories, value);
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public bool IsToolRunning
    {
        get => _isToolRunning;
        set => SetProperty(ref _isToolRunning, value);
    }

    public string TimeText => Time.ToString("HH:mm");
}

/// <summary>A short toast in the bottom-right corner.</summary>
public sealed class NoticeItem
{
    public required string Message { get; init; }
    public required NoticeSeverity Severity { get; init; }
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// The bridge between her nervous system and the screen.
///
/// Every value the interface shows is written here, from a real event, on the UI thread.
/// Nothing is faked and nothing polls: when the model unloads, the VRAM number moves
/// because the runtime published it; when she remembers something, the chip appears
/// because the memory engine said so.
///
/// Token streaming is batched at roughly 30 frames per second so a fast model cannot
/// drown the UI thread in layout work.
/// </summary>
public sealed partial class ShellModel : ObservableObject
{
    private readonly CompanionRuntime _runtime;
    private readonly DispatcherTimer _streamFlushTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _earsTimer;
    private readonly StringBuilder _streamBuffer = new();
    private readonly List<IDisposable> _subscriptions = new();

    private ChatEntry? _streamingEntry;
    private MindState _state = MindState.Idle;
    private OperatingMode _mode = OperatingMode.Awake;
    private string _stateText = "Starting";
    private string _modelLine = "Brain · starting";
    private string _vramText = "VRAM —";
    private string _contextText = "CTX —";
    private string _ramText = "RAM —";
    private string _lastUtterance = string.Empty;
    private string _earsLine = string.Empty;
    private bool _isListening;
    private bool _isSpeaking;
    private bool _isTurnInProgress;
    private bool _isSurfaceActionInProgress;
    private string _activeConversationTurnId = string.Empty;
    private string _activityTurnId = string.Empty;
    private string _activityMessage = string.Empty;
    private string _activityModule = string.Empty;
    private string _activityIconKey = "body";
    private string _activityDetail = string.Empty;
    private double? _activityProgress;
    private double? _activityTotal;
    private DateTimeOffset _activityStartedAt;
    private double _microphoneLevel;
    private double _speechLevel;
    private IrisPrompt? _pendingPrompt;
    private PromptNotice? _lastPromptNotice;
    private IReadOnlyList<RecalledMemory> _pendingRecall = Array.Empty<RecalledMemory>();
    private string _pendingRecallTurnId = string.Empty;
    private string _pendingUserDetail = string.Empty;
    private bool _isRuntimeReady;

    public ShellModel(CompanionRuntime runtime)
    {
        _runtime = runtime;

        _streamFlushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => FlushStream());
        _streamFlushTimer.Start();

        _noticeTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => PruneNotices());
        _noticeTimer.Start();

        // Her ears line decays seven quiet seconds after its last update: it reports one
        // segment or one switch press, then leaves the screen instead of becoming noise.
        _earsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(7), DispatcherPriority.Background, OnEarsTimerTick);

        Subscribe();
    }

    /// <summary>Everything said so far in this session.</summary>
    public ObservableCollection<ChatEntry> Messages { get; } = new();

    /// <summary>Toasts, newest last, at most three.</summary>
    public ObservableCollection<NoticeItem> Notices { get; } = new();

    public CompanionRuntime Runtime => _runtime;

    /// <summary>
    /// False until the runtime's boot.done lands. The shell refuses typed input until
    /// then — her first seconds are for paging the brain into VRAM, not for catching a
    /// 503 the owner can see.
    /// </summary>
    public bool IsRuntimeReady
    {
        get => _isRuntimeReady;
        private set => SetProperty(ref _isRuntimeReady, value);
    }

    public MindState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public OperatingMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsSleeping));
                OnPropertyChanged(nameof(SleepButtonLabel));
            }
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string ModelLine
    {
        get => _modelLine;
        private set => SetProperty(ref _modelLine, value);
    }

    public string VramText
    {
        get => _vramText;
        private set => SetProperty(ref _vramText, value);
    }

    public string ContextText
    {
        get => _contextText;
        private set => SetProperty(ref _contextText, value);
    }

    public string RamText
    {
        get => _ramText;
        private set => SetProperty(ref _ramText, value);
    }

    public string LastUtterance
    {
        get => _lastUtterance;
        private set => SetProperty(ref _lastUtterance, value);
    }

    public bool IsListening
    {
        get => _isListening;
        private set => SetProperty(ref _isListening, value);
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set => SetProperty(ref _isSpeaking, value);
    }

    /// <summary>True while a typed or spoken turn is still producing an answer.</summary>
    public bool IsTurnInProgress
    {
        get => _isTurnInProgress;
        private set
        {
            if (SetProperty(ref _isTurnInProgress, value))
            {
                OnPropertyChanged(nameof(HasActiveWork));
            }
        }
    }

    public bool HasActiveWork => IsTurnInProgress || _isSurfaceActionInProgress;

    /// <summary>The conversation turn whose events currently own the chat activity state.</summary>
    public string ActiveConversationTurnId
    {
        get => _activeConversationTurnId;
        private set => SetProperty(ref _activeConversationTurnId, value);
    }

    /// <summary>
    /// One live line of what her ears just did — what the recognizer heard, whether the
    /// segment was addressed to her, the invoke switch. A moment of truth on the board;
    /// empty again after her ears have been quiet for seven seconds.
    /// </summary>
    public string EarsLine
    {
        get => _earsLine;
        private set => SetProperty(ref _earsLine, value);
    }

    /// <summary>Current truthful work status, shared by chat and non-activating overlays.</summary>
    public string ActivityTurnId
    {
        get => _activityTurnId;
        private set => SetProperty(ref _activityTurnId, value);
    }

    public string ActivityMessage
    {
        get => _activityMessage;
        private set => SetProperty(ref _activityMessage, value);
    }

    public string ActivityModule
    {
        get => _activityModule;
        private set => SetProperty(ref _activityModule, value);
    }

    public string ActivityIconKey
    {
        get => _activityIconKey;
        private set => SetProperty(ref _activityIconKey, value);
    }

    public string ActivityDetail
    {
        get => _activityDetail;
        private set => SetProperty(ref _activityDetail, value);
    }

    public double? ActivityProgress
    {
        get => _activityProgress;
        private set => SetProperty(ref _activityProgress, value);
    }

    public double? ActivityTotal
    {
        get => _activityTotal;
        private set => SetProperty(ref _activityTotal, value);
    }

    public DateTimeOffset ActivityStartedAt
    {
        get => _activityStartedAt;
        private set => SetProperty(ref _activityStartedAt, value);
    }

    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set => SetProperty(ref _microphoneLevel, value);
    }

    public double SpeechLevel
    {
        get => _speechLevel;
        private set => SetProperty(ref _speechLevel, value);
    }

    public bool IsSleeping => Mode != OperatingMode.Awake;

    public string SleepButtonLabel => Mode == OperatingMode.Awake ? "Rest" : "Wake";

    /// <summary>The question on screen right now (approval, choice or numbered list), if any.</summary>
    public IrisPrompt? PendingPrompt
    {
        get => _pendingPrompt;
        private set
        {
            if (SetProperty(ref _pendingPrompt, value))
            {
                OnPropertyChanged(nameof(HasPendingPermission));
            }
        }
    }

    /// <summary>True while any question is open. Other overlays step aside for it.</summary>
    public bool HasPendingPermission => _pendingPrompt is not null;

    /// <summary>The last answered question, kept for a moment so the overlay can say "done".</summary>
    public PromptNotice? LastPromptNotice
    {
        get => _lastPromptNotice;
        private set => SetProperty(ref _lastPromptNotice, value);
    }

    /// <summary>The name she answers to, taken from her persona.</summary>
    public string PersonaName => _runtime.Persona.Current.Name;

    /// <summary>Raised when the user opens the source memory behind a recall chip.</summary>
    public event Action<string>? MemoryNodeOpenRequested;

    public void OpenMemoryNode(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            MemoryNodeOpenRequested?.Invoke(nodeId);
        }
    }

    /// <summary>Starts her whole body. Called once, from the window.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // No greeting card: she is never a marketing page. The conversation starts when
        // the user or a system event speaks first.
        await _runtime.StartAsync(AppModules.CreateBuiltInModules(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one typed or attached message.</summary>
    public async Task SendAsync(string text, string? attachedImageDataUri = null)
    {
        // Inputs open the moment boot.done lands, never earlier: racing the loader is
        // how the owner's first "hey" became a visible 503.
        if (!IsRuntimeReady)
        {
            _runtime.Events.PublishNotice("She is still waking up — her brain is loading. Give her a moment.",
                NoticeSeverity.Info);
            return;
        }

        if (_isSurfaceActionInProgress)
        {
            _runtime.Events.PublishNotice("Iris is finishing the current module action.", NoticeSeverity.Info);
            return;
        }

        if (string.IsNullOrWhiteSpace(text) && attachedImageDataUri is null)
        {
            return;
        }

        // A turn that carries a screenshot gets a kernel line under the bubble: the fact
        // worth remembering about the turn, exactly the board's small eye-line caption.
        _pendingUserDetail = attachedImageDataUri is not null ? "Looked at your screen" : string.Empty;

        _runtime.Voice.StopSpeaking();

        // One marshal for BOTH edges of the flag. The composer path arrives on the UI
        // thread, but AskAboutScreenAsync arrives AFTER LookAsync's ConfigureAwait(false)
        // on a pool thread — and IsTurnInProgress drives the send button and the activity
        // rail through PropertyChanged. Raising that off the UI thread is exactly the
        // binding corruption the shell pretends not to have (2026-10-27 audit).
        OnUiThread(() => IsTurnInProgress = true);
        try
        {
            await _runtime.Brain.SendMessageAsync(text, "text", attachedImageDataUri).ConfigureAwait(false);
        }
        finally
        {
            OnUiThread(() => IsTurnInProgress = false);
        }
    }

    /// <summary>Runs a button action declared by a hosted module surface through the normal sandbox.</summary>
    public async Task ExecuteModuleSurfaceActionAsync(ModuleSurface surface, ModuleSurfaceElement button,
        IReadOnlyDictionary<string, string> inputs)
    {
        if (button.Type != ModuleSurfaceElementType.Button)
        {
            return;
        }
        if (HasActiveWork)
        {
            _runtime.Events.PublishNotice("Finish Iris's current task before starting another module action.",
                NoticeSeverity.Info);
            return;
        }

        string turnId = Guid.NewGuid().ToString("N");
        OnUiThread(() =>
        {
            _isSurfaceActionInProgress = true;
            OnPropertyChanged(nameof(HasActiveWork));
            BeginActivity(turnId, button.Label, surface.ModuleName, "Starting", surface.IconKey);
        });

        try
        {
            JsonElement arguments = JsonSerializer.SerializeToElement(inputs);
            ToolResult result = await _runtime.Modules.ExecuteAsync(button.ToolName, arguments, turnId,
                CancellationToken.None, surface.ModuleId).ConfigureAwait(false);
            string message = Trim(result.DisplayText ?? result.ModelText, 140);
            if (!string.IsNullOrWhiteSpace(message))
            {
                _runtime.Events.PublishNotice(message,
                    result.Succeeded ? NoticeSeverity.Success : NoticeSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            _runtime.Events.PublishNotice($"{surface.ModuleName} failed: {exception.Message}", NoticeSeverity.Danger);
        }
        finally
        {
            OnUiThread(() =>
            {
                _isSurfaceActionInProgress = false;
                OnPropertyChanged(nameof(HasActiveWork));
                ClearActivity();
            });
        }
    }

    /// <summary>Opens or closes the microphone from the composer button.</summary>
    public void ToggleMicrophone()
    {
        bool shouldOpen = !_runtime.Voice.IsListening;
        _runtime.Brain.SetMicrophoneOpen(shouldOpen);
        IsListening = _runtime.Voice.IsListening;
    }

    /// <summary>
    /// Raised when the user asked her to sleep and the window should carry that out.
    ///
    /// Sleep is a window-level operation, not a brain-level one: it disposes the whole
    /// runtime and leaves a tray icon behind, so it cannot live in the model.
    /// </summary>
    public event Action? DeepSleepRequested;

    /// <summary>
    /// Sleep, as the user means it: everything stops.
    ///
    /// With System.SleepToTray on (the default) this releases the microphone, unloads
    /// the speech model, stops every timer, closes the database and leaves only a tray
    /// icon. Nothing of hers runs until she is started again.
    ///
    /// With it off, she merely unloads her cortex and keeps listening, which is
    /// cheaper to wake from but is not "off" in any honest sense.
    /// </summary>
    public void RequestSleep()
    {
        if (!_runtime.Settings.Current.System.SleepToTray)
        {
            _ = ToggleSleepAsync();
            return;
        }

        DeepSleepRequested?.Invoke();
    }

    /// <summary>Unloads the cortex but keeps her senses, so her name still wakes her.</summary>
    public async Task ToggleSleepAsync()
    {
        OperatingMode targetMode = Mode == OperatingMode.Awake ? OperatingMode.Sleeping : OperatingMode.Awake;
        await _runtime.Brain.SetOperatingModeAsync(targetMode, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Explicit performance hand-off for a game that is not in the learned list.</summary>
    public Task ToggleGamingAsync()
    {
        bool enterGaming = Mode != OperatingMode.Gaming;
        return _runtime.Brain.SetManualGamingAsync(enterGaming, CancellationToken.None);
    }

    /// <summary>Answers the open question with one of its options (a button press).</summary>
    public void AnswerPrompt(Guid promptId, string optionId) =>
        _runtime.Modules.Prompts.Answer(promptId, optionId, PromptSource.Button);

    /// <summary>The X on the card: deny an approval, or drop a question without choosing.</summary>
    public void DismissPrompt() => _runtime.Modules.Prompts.CancelActive();

    /// <summary>Kept for older callers: answers an approval with the matching button.</summary>
    public void AnswerPermission(PermissionDecision decision)
    {
        IrisPrompt? prompt = PendingPrompt;
        if (prompt is not null)
        {
            AnswerPrompt(prompt.Id, PermissionRequest.OptionIdFor(decision));
        }
    }

    /// <summary>Stops her voice and cancels the turn (Escape).</summary>
    public void Interrupt()
    {
        _runtime.Voice.StopSpeaking();
        _runtime.Brain.CancelCurrentTurn();
    }
}


/// <summary>What happened to the last question, in words a person would say.</summary>
public sealed record PromptNotice(string Headline, string Detail, bool Positive, string Status, DateTimeOffset At);
