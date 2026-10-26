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
            AddSystemMessage("I could not see the screen just now.");
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
        _subscriptions.Add(events.Subscribe<TelemetryEvent>(payload => OnUiThread(() => HandleTelemetry(payload))));
        _subscriptions.Add(events.Subscribe<PermissionRequestedEvent>(payload => OnUiThread(() => HandlePermission(payload))));
        _subscriptions.Add(events.Subscribe<NoticeEvent>(payload => OnUiThread(() => HandleNotice(payload))));
        _subscriptions.Add(events.Subscribe<TranscriptEvent>(payload => OnUiThread(() => HandleTranscript(payload))));
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
                    Detail = _pendingUserDetail
                });
                _pendingUserDetail = string.Empty;
                break;

            case ConversationPhase.AssistantStarted:
                BeginActivity(payload.TurnId, "Thinking", "Iris", "Preparing a response");
                _streamBuffer.Clear();
                _streamingEntry = new ChatEntry
                {
                    Role = ChatRole.Assistant,
                    IsStreaming = true,
                    RecalledTitles = _runtime.Settings.Current.Interface.ShowRecallChips
                        ? _pendingRecall
                        : Array.Empty<string>()
                };
                _pendingRecall = Array.Empty<string>();
                Messages.Add(_streamingEntry);
                break;

            case ConversationPhase.AssistantDelta:
                _streamBuffer.Append(payload.Text);
                LastUtterance = Trim(_streamBuffer.ToString(), 120);
                break;

            case ConversationPhase.AssistantCompleted:
                IsTurnInProgress = false;
                ClearActivity();
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
                BeginActivity(payload.TurnId, payload.Text, payload.Detail ?? "Module", "Working");
                Messages.Add(new ChatEntry
                {
                    Role = ChatRole.Tool,
                    Text = payload.Text,
                    Detail = payload.Detail ?? string.Empty
                });
                break;

            case ConversationPhase.ToolFinished:
                if (Messages.Count > 0 && Messages[^1].Role == ChatRole.Tool && !string.IsNullOrEmpty(payload.Detail))
                {
                    Messages[^1].Text = $"{payload.Text} — {Trim(payload.Detail, 160)}";
                }

                break;

            case ConversationPhase.TurnFailed:
                IsTurnInProgress = false;
                ClearActivity();
                AddSystemMessage($"Something went wrong: {payload.Text}");
                break;
        }
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
        ActivityProgress = payload.Progress;
        ActivityTotal = payload.Total;
    }

    private void BeginActivity(string turnId, string message, string module, string detail)
    {
        ActivityTurnId = turnId;
        ActivityMessage = message;
        ActivityModule = module;
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
        IReadOnlyList<string> titles = payload.Memories.Select(memory => memory.Title).ToList();
        if (_streamingEntry is not null)
        {
            _streamingEntry.RecalledTitles = titles;
        }
        else
        {
            _pendingRecall = titles;
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
        Notices.Add(new NoticeItem { Message = payload.Message, Severity = payload.Severity });
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
    }

    private void PruneNotices()
    {
        for (int index = Notices.Count - 1; index >= 0; index--)
        {
            // Board timing: 4 s for good news, 8 s for a warning, and errors stay until
            // the user reads them (Details) or dismisses the card.
            TimeSpan? lifetime = Notices[index].Severity switch
            {
                NoticeSeverity.Success => TimeSpan.FromSeconds(4),
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
