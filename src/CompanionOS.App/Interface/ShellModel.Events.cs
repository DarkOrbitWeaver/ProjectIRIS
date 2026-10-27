using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Common;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;

namespace CompanionOS.App.Interface;

/// <summary>
/// Every event handler the shell listens to, plus the small commands the window calls.
///
/// Why this is split from the properties: this half is a translation table from her nervous
/// system to the values the interface binds to. Reading it top to bottom is reading the list of
/// things that can happen to her, which is a different question from "what does the window show".
/// </summary>
public sealed partial class ShellModel : ObservableObject
{
    /// <summary>Takes a screenshot and asks her about it.</summary>
    public async Task AskAboutScreenAsync()
    {
        var frame = await _runtime.Perception.LookAsync(true).ConfigureAwait(false);
        if (frame is null)
        {
            // LookAsync's ConfigureAwait(false) put this continuation on a pool thread;
            // the collection change is still raised on the UI thread.
            OnUiThread(() => AddSystemMessage("I could not see the screen just now."));
            return;
        }

        await SendAsync("What is on my screen right now?", frame.DataUri).ConfigureAwait(false);
    }

    private void Subscribe()
    {
        EventBus events = _runtime.Events;

        _subscriptions.Add(events.Subscribe<ConversationEvent>(payload => OnUiThread(() => HandleConversation(payload))));
        _subscriptions.Add(events.Subscribe<ModuleProgressEvent>(payload => OnUiThread(() => HandleModuleProgress(payload))));
        _subscriptions.Add(events.Subscribe<MindStateChangedEvent>(payload => OnUiThread(() => HandleMindState(payload))));
        _subscriptions.Add(events.Subscribe<OperatingModeChangedEvent>(payload => OnUiThread(() => HandleMode(payload))));
        _subscriptions.Add(events.Subscribe<MemoryRecallEvent>(payload => OnUiThread(() => HandleRecall(payload))));
        _subscriptions.Add(events.Subscribe<SpeechEvent>(payload => OnUiThread(() => HandleSpeech(payload))));
        _subscriptions.Add(events.Subscribe<AudioLevelEvent>(payload => OnUiThread(() => HandleAudio(payload))));
        _subscriptions.Add(events.Subscribe<ModelStatusEvent>(payload => OnUiThread(() => HandleModelStatus(payload))));
        _subscriptions.Add(events.Subscribe<RuntimeReadyEvent>(_ => OnUiThread(() => IsRuntimeReady = true)));
        _subscriptions.Add(events.Subscribe<TelemetryEvent>(payload => OnUiThread(() => HandleTelemetry(payload))));
        _subscriptions.Add(events.Subscribe<PermissionRequestedEvent>(payload => OnUiThread(() => HandlePermission(payload))));
        _subscriptions.Add(events.Subscribe<NoticeEvent>(payload => OnUiThread(() => HandleNotice(payload))));
        _subscriptions.Add(events.Subscribe<TranscriptEvent>(payload => OnUiThread(() => HandleTranscript(payload))));
        _subscriptions.Add(events.Subscribe<EarsEvent>(payload => OnUiThread(() => HandleEars(payload))));

        // The shell's own black box: a caught UI-thread fault is no longer just file
        // data — the user is told, so a half-rendered screen never happens silently.
        // SystemWatch rate-limits the raising; Detach unsubscribes.
        SystemWatch.UiFaultObserved += OnSystemWatchFault;
    }

    private void OnSystemWatchFault(string area, Exception exception, string context)
    {
        OnUiThread(() =>
        {
            string contextNote = string.IsNullOrWhiteSpace(context) ? string.Empty : $" ({context})";
            _runtime.Events.PublishNotice(
                $"A shell fault was caught in the {area} area and she kept running — the log " +
                $"has the details. {Trim(exception.Message, 80)}{contextNote}",
                NoticeSeverity.Warning);
        });
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void HandleConversation(ConversationEvent payload)
    {
        if (payload.Phase == ConversationPhase.UserMessage)
        {
            ActiveConversationTurnId = payload.TurnId;
            if (_pendingRecallTurnId != payload.TurnId)
            {
                _pendingRecall = Array.Empty<RecalledMemory>();
                _pendingRecallTurnId = string.Empty;
            }
        }
        else if (!string.Equals(payload.TurnId, ActiveConversationTurnId, StringComparison.Ordinal))
        {
            return;
        }

        switch (payload.Phase)
        {
            case ConversationPhase.UserMessage:
                IsTurnInProgress = true;
                // The pending kernel detail attaches to whichever user bubble comes in
                // next, then clears — it describes exactly one turn.
                Messages.Add(new ChatEntry
                {
                    Role = ChatRole.User,
                    Text = payload.Text,
                    Detail = _pendingUserDetail,
                    TurnId = payload.TurnId
                });
                _pendingUserDetail = string.Empty;
                break;

            case ConversationPhase.AssistantStarted:
                BeginActivity(payload.TurnId, "Thinking", "Iris", "Preparing a response", "body");
                _streamBuffer.Clear();
                _streamingEntry = new ChatEntry
                {
                    Role = ChatRole.Assistant,
                    IsStreaming = true,
                    TurnId = payload.TurnId,
                    RecalledMemories = _runtime.Settings.Current.Interface.ShowRecallChips &&
                                       _pendingRecallTurnId == payload.TurnId
                        ? _pendingRecall
                        : Array.Empty<RecalledMemory>()
                };
                if (_pendingRecallTurnId == payload.TurnId)
                {
                    _pendingRecall = Array.Empty<RecalledMemory>();
                    _pendingRecallTurnId = string.Empty;
                }
                Messages.Add(_streamingEntry);
                break;

            case ConversationPhase.AssistantDelta:
                _streamBuffer.Append(payload.Text);
                LastUtterance = Trim(_streamBuffer.ToString(), 120);
                break;

            case ConversationPhase.AssistantCompleted:
                IsTurnInProgress = false;
                ClearActivity();
                ActiveConversationTurnId = string.Empty;
                if (_streamingEntry is null)
                {
                    Messages.Add(new ChatEntry { Role = ChatRole.Assistant, Text = payload.Text });
                    break;
                }

                _streamingEntry.Text = payload.Text;
                _streamingEntry.IsStreaming = false;
                _streamingEntry = null;
                _streamBuffer.Clear();
                LastUtterance = payload.Text;
                break;

            case ConversationPhase.ToolStarted:
                BeginActivity(payload.TurnId, payload.Text, payload.Detail ?? "Module", "Working", payload.IconKey);
                Messages.Add(new ChatEntry
                {
                    Role = ChatRole.Tool,
                    Text = payload.Text,
                    Detail = payload.Detail ?? string.Empty,
                    TurnId = payload.TurnId,
                    IsToolRunning = true
                });
                break;

            case ConversationPhase.ToolFinished:
                ChatEntry? activeTool = Messages.LastOrDefault(message =>
                    message.Role == ChatRole.Tool && message.TurnId == payload.TurnId && message.IsToolRunning);
                string toolResult = string.IsNullOrEmpty(payload.Detail)
                    ? payload.Text
                    : $"{payload.Text} — {Trim(payload.Detail, 160)}";
                if (activeTool is not null)
                {
                    activeTool.Text = toolResult;
                    activeTool.IsToolRunning = false;
                }
                else
                {
                    // Permission refusals can finish without a ToolStarted event. Keep
                    // their result visible without accidentally rewriting an older step.
                    Messages.Add(new ChatEntry
                    {
                        Role = ChatRole.Tool,
                        Text = toolResult,
                        TurnId = payload.TurnId
                    });
                }

                if (payload.TurnId == ActivityTurnId)
                {
                    BeginActivity(payload.TurnId, "Thinking", "Iris", "Reviewing the result", "body");
                }

                break;

            case ConversationPhase.TurnFailed:
                IsTurnInProgress = false;
                ClearActivity();
                ActiveConversationTurnId = string.Empty;
                FinalizeStreamingTurn(payload.TurnId);
                FinalizeToolSteps(payload.TurnId, "stopped after an error");
                AddSystemMessage($"Something went wrong: {payload.Text}");
                break;

            case ConversationPhase.TurnCancelled:
                IsTurnInProgress = false;
                ClearActivity();
                ActiveConversationTurnId = string.Empty;
                FinalizeStreamingTurn(payload.TurnId);
                FinalizeToolSteps(payload.TurnId, "cancelled");
                AddSystemMessage(payload.Text);
                break;
        }
    }

    private void FinalizeToolSteps(string turnId, string outcome)
    {
        foreach (ChatEntry entry in Messages.Where(message =>
                     message.Role == ChatRole.Tool && message.TurnId == turnId && message.IsToolRunning))
        {
            entry.Text = $"{entry.Text} — {outcome}";
            entry.IsToolRunning = false;
        }
    }

    private void FinalizeStreamingTurn(string turnId)
    {
        if (_streamingEntry is null || _streamingEntry.TurnId != turnId)
        {
            return;
        }

        FlushStream();
        if (string.IsNullOrWhiteSpace(_streamingEntry.Text))
        {
            Messages.Remove(_streamingEntry);
        }
        else
        {
            _streamingEntry.IsStreaming = false;
        }

        _streamingEntry = null;
        _streamBuffer.Clear();
    }

    private void HandleModuleProgress(ModuleProgressEvent payload)
    {
        if (payload.TurnId != ActivityTurnId ||
            (!string.IsNullOrWhiteSpace(ActivityModule) &&
             !string.Equals(ActivityModule, payload.ModuleName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ActivityMessage = string.IsNullOrWhiteSpace(payload.Message) ? ActivityMessage : payload.Message;
        ActivityModule = payload.ModuleName;
        ActivityIconKey = payload.IconKey;
        ActivityProgress = payload.Progress;
        ActivityTotal = payload.Total;
    }

    private void BeginActivity(string turnId, string message, string module, string detail, string iconKey)
    {
        ActivityTurnId = turnId;
        ActivityMessage = message;
        ActivityModule = module;
        ActivityIconKey = iconKey;
        ActivityDetail = detail;
        ActivityProgress = null;
        ActivityTotal = null;
        ActivityStartedAt = DateTimeOffset.Now;
    }

    private void ClearActivity()
    {
        ActivityTurnId = string.Empty;
        ActivityMessage = string.Empty;
        ActivityModule = string.Empty;
        ActivityDetail = string.Empty;
        ActivityProgress = null;
        ActivityTotal = null;
    }

    private void HandleMindState(MindStateChangedEvent payload)
    {
        State = payload.Current;
        StateText = _runtime.Mind.DescribeState();
        IsListening = payload.Current == MindState.Listening || _runtime.Voice.IsListening;
    }

    private void HandleMode(OperatingModeChangedEvent payload)
    {
        Mode = payload.Current;
        StateText = _runtime.Mind.DescribeState();
    }

    private void HandleRecall(MemoryRecallEvent payload)
    {
        if (payload.TurnId != ActiveConversationTurnId)
        {
            return;
        }

        if (_streamingEntry is not null && _streamingEntry.TurnId == payload.TurnId)
        {
            _streamingEntry.RecalledMemories = payload.Memories;
        }
        else
        {
            _pendingRecall = payload.Memories;
            _pendingRecallTurnId = payload.TurnId;
        }
    }

    private void HandleSpeech(SpeechEvent payload)
    {
        IsSpeaking = payload.IsSpeaking;
        if (payload.IsSpeaking && !string.IsNullOrWhiteSpace(payload.Text))
        {
            LastUtterance = payload.Text;
        }
    }

    private void HandleAudio(AudioLevelEvent payload)
    {
        if (payload.IsMicrophone)
        {
            MicrophoneLevel = payload.Level;
            IsListening = true;
        }
        else
        {
            SpeechLevel = payload.Level;
        }
    }

    private void HandleModelStatus(ModelStatusEvent payload)
    {
        ModelLine = payload.Status switch
        {
            ModelStatus.Ready => $"Brain {payload.ProfileName}",
            ModelStatus.Loading => "Brain loading…",
            ModelStatus.Unloaded => "Brain resting",
            _ => "Brain unavailable"
        };

        if (payload.Status == ModelStatus.Unloaded)
        {
            VramText = "GPU VRAM released";
        }
    }

    private void HandleTelemetry(TelemetryEvent payload)
    {
        if (payload.TotalVramMegabytes > 0)
        {
            // nvidia-smi reports the card-wide allocation, not an Iris-only estimate.
            // Say that in the compact strip so a game or another GPU process is never
            // mistakenly blamed on (or hidden from) Iris.
            VramText = $"GPU {payload.UsedVramMegabytes / 1024:0.0}/{payload.TotalVramMegabytes / 1024:0.0} GB";
        }

        if (payload.ProcessRamMegabytes > 0)
        {
            RamText = $"RAM {payload.ProcessRamMegabytes:0} MB";
        }

        if (payload.ContextFillPercent > 0)
        {
            ContextText = $"CTX {payload.ContextFillPercent:0}%";
        }
    }

    private void HandlePermission(PermissionRequestedEvent payload)
    {
        PendingPermission = payload.Request;
        // Restart the deadline for this request. A second request replaces the card, so
        // the old question must not clear the new one when its own clock runs out.
        _permissionTimer.Stop();
        _permissionTimer.Start();
    }

    private void HandleNotice(NoticeEvent payload)
    {
        Notices.Add(new NoticeItem
        {
            Message = payload.Message,
            Severity = payload.Severity,
            ActionLabel = payload.ActionLabel,
            Action = payload.Action
        });
        // The States board allows two stacked cards, no more.
        while (Notices.Count > 2)
        {
            Notices.RemoveAt(0);
        }
    }

    private void HandleTranscript(TranscriptEvent payload)
    {
        if (!payload.IsFinal || !payload.IsUser)
        {
            return;
        }

        // Heard speech appears immediately; the brain adds its own copy a moment later,
        // so this only shows while she is still thinking.
        LastUtterance = payload.Text;
        IsListening = true;
        SetEarsLine($"she heard \u201C{payload.Text.Trim()}\u201D — on it");
    }

    /// <summary>
    /// One line of truth from her ears, displayed while it matters and gone after:
    /// what the recognizer heard, whether the segment was for her, the invoke switch.
    /// This is how the boss can finally see the ears working without opening logs.
    /// </summary>
    private void HandleEars(EarsEvent payload)
    {
        string line = payload.State switch
        {
            "not_addressed" => string.IsNullOrWhiteSpace(payload.Text)
                ? "her ears heard something — waiting for her name"
                : payload.Text,
            "open" => "ears open on purpose — go ahead, Boss",
            "closed" => "ears closed on purpose",
            _ => payload.Text
        };
        SetEarsLine(line);
    }

    private void SetEarsLine(string line)
    {
        EarsLine = line;
        _earsTimer.Stop();
        _earsTimer.Start();
    }

    private void OnEarsTimerTick(object? sender, EventArgs eventArgs)
    {
        _earsTimer.Stop();
        EarsLine = string.Empty;
    }

    private void PruneNotices()
    {
        for (int index = Notices.Count - 1; index >= 0; index--)
        {
            // Short status and success notices clear themselves. Warnings get longer;
            // actionable errors stay until the user reads Details or dismisses them.
            TimeSpan? lifetime = Notices[index].ActionLabel is not null
                ? null
                : Notices[index].Severity switch
            {
                NoticeSeverity.Success => TimeSpan.FromSeconds(4),
                NoticeSeverity.Info => TimeSpan.FromSeconds(5),
                NoticeSeverity.Warning => TimeSpan.FromSeconds(8),
                _ => null
            };

            if (lifetime is { } limit && DateTimeOffset.Now - Notices[index].CreatedAt > limit)
            {
                Notices.RemoveAt(index);
            }
        }
    }

    private void FlushStream()
    {
        if (_streamingEntry is null)
        {
            return;
        }

        string pending = _streamBuffer.ToString();
        if (pending.Length == 0 || pending == _streamingEntry.Text)
        {
            return;
        }

        _streamingEntry.Text = pending;
    }

    private void AddSystemMessage(string text) =>
        Messages.Add(new ChatEntry { Role = ChatRole.System, Text = text });

    private static string Trim(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength].TrimEnd() + "…";

    /// <summary>Releases every subscription. Called when the window really closes.</summary>
    public void Detach()
    {
        SystemWatch.UiFaultObserved -= OnSystemWatchFault;
        _streamFlushTimer.Stop();
        _noticeTimer.Stop();
        _permissionTimer.Stop();
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
