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
/// The turn itself: everything that happens between the user finishing a sentence and her
/// finishing the answer.
///
/// Why this is a separate part of AssistantBrain: the composition and lifecycle of the mind
/// (modes, prompt building, memory seeding) are one concern, and the streaming tool loop is
/// another. Splitting them keeps both readable without pretending they are separate classes,
/// because they share every field.
/// </summary>
public sealed partial class AssistantBrain : IAssistantHostServices, IAsyncDisposable
{
    /// <summary>
    /// What she says when her cortex is deliberately unloaded. Returns null when she just
    /// woke up, which tells the caller to continue the turn normally.
    /// </summary>
    private async Task<string?> AnswerWhileLowPowerAsync(string userText, string turnId,
        CancellationToken cancellationToken)
    {
        // Whole words only. A raw substring test once matched "up" inside "set up" and
        // "make up", which woke her cortex on ordinary chatter. "wake" already covers
        // "wake up", and a bare "up" is not a wake request — her own rest answer tells
        // the user to say "wake up".
        bool asksToWake = ContainsWholeWord(userText, "wake") ||
                          ContainsWholeWord(userText, "awake") ||
                          ContainsWholeWord(userText, "think");

        if (_pendingGameRequest is not null &&
            DateTimeOffset.Now - _pendingGameRequest.RequestedAt < TimeSpan.FromMinutes(5))
        {
            asksToWake = true;
        }

        string answer;
        if (asksToWake)
        {
            await SetOperatingModeAsync(OperatingMode.Awake, cancellationToken).ConfigureAwait(false);
            return null;
        }

        answer = _mind.CurrentMode == OperatingMode.Gaming
            ? "I'm in gaming mode so the GPU is yours. Say \"wake up\" if you want me thinking again."
            : "I'm resting with my brain unloaded. Say \"wake up\" and I'll be back in a moment.";

        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.AssistantCompleted,
            TurnId = turnId,
            Text = answer
        });

        await _voice.SpeakReflexAsync(
            _mind.CurrentMode == OperatingMode.Gaming ? "gaming" : "sleep", cancellationToken)
            .ConfigureAwait(false);

        _mind.MoveTo(_mind.CurrentMode == OperatingMode.Gaming ? MindState.Gaming : MindState.Sleeping,
            "answered with reflex only");
        return answer;
    }

    /// <summary>
    /// True when <paramref name="word"/> appears as a complete word, so "up" inside
    /// "set up" never matches. Letters at both edges disqualify the hit, which keeps
    /// punctuation like "Wake up." or "wake," working.
    /// </summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        int searchFrom = 0;
        while (searchFrom <= text.Length - word.Length)
        {
            int index = text.IndexOf(word, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int end = index + word.Length;
            bool cleanStart = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            bool cleanEnd = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (cleanStart && cleanEnd)
            {
                return true;
            }

            searchFrom = index + 1;
        }

        return false;
    }

    /// <summary>Loads the cortex if needed, and reports honestly when it cannot.</summary>
    private async Task<bool> EnsureCortexReadyAsync(CancellationToken cancellationToken)
    {
        BrainProfile profile = _settings.Current.ActiveBrainProfile;

        if (profile.Kind == BrainHostKind.External)
        {
            bool reachable = await _modelClient.IsEndpointReachableAsync(profile.Endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!reachable)
            {
                _events.Publish(new ModelStatusEvent
                {
                    Status = ModelStatus.Failed,
                    ProfileName = profile.DisplayName,
                    Detail = $"Nothing is answering at {profile.Endpoint}."
                });
            }

            return reachable;
        }

        if (_serverHost.IsRunning)
        {
            if (_serverHost.IsReady)
            {
                return true;
            }

            // The child is alive but the weights are still loading — the exact window
            // that 503'd the owner's first typed message. Wait bounded, then answer
            // honestly; never stream into a half-loaded brain.
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!_serverHost.IsReady && DateTime.UtcNow < deadline &&
                   !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            return _serverHost.IsReady;
        }

        await SetOperatingModeAsync(OperatingMode.Awake, cancellationToken).ConfigureAwait(false);
        return _serverHost.IsRunning;
    }

    /// <summary>Starts the model and moves her out of sleep or gaming mode.</summary>
    private async Task WakeCortexAsync(CancellationToken cancellationToken)
    {
        BrainProfile profile = _settings.Current.ActiveBrainProfile;

        // Whatever mode she is leaving, awake means her ears are open again.
        _voice.ResumeListening();

        if (profile.Kind == BrainHostKind.External)
        {
            bool reachable = await _modelClient.IsEndpointReachableAsync(profile.Endpoint, cancellationToken)
                .ConfigureAwait(false);
            _events.Publish(new ModelStatusEvent
            {
                Status = reachable ? ModelStatus.Ready : ModelStatus.Failed,
                ProfileName = profile.DisplayName,
                Detail = reachable ? profile.Endpoint : $"No answer from {profile.Endpoint}."
            });
            _mind.MoveModeTo(OperatingMode.Awake, "external brain");
            _mind.MoveTo(MindState.Idle, "ready");
            return;
        }

        _events.Publish(new ModelStatusEvent
        {
            Status = ModelStatus.Loading,
            ProfileName = profile.DisplayName,
            Detail = "Loading the model into VRAM."
        });

        try
        {
            await _serverHost.StartAsync(profile, _settings.Current, cancellationToken).ConfigureAwait(false);
            _events.Publish(new ModelStatusEvent
            {
                Status = ModelStatus.Ready,
                ProfileName = profile.DisplayName,
                Detail = $"{profile.ContextTokens} token context, vision {(profile.SupportsVision ? "on" : "off")}."
            });
            _mind.MoveModeTo(OperatingMode.Awake, "cortex loaded");
            _mind.MoveTo(MindState.Idle, "cortex loaded");
            _pendingGameRequest = null;
        }
        catch (Exception exception)
        {
            CompanionLog.Error("Brain", "The cortex did not start.", exception);
            _events.Publish(new ModelStatusEvent
            {
                Status = ModelStatus.Failed,
                ProfileName = profile.DisplayName,
                Detail = exception.Message
            });
            _events.PublishNotice(
                "Her brain did not load. Run scripts/setup.ps1 or check the model path in settings.",
                NoticeSeverity.Danger);
        }
    }

    /// <summary>Unloads the GPU for sleep or gaming, keeping her senses alive.</summary>
    private async Task EnterLowPowerModeAsync(OperatingMode mode, string reason,
        CancellationToken cancellationToken)
    {
        _voice.StopSpeaking();
        CancelCurrentTurn();
        // Publish the mode before awaiting process teardown. This makes the state machine
        // authoritative while the server is stopping, so a concurrent turn cannot start a
        // second server against a half-unloaded cortex.
        _mind.MoveModeTo(mode, reason);

        await _serverHost.StopAsync().ConfigureAwait(false);

        // Gaming Mode's promise is that the machine belongs to the game. With the
        // setting off (the default) her ears close completely: no microphone, no
        // speech model in memory, no transcription competing for cores.
        // Resting by idle keeps her ears, because she is supposed to still answer
        // when the user comes back and says her name. A full Sleep never reaches this
        // method at all — it disposes the whole runtime (docs/DECISIONS.md §28).
        bool earsClosed = mode == OperatingMode.Gaming && !_settings.Current.System.GamingEarsEnabled;
        if (earsClosed)
        {
            _voice.SuspendListening();
        }

        _events.Publish(new ModelStatusEvent
        {
            Status = ModelStatus.Unloaded,
            ProfileName = _settings.Current.ActiveBrainProfile.DisplayName,
            Detail = mode == OperatingMode.Gaming
                ? earsClosed
                    ? "GPU and CPU released for the game. Only the overlay and hotkeys are live."
                    : "GPU released for the game. Ears and reflexes are still running."
                : "Model unloaded. Ears and reflexes are still running."
        });

        _journal.RecordState(_mind.CurrentState, reason);

        // Mode announcements live behind the reflex switch: they are her saying
        // something nobody asked for, and the switch owns exactly that.
        if (_settings.Current.Voice.ReflexPhrasesEnabled)
        {
            await _voice.SpeakReflexAsync(mode == OperatingMode.Gaming ? "gaming" : "sleep", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SpeakAnswerAsync(string answerText, CancellationToken cancellationToken)
    {
        _speechSource?.Dispose();
        _speechSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // The written answer stays whole on screen; the voice carries the verdict.
            string spoken = SpeechPlanner.TrimForSpeech(
                SpeechTextSanitizer.StripControlTags(answerText),
                _settings.Current.Voice.SpeechBudgetCharacters);
            await _voice.SpeakAsync(spoken, _speechSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user interrupted; that is the intended path.
        }
    }

    /// <summary>Keeps the sliding conversation window and writes episodes worth keeping.</summary>
    private void RememberExchange(string userText, string answerText, IReadOnlyList<string> toolLedger,
        bool toolsSucceeded)
    {
        lock (_historyLock)
        {
            _history.Add(ChatMessage.User(userText));
            _history.Add(ChatMessage.Assistant(answerText));

            int maximumMessages = Math.Max(4, _settings.Current.Memory.ConversationWindowMessages);
            while (_history.Count > maximumMessages)
            {
                _history.RemoveAt(0);
            }
        }

        if (toolLedger.Count == 0)
        {
            return;
        }

        // Episodes record what happened, never what was asked. The old wording
        // ("Requested: open chrome") turned a launch she failed to run into a standing
        // memory of an intention, and that false memory steered later replies. The
        // ledger is the honest record: which hand ran, whether it went through.
        //
        // Transcription debris ("a", "uh", "play") is not an event; a fragment this thin
        // becomes a node of noise. Follow-up garbage stays noise — no memory.
        if (!IsRememberWorthyRequest(userText))
        {
            return;
        }

        string request = userText.Length > 90 ? userText[..90] + "…" : userText;
        string outcomes = string.Join("; ", toolLedger.Take(3));
        string summary = toolsSucceeded
            ? $"Did: {request} — {outcomes}"
            : $"Tried: {request} — it did not go through ({outcomes})";
        string labels = string.Join(", ", toolLedger.Take(2).Select(LabelOfLedgerEntry));
        string headline = toolsSucceeded ? $"Did: {labels}" : $"Tried: {labels}";
        // These are her activity diary, not world knowledge: the dedicated provenance
        // keeps them out of the automatic intuition seeds (SynapticMemoryEngine filters
        // task_log there) while recall_memory can still search them whenever she wants
        // her own history.
        _ = _memory.RecordEpisodeAsync(summary, SynapticMemoryEngine.TaskLogProvenance, title: headline)
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Turns "play music: done" into "play music" — the action she ran.</summary>
    private static string LabelOfLedgerEntry(string entry)
    {
        int statusCut = entry.LastIndexOf(':');
        return (statusCut >= 0 ? entry[..statusCut] : entry).Trim();
    }

    /// <summary>
    /// The ears catch all kinds of fragments; only the ones carrying some readable
    /// train of words are events worth remembering. A single word or a few stray
    /// letters ("play", "huh?") say nothing true about the day, so they are skipped
    /// before they become noise nodes.
    /// </summary>
    private static bool IsRememberWorthyRequest(string userText)
    {
        string[] words = userText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return false;
        }

        int letters = userText.Count(char.IsLetter);
        return letters >= 8;
    }

    /// <summary>
    /// The receipt-backed answer the OS owns: when her own words collapse or carry a
    /// claim the receipts contradict, these are the words she says instead. Written by
    /// the loop, never the model — the one part of a turn that can no longer lie.
    /// </summary>
    private static string BuildReceiptAnswer(IReadOnlyList<string> toolLedger, bool anyToolSucceeded,
        string? failureDetail)
    {
        if (anyToolSucceeded)
        {
            string done = string.Join("; ", toolLedger
                .Where(entry => entry.EndsWith("done", StringComparison.OrdinalIgnoreCase))
                .Take(3));
            if (done.Length == 0)
            {
                done = string.Join("; ", toolLedger.Take(2));
            }

            return $"Done — {done}.";
        }

        if (toolLedger.Count > 0)
        {
            string why = string.IsNullOrWhiteSpace(failureDetail)
                ? string.Empty
                : $" {failureDetail.TrimEnd('.').TrimEnd(',')}";
            return $"That did not go through.{why} Want me to try another way?";
        }

        // One sentence, one segment: the 104-character version of this line was 5.8
        // seconds of speech for a turn that accomplished nothing.
        return "Nothing got done that turn — say the word and I will go for it.";
    }

    /// <summary>Keeps the first sentence of a refusal — the honest "why", not a wall.</summary>
    private static string FirstSentenceOf(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        int cut = trimmed.IndexOf('.');
        string sentence = cut >= 0 ? trimmed[..(cut + 1)] : trimmed;
        return sentence.Length <= 140 ? sentence : sentence[..140].TrimEnd() + "…";
    }

    private static Dictionary<string, string?> ReceiptData(IReadOnlyList<string> toolLedger) => new()
    {
        ["receipts"] = string.Join(" | ", toolLedger)
    };

    private IReadOnlyList<ChatMessage> TakeHistoryWindow()
    {
        lock (_historyLock)
        {
            return _history.ToList();
        }
    }

    private void OnUserRequested(RecognizedVoiceRequest request) => _ = HandleVoiceRequestAsync(request);

    private void OnUserInterrupted()
    {
        _speechSource?.Cancel();
        _voice.StopSpeaking();
    }

    private void OnUserCalledHer(string interactionId)
    {
        _lastActivity = DateTimeOffset.Now;
        _voice.OpenConversationWindow();
        _mind.MoveTo(MindState.Listening, "called by name");
        _ = HandleUserCallAsync(interactionId);
    }

    /// <summary>
    /// Voice is a foreground interaction: surface it, pause only the media we own, and give the
    /// user a bounded listening window. No screen capture, model load or background scan occurs
    /// here. This is the desktop equivalent of an Android assistant becoming present on call.
    /// </summary>
    private async Task HandleUserCallAsync(string interactionId)
    {
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(interactionId);
        // Do not speak over a film or song. Waiting for the tiny Windows media operation here
        // also makes the visual call state and the audible greeting arrive as one interaction.
        await BeginVoiceAttentionAsync("called by name", TimeSpan.FromSeconds(28), interactionId).ConfigureAwait(false);
        await PlayGreetingAsync().ConfigureAwait(false);
    }

    private async Task BeginVoiceAttentionAsync(string reason, TimeSpan holdFor, string interactionId)
    {
        _events.Publish(new AttentionRequestedEvent
        {
            Reason = reason,
            HoldFor = holdFor,
            TurnId = interactionId
        });
        await _perception.PauseMediaForVoiceAsync().ConfigureAwait(false);

        _voiceAttentionTimeout?.Cancel();
        _voiceAttentionTimeout?.Dispose();
        _voiceAttentionTimeout = new CancellationTokenSource();
        CancellationToken timeoutToken = _voiceAttentionTimeout.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(holdFor, timeoutToken).ConfigureAwait(false);
                await _perception.ResumeMediaAfterVoiceAsync(timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A real request arrived, or Iris was closed. The request owns the next step.
            }
        }, timeoutToken);
    }

    private async Task HandleVoiceRequestAsync(RecognizedVoiceRequest request)
    {
        using IDisposable correlationScope = IrisEvent.BeginCorrelationScope(request.InteractionId);
        await BeginVoiceAttentionAsync("voice request", TimeSpan.FromSeconds(45), request.InteractionId)
            .ConfigureAwait(false);
        _voiceAttentionTimeout?.Cancel();
        try
        {
            await SendMessageAsync(request.Text, "voice", interactionId: request.InteractionId).ConfigureAwait(false);
        }
        finally
        {
            // Start the follow-up window after the reply finishes, not when recognition
            // starts. Long model and speech turns must not consume the whole window.
            _voice.OpenConversationWindow(45);
            await _perception.ResumeMediaAfterVoiceAsync().ConfigureAwait(false);
        }
    }

    private async Task PlayGreetingAsync()
    {
        if (_settings.Current.Voice.ReflexPhrasesEnabled)
        {
            await _voice.SpeakReflexAsync("greeting").ConfigureAwait(false);
        }

        _mind.MoveTo(MindState.Idle, "greeting finished");
    }

    private void OnSituationUpdated(SituationSnapshot snapshot)
    {
        if (_settings.Current.System.ManualGamingMode)
        {
            // The explicit hand-off is only a lock while Iris is actually in Gaming Mode.
            // If another path woke her (for example, the owner typed to her), clear the
            // stale lock so automatic game detection is available again.
            if (_mind.CurrentMode == OperatingMode.Gaming)
            {
                return;
            }

            _settings.Current.System.ManualGamingMode = false;
            _settings.Save();
        }

        if (!_settings.Current.System.GamingModeEnabled || !_settings.Current.System.GamingAutoDetect)
        {
            return;
        }

        bool gameInFront = !string.IsNullOrWhiteSpace(_perception.ForegroundGame);
        bool shouldEnterGaming = gameInFront && _mind.CurrentMode == OperatingMode.Awake;
        bool shouldLeaveGaming = !gameInFront && _mind.CurrentMode == OperatingMode.Gaming;

        if (shouldEnterGaming)
        {
            _pendingGameRequest = new GameModeRequest { RequestedAt = DateTimeOffset.Now };
            _ = SetOperatingModeAsync(OperatingMode.Gaming, CancellationToken.None);
        }
        else if (shouldLeaveGaming)
        {
            _ = SetOperatingModeAsync(OperatingMode.Awake, CancellationToken.None);
            if (_settings.Current.Voice.ReflexPhrasesEnabled)
            {
                _ = SpeakReflexInBackgroundAsync("gamingEnd");
            }
        }

        _ = snapshot;
    }

    /// <summary>Idle sleep, memory consolidation and the game-release check.</summary>
    private void Housekeeping()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            TimeSpan idle = DateTimeOffset.Now - _lastActivity;
            int autoSleepMinutes = _settings.Current.Brain.AutoSleepIdleMinutes;

            if (autoSleepMinutes > 0 &&
                _mind.CurrentMode == OperatingMode.Awake &&
                idle > TimeSpan.FromMinutes(autoSleepMinutes) &&
                _perception.Current.IsUserIdle)
            {
                _ = SetOperatingModeAsync(OperatingMode.Sleeping, CancellationToken.None);
            }

            MemorySettings memorySettings = _settings.Current.Memory;
            if (memorySettings.ConsolidationEnabled &&
                idle > TimeSpan.FromMinutes(memorySettings.ConsolidationIdleMinutes) &&
                _mind.CurrentMode == OperatingMode.Awake &&
                Interlocked.Exchange(ref _consolidationInProgress, 1) == 0)
            {
                _ = ConsolidateWhenIdleAsync();
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Brain", $"Housekeeping failed: {exception.Message}");
        }
    }

    private async Task ConsolidateWhenIdleAsync()
    {
        try
        {
            await _memory.Graph.ConsolidateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Brain", $"Memory consolidation failed: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _consolidationInProgress, 0);
        }
    }

    private string DescribeMode() => _mind.CurrentMode switch
    {
        OperatingMode.Awake => "awake, full capability",
        OperatingMode.Sleeping => "sleeping, thinking model unloaded, reflexes only",
        _ => "gaming, thinking model unloaded, reflexes only, never use screen vision"
    };

    /// <summary>Creates her Layer 0 self once, so she is the same person after any restart.</summary>
    private async Task SeedPersonaAsync(Persona persona, CancellationToken cancellationToken)
    {
        await _memory.Graph.UpsertNodeAsync(
            persona.Name,
            $"{persona.Tagline} Speaks warmly, briefly, addresses the user as {persona.UserAddress}.",
            MemoryLayer.Core,
            "persona",
            pinned: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _memory.Graph.UpsertNodeAsync(
            persona.UserAddress,
            "The person she lives with and works for.",
            MemoryLayer.Core,
            "persona",
            pinned: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rough context accounting for the status strip, deliberately cheap.</summary>
    private void PublishContextTelemetry(IReadOnlyList<ChatMessage> messages)
    {
        int estimatedTokens = messages.Sum(message => (message.Content.Length / 4) + 4);
        int contextSize = Math.Max(2048, _settings.Current.ActiveBrainProfile.ContextTokens);

        _events.Publish(new TelemetryEvent
        {
            ContextFillPercent = Math.Clamp(estimatedTokens * 100.0 / contextSize, 0, 100)
        });
    }

    /// <summary>Remembers that the user asked for her brain while a game was running.</summary>
    private sealed class GameModeRequest
    {
        public DateTimeOffset RequestedAt { get; init; }
    }
}
