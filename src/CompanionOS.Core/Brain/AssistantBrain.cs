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
    /// <summary>
    /// Eight agentic rounds. Four once cut real chains (scan → filter → act → verify)
    /// mid-work and fell back to a permission-asking canned line. Eight keeps the
    /// common chains inside one turn; the loop still ends the moment she stops calling
    /// tools, so chatty models pay no tax.
    /// </summary>
    private const int MaximumToolRounds = 8;

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
    private readonly IDisposable _backgroundTaskJournalSubscription;
    private readonly object _backgroundTaskJournalLock = new();
    private readonly Dictionary<string, string> _lastBackgroundTaskStates = new(StringComparer.Ordinal);

    private readonly List<ChatMessage> _history = new();
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _modeGate = new(1, 1);
    private readonly Timer _housekeepingTimer;

    private CancellationTokenSource? _currentTurnSource;
    private string? _currentTurnId;
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
        _backgroundTaskJournalSubscription = events.Subscribe<BackgroundTaskChangedEvent>(RecordBackgroundTaskState);

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
        if (recovery.HadIncompleteTurn || recovery.InterruptedBackgroundTasks.Count > 0)
        {
            CompanionLog.Info("Brain", "The last session stopped with work in progress; restoring cleanly.");
            bool uncertainTool = recovery.InterruptedTools.Any(tool =>
                tool.Status is JournalToolStatus.Unknown or JournalToolStatus.BackgroundStarted);
            bool actionMayHaveCompleted = uncertainTool || recovery.InterruptedBackgroundTasks.Count > 0;
            List<string> recoveryDetails = [];
            if (uncertainTool)
            {
                recoveryDetails.Add("An action may have completed externally; check before asking Iris to repeat it.");
            }
            if (recovery.InterruptedBackgroundTasks.Count > 0)
            {
                int activeTaskCount = recovery.InterruptedBackgroundTasks.Count;
                recoveryDetails.Add(activeTaskCount == 1
                    ? "One background task was still active; check its service before repeating it."
                    : $"{activeTaskCount} background tasks were still active; check their services before repeating them.");
            }
            if (recoveryDetails.Count == 0 && recovery.InterruptedTools.Count > 0)
            {
                int actionCount = recovery.InterruptedTools.Count;
                recoveryDetails.Add(actionCount == 1
                    ? "I recorded one action result."
                    : $"I recorded {actionCount} action results.");
            }
            string recoveryNotice = "Last session was interrupted. " +
                (recoveryDetails.Count > 0 ? string.Join(" ", recoveryDetails) + " " : string.Empty) +
                "Your question was kept.";
            _events.PublishNotice(recoveryNotice,
                actionMayHaveCompleted ? NoticeSeverity.Warning : NoticeSeverity.Info);
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
        _currentTurnId = turnId;
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
            CompanionLog.Info("Brain", $"Turn {turnId} cancelled.");
            IrisEvent.Log("Brain", "turn.cancelled", LogLevel.Info,
                data: new Dictionary<string, string?> { ["origin"] = origin });
            _journal.RecordAnswerCancelled(turnId);
            _events.Publish(new ConversationEvent
            {
                Phase = ConversationPhase.TurnCancelled,
                TurnId = turnId,
                Text = "Stopped."
            });
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
            if (_settings.Current.Voice.ReflexPhrasesEnabled)
            {
                _ = SpeakReflexInBackgroundAsync("error");
            }

            _mind.MoveTo(MindState.Idle, "recovered");
            return string.Empty;
        }
        finally
        {
            _currentTurnId = null;
            _turnGate.Release();
        }
    }

    /// <summary>Stops whatever she is doing; used by Escape and by the stop button.</summary>
    public void CancelCurrentTurn()
    {
        _voice.StopSpeaking();
        _currentTurnSource?.Cancel();
    }

    /// <summary>Observes reflex playback launched from callbacks that cannot await it.</summary>
    private async Task SpeakReflexInBackgroundAsync(string category, CancellationToken cancellationToken = default)
    {
        try
        {
            await _voice.SpeakReflexAsync(category, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is a normal end state for a detached acknowledgement.
        }
        catch (OperationCanceledException exception)
        {
            CompanionLog.Trace("Voice", $"Background reflex '{category}' was cancelled: {exception.Message}");
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Voice", $"Background reflex '{category}' failed: {exception.Message}");
        }
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

    /// <summary>Controls Windows media through the current media-session sensor.</summary>
    public Task<MediaControlResult> ControlMediaAsync(string action, CancellationToken cancellationToken) =>
        _perception.ControlMediaAsync(action, cancellationToken);

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
        _backgroundTaskJournalSubscription.Dispose();
        _currentTurnSource?.Dispose();
        _speechSource?.Dispose();
        _modeGate.Dispose();
        await _serverHost.StopAsync().ConfigureAwait(false);
    }

    private void RecordBackgroundTaskState(BackgroundTaskChangedEvent change)
    {
        BackgroundTaskSnapshot task = change.Task;
        string state = task.State.ToString();
        lock (_backgroundTaskJournalLock)
        {
            if (_lastBackgroundTaskStates.TryGetValue(task.Id, out string? previous) && previous == state)
            {
                return;
            }
            _lastBackgroundTaskStates[task.Id] = state;
        }

        _journal.RecordBackgroundTaskState(task.Id, task.ModuleId, task.ToolName, state);
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

        // 3. Routing: only the hands this sentence needs. The receipt is logged in the
        //    event stream, not trace: "she picked X because of Y" has to be answerable
        //    from the day's records whenever her behavior felt wrong.
        IReadOnlyList<ModuleTool> routedTools = _modules.RouteTools(userText, out IReadOnlyList<IAssistantModule> selectedModules);
        IrisEvent.Log("Brain", "turn.routed", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["origin"] = origin,
                ["modules"] = string.Join(",", selectedModules.Select(module => module.Id)),
                ["tools"] = string.Join(",", routedTools.Select(tool => tool.Name))
            });

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

        // Spoken conversation has no scrollback and no skimming: a long answer is a wall
        // of sound. Keep heard replies short unless the user explicitly asks for detail.
        if (origin.Equals("voice", StringComparison.OrdinalIgnoreCase))
        {
            systemPrompt +=
                "\n\nSPOKEN REPLIES\n" +
                "- The user hears this answer out loud, one sentence at a time. Answer in one to three\n" +
                "  short sentences. Only add more when they ask for detail or clearly want to talk.\n" +
                "- Never read out lists, changelogs, or more than one example. Confirm, and offer the rest.\n" +
                "- If you used a tool, report the outcome in one plain sentence.";
        }

        List<ChatMessage> messages = new() { ChatMessage.System(systemPrompt) };
        messages.AddRange(TakeHistoryWindow());
        messages.Add(ChatMessage.User(userText, attachedImage));

        PublishContextTelemetry(messages);

        _journal.RecordAnswerStarted(turnId);
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.AssistantStarted,
            TurnId = turnId
        });

        StringBuilder finalAnswer = new();
        bool anyToolSucceeded = false;
        bool reachedFinalAnswer = false;
        bool answerSpokenDuringGeneration = false;
        string answerText = string.Empty;
        List<string> toolLedger = new();
        string? lastToolFailureDetail = null;
        HashSet<string> uncertainTools = new(StringComparer.OrdinalIgnoreCase);
        bool honestyRetryUsed = false;
        bool lengthContinued = false;

        for (int round = 0; round < MaximumToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StringBuilder roundText = new();
            StringBuilder speechBuffer = new();
            bool maySpeakWhileGenerating = origin.Equals("voice", StringComparison.OrdinalIgnoreCase) &&
                                           routedTools.Count == 0 && _settings.Current.Voice.SpeakReplies &&
                                           !honestyRetryUsed && !lengthContinued;
            bool streamedAnySpeech = false;
            Task speechTail = Task.CompletedTask;
            ModelTurn turn = await _modelClient.StreamChatAsync(
                _settings.Current.ActiveBrainProfile,
                messages,
                routedTools,
                async delta =>
                {
                    roundText.Append(delta);
                    if (maySpeakWhileGenerating)
                    {
                        speechBuffer.Append(delta);
                        foreach (string sentence in TakeCompleteSpeechSentences(speechBuffer,
                                     _settings.Current.Brain.MinimumSpokenSentenceCharacters))
                        {
                            // The speech-economy floor applies mid-stream too: a list-shaped
                            // sentence is dropped before it can cost seconds of synthesis.
                            string spoken = SpeechPlanner.TrimForSpeech(sentence,
                                SpeechPlanner.SpeechEconomyCharacters);
                            if (string.IsNullOrWhiteSpace(spoken))
                            {
                                continue;
                            }

                            speechTail = QueueSpeechAfterAsync(speechTail, spoken, cancellationToken);
                            streamedAnySpeech = true;
                        }
                    }

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

                // The length limit is a physical wall, not a decision. When the server
                // says the answer was cut, she is handed her own cut text and told to
                // continue from exactly where it stopped — the ModelClient used to
                // swallow mid-tool-call truncations entirely, which read downstream as
                // "she chose not to act".
                bool truncation = string.Equals(turn.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(turn.FinishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);
                if (truncation && !lengthContinued)
                {
                    lengthContinued = true;
                    IrisEvent.Log("Brain", "turn.length_continue", LogLevel.Info,
                        message: "The length limit cut this answer; she is told to continue from where it stopped.",
                        data: new Dictionary<string, string?>
                        {
                            ["chars_before_cut"] = answerText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        });
                    messages.Add(ChatMessage.Assistant(turn.Text));
                    messages.Add(ChatMessage.System(
                        "Your previous reply was cut off by the length limit before it finished. Continue from exactly " +
                        "where it stopped. Do not repeat anything already written. When the work and the answer are " +
                        "complete, stop."));
                    answerSpokenDuringGeneration = false;
                    continue;
                }

                // The gate is receipts, not words. Regex never judges her sentences again:
                // every pattern built on her output eventually punished truth ("the video
                // is paused", "an ASMR playlist playing…" once replaced her correct
                // answer). What remain are facts — did the request ask for work, and did
                // any hand succeed. If not, she faces the receipt truth ONCE — acts, or
                // refuses plainly — and then her words ship exactly as she wrote them.
                bool actionIntended = toolLedger.Count > 0 ||
                    (routedTools.Count > 0 && SpeechPlanner.IsActionRequest(userText));
                bool receiptsOwed = actionIntended && !anyToolSucceeded && uncertainTools.Count == 0;
                if (receiptsOwed && !honestyRetryUsed)
                {
                    honestyRetryUsed = true;
                    IrisEvent.Log("Brain", "turn.receipt_check", LogLevel.Info,
                        message: "Work was asked for but no hand succeeded; she is told the receipt truth once and acts or refuses plainly.",
                        data: new Dictionary<string, string?>
                        {
                            ["tools_run"] = toolLedger.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["tools_routed"] = routedTools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        });
                    messages.Add(ChatMessage.Assistant(turn.Text));
                    messages.Add(ChatMessage.System(toolLedger.Count == 0
                        ? "RECEIPT CHECK: this request is an action, and no hand has run yet. Call the matching tool " +
                          "now and act on its result — a clear order is an instruction, not a question; do not ask " +
                          "permission. If no hand can truly do it, answer in one short sentence that it has not " +
                          "happened, and offer the nearest working next step."
                        : "RECEIPT CHECK: the hands you moved did not complete: " + string.Join("; ", toolLedger) +
                          ". That is the record. Answer in ONE short sentence: what did not happen, and the nearest " +
                          "working step. Never describe a failed action as a success."));
                    // Anything already streamed may have carried a claim; the truth lands last.
                    answerSpokenDuringGeneration = false;
                    continue;
                }

                if (receiptsOwed && SpeechPlanner.LooksLikeCollapsedReply(answerText))
                {
                    // The collapse of 19:31: a non-answer ("Boss." and silence) on a turn
                    // that owed receipts. The only word-shaped check left anywhere,
                    // because a reply wholly made of address words is a fact, not phrasing.
                    answerText = BuildReceiptAnswer(toolLedger, anyToolSucceeded, lastToolFailureDetail);
                    answerSpokenDuringGeneration = false; // the receipt answer is the one she speaks
                    IrisEvent.Log("Brain", "turn.answer_rescued", LogLevel.Info,
                        message: "A collapsed reply was replaced with the receipt answer so the turn still lands honestly.",
                        data: ReceiptData(toolLedger));
                }

                if (maySpeakWhileGenerating && streamedAnySpeech)
                {
                    string remainingSpeech = SpeechPlanner.TrimForSpeech(
                        SpeechTextSanitizer.CleanForSpeech(speechBuffer.ToString()),
                        SpeechPlanner.SpeechEconomyCharacters);
                    if (!string.IsNullOrWhiteSpace(remainingSpeech))
                    {
                        speechTail = QueueSpeechAfterAsync(speechTail, remainingSpeech, cancellationToken);
                    }

                    await speechTail.ConfigureAwait(false);
                    answerSpokenDuringGeneration = true;
                }

                reachedFinalAnswer = true;
                break;
            }

            _mind.MoveTo(MindState.Acting, "tool call");

            // Echo the assistant's tool request back so the model keeps its own thread.
            messages.Add(ChatMessage.Assistant(turn.Text, turn.ToolCalls));
            if (turn.Text.Length > 0)
            {
                finalAnswer.AppendLine(turn.Text.Trim());
            }

            foreach (ToolCallRequest call in turn.ToolCalls)
            {
                string operationId = _journal.RecordToolCall(turnId, "router", call.Name);
                string label = call.Name.Replace('_', ' ').Replace('.', ' ');

                if (!ToolArgumentsParser.TryParse(call.ArgumentsJson, out JsonElement arguments))
                {
                    // The schema is the corrective evidence a small model needs: without
                    // the keys, it used to re-emit the same broken arguments and burn
                    // another round on a canned "I should ask for the missing details".
                    ModuleTool? routedDefinition = routedTools.FirstOrDefault(candidate =>
                        candidate.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase));
                    string argumentHelp = routedDefinition is null
                        ? string.Empty
                        : " " + DescribeRequiredArguments(routedDefinition);
                    ToolResult invalidArguments = ToolResult.Fail(
                        $"Invalid arguments for {call.Name}.{argumentHelp} Re-call the action with exactly those keys.");
                    _journal.RecordToolResult(turnId, operationId, JournalToolStatus.Failed);
                    messages.Add(ChatMessage.ToolResult(call.Id, call.Name, invalidArguments.ModelText));
                    toolLedger.Add($"{label}: not run (invalid arguments)");
                    continue;
                }

                if (uncertainTools.Contains($"{call.Name}|{call.ArgumentsJson ?? string.Empty}", StringComparer.OrdinalIgnoreCase))
                {
                    ToolResult notRepeated = ToolResult.Fail(
                        $"I did not repeat {label}: an identical attempt is still unconfirmed. Check the current state first.");
                    _journal.RecordToolResult(turnId, operationId, JournalToolStatus.Failed);
                    messages.Add(ChatMessage.ToolResult(call.Id, call.Name, notRepeated.ModelText));
                    toolLedger.Add($"{label}: not repeated");
                    continue;
                }

                ToolResult result;
                try
                {
                    result = await _modules
                        .ExecuteAsync(call.Name, arguments, turnId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _journal.RecordToolResult(turnId, operationId, JournalToolStatus.Unknown);
                    throw;
                }

                _journal.RecordToolResult(turnId, operationId,
                    result.BackgroundTaskId is not null
                        ? JournalToolStatus.BackgroundStarted
                        : result.IsOutcomeUncertain ? JournalToolStatus.Unknown
                        : result.Succeeded ? JournalToolStatus.Succeeded : JournalToolStatus.Failed,
                    result.BackgroundTaskId);

                if (result.IsOutcomeUncertain)
                {
                    uncertainTools.Add($"{call.Name}|{call.ArgumentsJson ?? string.Empty}");
                }

                anyToolSucceeded |= result.Succeeded;
                toolLedger.Add($"{label}: " + (result.BackgroundTaskId is not null ? "started in background"
                    : result.IsOutcomeUncertain ? "unconfirmed"
                    : result.Succeeded ? "done" : "failed"));

                if (!result.Succeeded && result.BackgroundTaskId is null && !result.IsOutcomeUncertain)
                {
                    // The refusal text is the honest "why" — the receipt answer quotes it
                    // instead of leaving the boss guessing what broke.
                    lastToolFailureDetail = FirstSentenceOf(result.ModelText);
                }

                messages.Add(ChatMessage.ToolResult(call.Id, call.Name, result.ModelText));

                if (!string.IsNullOrWhiteSpace(result.ImageDataUri))
                {
                    // Vision results become a real image message, which is the only form
                    // a multimodal model actually understands.
                    messages.Add(ChatMessage.User("This is the screenshot I just took.", result.ImageDataUri));
                }

                // result.SpokenText is deliberately not spoken here. Modules used to talk
                // mid-turn ("Playing ..."), which bogarted the single speech path: her
                // answer is the only voice inside a turn. The fragment still exists as
                // module-authored copy for surfaces that show text, but speech flows
                // through exactly one gate — the answer below.
            }

            _journal.RecordAnswerProgress(turnId, finalAnswer.ToString());
            _mind.MoveTo(MindState.Thinking, "resuming after tool");
        }

        if (!reachedFinalAnswer && toolLedger.Count > 0)
        {
            answerText = (finalAnswer.ToString().Trim() + "\n\n" +
                          $"I stopped after {toolLedger.Count} step(s) without finishing: {string.Join("; ", toolLedger)}. " +
                          "The receipts are on record for next time.").Trim();
        }
        else if (string.IsNullOrWhiteSpace(answerText) && toolLedger.Count > 0)
        {
            answerText = $"Here is what happened: {string.Join("; ", toolLedger)}.";
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            answerText = finalAnswer.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(answerText))
        {
            answerText = "I could not work that one out. Ask me again in a moment?";
        }

        IrisEvent.Log("Brain", "turn.report", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["origin"] = origin,
                ["image_attached"] = hasAttachedImage.ToString(),
                ["memories_activated"] = activation.Activated.Count().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tools"] = string.Join(" | ", toolLedger),
                ["round_limit_hit"] = (!reachedFinalAnswer && toolLedger.Count > 0).ToString()
            });

        // 6. Speak, unless the user interrupted or the voice is muted.
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.AssistantCompleted,
            TurnId = turnId,
            Text = answerText,
            Detail = string.Join(",", selectedModules.Select(module => module.Id))
        });

        RememberExchange(userText, answerText, toolLedger, anyToolSucceeded);

        if (_settings.Current.Voice.SpeakReplies && !answerSpokenDuringGeneration)
        {
            _mind.MoveTo(MindState.Speaking, "answering aloud");
            await SpeakAnswerAsync(answerText, cancellationToken).ConfigureAwait(false);
        }

        _journal.RecordAnswerCompleted(turnId);
        _mind.MoveTo(MindState.Idle, "turn finished");
        _lastActivity = DateTimeOffset.Now;
        return answerText;
    }

    private Task QueueSpeechAfterAsync(Task previous, string text, CancellationToken cancellationToken) =>
        previous.ContinueWith(async _ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _voice.SpeakAsync(text, cancellationToken).ConfigureAwait(false);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    private static IReadOnlyList<string> TakeCompleteSpeechSentences(StringBuilder buffer, int minimumCharacters)
    {
        List<string> sentences = new();
        int start = 0;
        for (int index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] is not ('.' or '!' or '?'))
            {
                continue;
            }

            string candidate = SpeechTextSanitizer.CleanForSpeech(buffer.ToString(start, index - start + 1));
            if (candidate.Length < Math.Max(1, minimumCharacters))
            {
                continue;
            }

            sentences.Add(candidate);
            start = index + 1;
        }

        if (start > 0)
        {
            buffer.Remove(0, start);
        }

        return sentences;
    }
}
