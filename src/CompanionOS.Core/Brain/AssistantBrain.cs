using System.Text;
using System.Text.Json;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Recovery;
using CompanionOS.Core.Voice;

namespace CompanionOS.Core.Brain;

/// <summary>
/// The orchestrator. Everything that makes one request become one answer lives here.
///
/// A turn runs in a fixed, boring order, which is exactly what keeps a 4B model feeling
/// reliable:
///
///   1. wake check          — if the cortex is unloaded, decide honestly what to do
///   2. memory              — spreading activation, rendered as short plain sentences
///   3. routing             — attach only the tools this sentence needs
///   4. prompt              — persona + situation + intuition + capabilities
///   5. stream              — publish tokens as they arrive so speech can start early
///   6. tools               — execute, feed results back, repeat at most four times
///   7. speak and journal    — say it, then record that it happened
///
/// Every step publishes an event, which is why the interface can show her thinking
/// instead of faking it.
/// </summary>
public sealed partial class AssistantBrain : IAssistantHostServices, IAsyncDisposable
{
    private const int MaximumToolRounds = 4;

    private readonly SettingsStore _settings;
    private readonly EventBus _events;
    private readonly PersonaStore _personaStore;
    private readonly SynapticMemoryEngine _memory;
    private readonly ModuleHost _modules;
    private readonly LlamaServerHost _serverHost;
    private readonly ModelClient _modelClient;
    private readonly PerceptionEngine _perception;
    private readonly VoicePipeline _voice;
    private readonly TurnJournal _journal;
    private readonly MindStateMachine _mind;

    private readonly List<ChatMessage> _history = new();
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _modeGate = new(1, 1);
    private readonly Timer _housekeepingTimer;

    private CancellationTokenSource? _currentTurnSource;
    private CancellationTokenSource? _voiceAttentionTimeout;
    private CancellationTokenSource? _speechSource;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private GameModeRequest? _pendingGameRequest;
    private int _consolidationInProgress;
    private bool _isDisposed;

    public AssistantBrain(
        SettingsStore settings,
        EventBus events,
        PersonaStore personaStore,
        SynapticMemoryEngine memory,
        ModuleHost modules,
        LlamaServerHost serverHost,
        ModelClient modelClient,
        PerceptionEngine perception,
        VoicePipeline voice,
        TurnJournal journal,
        MindStateMachine mind)
    {
        _settings = settings;
        _events = events;
        _personaStore = personaStore;
        _memory = memory;
        _modules = modules;
        _serverHost = serverHost;
        _modelClient = modelClient;
        _perception = perception;
        _voice = voice;
        _journal = journal;
        _mind = mind;

        _voice.UserRequested += OnUserRequested;
        _voice.UserInterrupted += OnUserInterrupted;
        _voice.UserCalledHer += OnUserCalledHer;
        _perception.SituationUpdated += OnSituationUpdated;

        _housekeepingTimer = new Timer(_ => Housekeeping(), null, 8000, 8000);
    }

    /// <summary>The name she is using right now.</summary>
    public string PersonaName => _personaStore.Current.Name;

    /// <summary>Awake, Sleeping or Gaming.</summary>
    public OperatingMode CurrentMode => _mind.CurrentMode;

    /// <summary>True while the microphone is open.</summary>
    public bool IsMicrophoneOpen => _voice.IsListening;

    /// <summary>Loads the brain, restores what the last session was doing, and wakes the cortex.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Persona persona = _personaStore.Load();
        await _memory.Graph.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await SeedPersonaAsync(persona, cancellationToken).ConfigureAwait(false);
        await _memory.Graph.ApplyDecayIfDueAsync(_settings.Current.Memory.DailyDecay, cancellationToken)
            .ConfigureAwait(false);

        JournalRecovery recovery = _journal.Analyse();
        if (recovery.HadIncompleteTurn)
        {
            CompanionLog.Info("Brain", "The last session was interrupted mid-turn; restoring cleanly.");
            _events.PublishNotice(
                "Last session was interrupted. Your question was kept, nothing was lost.",
                NoticeSeverity.Info);
        }

        _mind.MoveTo(MindState.Idle, "startup");
        _journal.RecordState(MindState.Idle, "startup");

        await WakeCortexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles one message from the user, whether it was typed or spoken.
    /// Returns the answer text; the interface normally follows the events instead.
    /// </summary>
    public async Task<string> SendMessageAsync(string userText, string origin = "text",
        string? attachedImageDataUri = null, CancellationToken cancellationToken = default,
        string? interactionId = null)
    {
        if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(attachedImageDataUri))
        {
            return string.Empty;
        }

        _lastActivity = DateTimeOffset.Now;
        _voice.OpenConversationWindow();

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string turnId = string.IsNullOrWhiteSpace(interactionId)
            ? IrisEvent.CurrentCorrelationId ?? Guid.NewGuid().ToString("N")[..8]
            : interactionId.Trim();
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(turnId);
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _currentTurnSource?.Dispose();
            _currentTurnSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken turnToken = _currentTurnSource.Token;

            string answer = await RunTurnAsync(turnId, userText.Trim(), origin, attachedImageDataUri, turnToken)
                .ConfigureAwait(false);

            IrisEvent.Log("Brain", "turn.done", LogLevel.Debug,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                data: new Dictionary<string, string?>
                {
                    ["origin"] = origin,
                    ["chars_in"] = userText.Length.ToString(),
                    ["chars_out"] = answer.Length.ToString(),
                    ["had_image"] = (attachedImageDataUri is not null).ToString()
                });

            return answer;
        }
        catch (OperationCanceledException)
        {
            CompanionLog.Info("Brain", "Turn cancelled.");
            IrisEvent.Log("Brain", "turn.cancelled", LogLevel.Info);
            _mind.MoveTo(MindState.Idle, "turn cancelled");
            return string.Empty;
        }
        catch (Exception exception)
        {
            CompanionLog.Error("Brain", "The turn failed.", exception);
            IrisEvent.Log("Brain", "turn.fail", LogLevel.Error, $"{exception.GetType().Name}: {exception.Message}",
                data: new Dictionary<string, string?> { ["origin"] = origin });
            _mind.MoveTo(MindState.Error, "turn failed");
            _events.Publish(new ConversationEvent
            {
                Phase = ConversationPhase.TurnFailed,
                TurnId = turnId,
                Text = exception.Message
            });
            _ = _voice.SpeakReflexAsync("error");
            _mind.MoveTo(MindState.Idle, "recovered");
            return string.Empty;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Stops whatever she is doing; used by Escape and by the stop button.</summary>
    public void CancelCurrentTurn()
    {
        _voice.StopSpeaking();
        _currentTurnSource?.Cancel();
    }

    /// <summary>Sleeps, wakes or steps aside for a game.</summary>
    public async Task SetOperatingModeAsync(OperatingMode mode, CancellationToken cancellationToken)
    {
        await _modeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (mode)
            {
                case OperatingMode.Sleeping when _mind.CurrentMode != OperatingMode.Sleeping:
                    await EnterLowPowerModeAsync(OperatingMode.Sleeping, "user asked", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case OperatingMode.Gaming when _mind.CurrentMode != OperatingMode.Gaming:
                    await EnterLowPowerModeAsync(OperatingMode.Gaming, "game detected", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case OperatingMode.Awake:
                    await WakeCortexAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _modeGate.Release();
        }
    }

    /// <summary>
    /// Gives the owner an explicit, stable performance hand-off. Unlike auto-detection this
    /// remains in Gaming Mode until they bring Iris back, which is essential for unknown games
    /// and launchers whose foreground executable keeps changing.
    /// </summary>
    public async Task SetManualGamingAsync(bool enabled, CancellationToken cancellationToken)
    {
        _settings.Current.System.ManualGamingMode = enabled;
        _settings.Save();
        await SetOperatingModeAsync(enabled ? OperatingMode.Gaming : OperatingMode.Awake, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Captures the screen for a tool that needs pixels.</summary>
    public Task<ScreenFrame?> LookAtScreenAsync(bool forceFreshCapture, CancellationToken cancellationToken) =>
        _perception.LookAsync(forceFreshCapture, cancellationToken);

    /// <summary>Plain-English description of the machine.</summary>
    public string DescribeSituation() => _perception.DescribeForPrompt();

    /// <summary>One line per installed ability.</summary>
    public string DescribeCapabilities() => _modules.DescribeCapabilities();

    /// <summary>Teaches the game detector, and remembers the decision permanently.</summary>
    public void LearnGame(string executableName, bool isGame) =>
        _perception.LearnGameExecutable(executableName, isGame);

    /// <summary>Opens or closes the microphone.</summary>
    public void SetMicrophoneOpen(bool isOpen) => _voice.SetListening(isOpen);

    /// <summary>Speaks a line outside a conversation turn (timers, notices).</summary>
    public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        _voice.SpeakAsync(text, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _voiceAttentionTimeout?.Cancel();
        _voiceAttentionTimeout?.Dispose();
        _voiceAttentionTimeout = null;
        _housekeepingTimer.Dispose();
        _voice.UserRequested -= OnUserRequested;
        _voice.UserInterrupted -= OnUserInterrupted;
        _voice.UserCalledHer -= OnUserCalledHer;
        _perception.SituationUpdated -= OnSituationUpdated;
        _currentTurnSource?.Dispose();
        _speechSource?.Dispose();
        _modeGate.Dispose();
        await _serverHost.StopAsync().ConfigureAwait(false);
    }

    /// <summary>The actual turn, in the order documented at the top of the file.</summary>
    private async Task<string> RunTurnAsync(string turnId, string userText, string origin, string? attachedImage,
        CancellationToken cancellationToken)
    {
        _journal.RecordUserMessage(turnId, userText);

        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.UserMessage,
            TurnId = turnId,
            Text = userText
        });

        // 1. If the cortex is unloaded, be honest instead of pretending to think.
        //    A null answer means "the user asked to wake up, so carry on with the turn".
        if (_mind.IsCortexUnloaded)
        {
            string? lowPowerAnswer = await AnswerWhileLowPowerAsync(userText, turnId, cancellationToken)
                .ConfigureAwait(false);
            if (lowPowerAnswer is not null)
            {
                return lowPowerAnswer;
            }
        }

        if (!await EnsureCortexReadyAsync(cancellationToken).ConfigureAwait(false))
        {
            const string noBrain = "My thinking model is not loaded, so I cannot reason about that yet. " +
                                   "Check the brain settings and I will try again.";
            _events.Publish(new ConversationEvent
            {
                Phase = ConversationPhase.AssistantCompleted,
                TurnId = turnId,
                Text = noBrain
            });
            _mind.MoveTo(MindState.Idle, "no model");
            return noBrain;
        }

        _mind.MoveTo(MindState.Thinking, "turn started");

        // 2. Memory: graph thinking happens here, the model only reads the summary.
        ActivationResult activation = await _memory.ActivateAsync(userText, cancellationToken)
            .ConfigureAwait(false);
        string intuition = _memory.RenderIntuition(activation);
        _memory.PublishRecall(turnId, activation, intuition);

        // 3. Routing: only the hands this sentence needs.
        IReadOnlyList<ModuleTool> routedTools = _modules.RouteTools(userText, out IReadOnlyList<IAssistantModule> selectedModules);
        if (routedTools.Count > 0)
        {
            CompanionLog.Trace("Brain",
                $"Routed {routedTools.Count} tools: {string.Join(", ", routedTools.Select(tool => tool.Name))}");
        }

        // 4. Prompt assembly.
        bool hasAttachedImage = !string.IsNullOrWhiteSpace(attachedImage);
        string situation = _perception.DescribeForPrompt(hasAttachedImage, out IReadOnlyList<PerceptionSourceHealth> sourceHealth);
        string sourceStates = JsonSerializer.Serialize(sourceHealth.Select(source => new
        {
            source.Name,
            source.Status,
            source.LastObservedAt
        }));
        IrisEvent.Log("Perception", "turn.context", LogLevel.Debug,
            "Captured the source states used to build this turn's ambient context.",
            data: new Dictionary<string, string?>
            {
                ["image_attached"] = hasAttachedImage.ToString(),
                ["source_states"] = sourceStates
            });

        string systemPrompt = _personaStore.Current.BuildSystemPrompt(
            situation,
            intuition,
            _modules.DescribeCapabilities(),
            DescribeMode(),
            DateTime.Now.ToString("dddd HH:mm"));

        List<ChatMessage> messages = new() { ChatMessage.System(systemPrompt) };
        messages.AddRange(TakeHistoryWindow());
        messages.Add(ChatMessage.User(userText, attachedImage));

        PublishContextTelemetry(messages);

        // The instant acknowledgement: cached audio, played before the model answers.
        if (routedTools.Count > 0)
        {
            _ = _voice.SpeakReflexAsync("acknowledge", cancellationToken);
        }

        _journal.RecordAnswerStarted(turnId);
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.AssistantStarted,
            TurnId = turnId
        });

        StringBuilder finalAnswer = new();
        bool usedTools = false;
        string answerText = string.Empty;

        for (int round = 0; round < MaximumToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StringBuilder roundText = new();
            ModelTurn turn = await _modelClient.StreamChatAsync(
                _settings.Current.ActiveBrainProfile,
                messages,
                routedTools,
                async delta =>
                {
                    roundText.Append(delta);
                    _events.Publish(new ConversationEvent
                    {
                        Phase = ConversationPhase.AssistantDelta,
                        TurnId = turnId,
                        Text = delta
                    });
                    await Task.CompletedTask.ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (turn.ToolCalls.Count == 0)
            {
                answerText = turn.Text.Length > 0 ? turn.Text : roundText.ToString().Trim();
                break;
            }

            usedTools = true;
            _mind.MoveTo(MindState.Acting, "tool call");

            // Echo the assistant's tool request back so the model keeps its own thread.
            messages.Add(ChatMessage.Assistant(turn.Text, turn.ToolCalls));
            if (turn.Text.Length > 0)
            {
                finalAnswer.AppendLine(turn.Text.Trim());
            }

            foreach (ToolCallRequest call in turn.ToolCalls)
            {
                _journal.RecordToolCall(turnId, "router", call.Name);

                JsonElement arguments;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    arguments = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    arguments = JsonDocument.Parse("{}").RootElement.Clone();
                }

                ToolResult result = await _modules
                    .ExecuteAsync(call.Name, arguments, turnId, cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(ChatMessage.ToolResult(call.Id, call.Name, result.ModelText));

                if (!string.IsNullOrWhiteSpace(result.ImageDataUri))
                {
                    // Vision results become a real image message, which is the only form
                    // a multimodal model actually understands.
                    messages.Add(ChatMessage.User("This is the screenshot I just took.", result.ImageDataUri));
                }

                if (!string.IsNullOrWhiteSpace(result.SpokenText))
                {
                    await SpeakFragmentAsync(result.SpokenText!, cancellationToken).ConfigureAwait(false);
                }
            }

            _journal.RecordAnswerProgress(turnId, finalAnswer.ToString());
            _mind.MoveTo(MindState.Thinking, "resuming after tool");
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            answerText = finalAnswer.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            answerText = "I could not work that one out. Ask me again in a moment?";
        }

        // 6. Speak, unless the user interrupted or the voice is muted.
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.AssistantCompleted,
            TurnId = turnId,
            Text = answerText,
            Detail = string.Join(",", selectedModules.Select(module => module.Id))
        });

        RememberExchange(userText, answerText, usedTools);

        if (_settings.Current.Voice.SpeakReplies)
        {
            _mind.MoveTo(MindState.Speaking, "answering aloud");
            await SpeakAnswerAsync(answerText, cancellationToken).ConfigureAwait(false);
        }

        _journal.RecordAnswerCompleted(turnId);
        _mind.MoveTo(MindState.Idle, "turn finished");
        _lastActivity = DateTimeOffset.Now;
        return answerText;
    }
}
