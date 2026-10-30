using System.Security.Cryptography;
using System.Text;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Prompts;

/// <summary>Shared knobs. Settings can feed these later; the defaults are the safe ones.</summary>
public sealed class PromptPolicy
{
    /// <summary>Master switch for answering by voice. Typed and button answers always work.</summary>
    public bool VoiceAnswers { get; init; } = true;

    /// <summary>Read questions aloud when she is awake and the voice is online.</summary>
    public bool SpeakPrompts { get; init; } = true;

    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ChoiceTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Speech that lands this soon after a prompt appears is ignored. It is almost always
    /// her own voice reading the question back through the microphone.
    /// </summary>
    public TimeSpan MinimumVoiceDelay { get; init; } = TimeSpan.FromMilliseconds(900);

    public int MaximumQueued { get; init; } = 8;
}

/// <summary>What happened to an utterance while a prompt was open.</summary>
/// <param name="Handled">True when the prompt consumed it. It must NOT reach the model.</param>
/// <param name="Answered">True when this utterance completed the prompt.</param>
/// <param name="Reply">A short line Iris can say or show next ("Say approve, or deny.").</param>
public readonly record struct PromptUtteranceResult(bool Handled, bool Answered = false, string? Reply = null);

/// <summary>Published whenever the front of the queue changes or a prompt finishes.</summary>
public sealed class PromptChangedEvent : CompanionEvent
{
    /// <summary>The prompt now on screen, or null when none is waiting.</summary>
    public IrisPrompt? Active { get; init; }

    /// <summary>The prompt that just finished, if this event is a resolution.</summary>
    public IrisPrompt? Resolved { get; init; }

    public PromptAnswer? Answer { get; init; }
}

/// <summary>What a module sees. It can ask; it cannot see the screen or other modules.</summary>
public interface IModulePrompts
{
    Task<PromptAnswer> AskAsync(PromptRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The one place questions live. Permission approvals, module questions and the model's
/// own "which one?" all pass through here, so they all get the same three ways to answer
/// (button, voice, typing), the same safety rules, the same overlay and the same timeouts.
///
/// One prompt is on screen at a time; the rest wait in order and their clocks start only
/// when they reach the front, so a queued question never expires unseen.
/// </summary>
public sealed class PromptService
{
    private readonly EventBus _events;
    private readonly Func<PromptPolicy> _policy;
    private readonly SettingsStore? _settingsStore;
    private readonly object _gate = new();
    private readonly object _rememberedChoiceGate = new();
    private readonly List<IrisPrompt> _queue = new();
    private readonly Dictionary<Guid, Timer> _timers = new();
    private readonly Dictionary<string, string> _rememberedChoices;
    private int _unheardAttempts;

    public PromptService(EventBus events, Func<PromptPolicy>? policy = null, SettingsStore? settingsStore = null)
    {
        _events = events;
        _settingsStore = settingsStore;
        PromptPolicy fallback = new();
        _policy = policy ?? (() => fallback);
        _rememberedChoices = settingsStore is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(settingsStore.Current.RememberedPromptChoices, StringComparer.Ordinal);
    }

    public PromptPolicy Policy => _policy();

    /// <summary>The prompt currently on screen, if any.</summary>
    public IrisPrompt? Active
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count == 0 ? null : _queue[0];
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public int RememberedChoiceCount
    {
        get
        {
            lock (_rememberedChoiceGate)
            {
                return _rememberedChoices.Count;
            }
        }
    }

    public void ClearRememberedChoices()
    {
        lock (_rememberedChoiceGate)
        {
            _rememberedChoices.Clear();
            _settingsStore?.Mutate(settings =>
            {
                settings.RememberedPromptChoices.Clear();
                return true;
            });
        }
    }

    /// <summary>A scoped client for one module. Stamps the module's name and icon on every prompt.</summary>
    public IModulePrompts ForModule(string moduleId, string moduleName, string iconKey) =>
        new ModuleClient(this, moduleId, moduleName, iconKey);

    /// <summary>Builds a prompt from a request without queueing it.</summary>
    public IrisPrompt Build(PromptRequest request, string ownerId, string ownerName, string iconKey)
    {
        IReadOnlyList<PromptOption> options = request.Options;
        PromptKind kind = request.Kind ?? ChooseKind(options);
        if (options.Count == 0)
        {
            options = new[]
            {
                new PromptOption(PromptIds.Yes, "Yes", Role: PromptOptionRole.Approve),
                new PromptOption(PromptIds.No, "No", Role: PromptOptionRole.Deny)
            };
            kind = PromptKind.Confirm;
        }

        string title = request.Title ?? (kind == PromptKind.Confirm ? "Needs your OK" : "Quick question");
        PromptPolicy policy = _policy();
        TimeSpan timeout = request.Timeout ?? (kind == PromptKind.Confirm ? policy.ConfirmTimeout : policy.ChoiceTimeout);
        string? timeoutOption = request.TimeoutOptionId ??
                                (kind == PromptKind.Confirm
                                    ? options.FirstOrDefault(option => option.Role == PromptOptionRole.Deny)?.Id
                                    : null);

        bool canRemember = request.AllowRemembering && kind == PromptKind.Choice;
        string? rememberKey = canRemember ? RememberKey(ownerId, request.Question, options) : null;
        return new IrisPrompt(kind, title, request.Question, options, request.Risk, timeout, timeoutOption,
            request.SpeakAloud, ownerId, ownerName, iconKey)
        {
            CanRememberChoice = canRemember,
            RememberChoiceKey = rememberKey
        };
    }

    /// <summary>Queues a prompt and waits for the first answer, a timeout, or cancellation.</summary>
    public async Task<PromptAnswer> AskAsync(IrisPrompt prompt, CancellationToken cancellationToken)
    {
        if (prompt.IsResolved)
        {
            return await prompt.Completion.ConfigureAwait(false);
        }

        if (prompt.CanRememberChoice && prompt.RememberChoiceKey is { } rememberKey &&
            TryGetRememberedChoice(rememberKey, out string? rememberedOption) &&
            rememberedOption is not null && prompt.FindOption(rememberedOption) is not null)
        {
            PromptAnswer remembered = new(rememberedOption, PromptSource.Remembered);
            if (!prompt.TryAnswer(remembered))
            {
                return await prompt.Completion.ConfigureAwait(false);
            }
            IrisEvent.Log("Prompts", "prompt.remembered", LogLevel.Info,
                data: new Dictionary<string, string?>
                {
                    ["owner"] = prompt.OwnerId,
                    ["kind"] = prompt.Kind.ToString()
                });
            _events.Publish(new PromptChangedEvent { Active = Active, Resolved = prompt, Answer = remembered });
            return remembered;
        }

        bool accepted;
        lock (_gate)
        {
            accepted = _queue.Count < _policy().MaximumQueued;
            if (accepted)
            {
                _queue.Add(prompt);
            }
        }

        if (!accepted)
        {
            return new PromptAnswer(prompt.TimeoutOptionId ?? string.Empty, PromptSource.Cancelled);
        }

        ActivateFront();

        try
        {
            return await prompt.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Resolve(prompt, new PromptAnswer(prompt.TimeoutOptionId ?? string.Empty, PromptSource.Cancelled));
            throw;
        }
    }

    /// <summary>Convenience for modules: build, queue, wait.</summary>
    public Task<PromptAnswer> AskAsync(PromptRequest request, string ownerId, string ownerName, string iconKey,
        CancellationToken cancellationToken) =>
        AskAsync(Build(request, ownerId, ownerName, iconKey), cancellationToken);

    /// <summary>A button press. Returns false when the option is unknown or the prompt is already answered.</summary>
    public bool Answer(Guid promptId, string optionId, PromptSource source = PromptSource.Button)
    {
        IrisPrompt? prompt;
        lock (_gate)
        {
            prompt = _queue.FirstOrDefault(candidate => candidate.Id == promptId);
        }

        if (prompt is null || prompt.FindOption(optionId) is null)
        {
            return false;
        }

        return Resolve(prompt, new PromptAnswer(optionId, source));
    }

    /// <summary>Dismisses the front prompt as "no answer". Used by Escape and sleep.</summary>
    public void CancelActive()
    {
        IrisPrompt? prompt = Active;
        if (prompt is not null)
        {
            Resolve(prompt, new PromptAnswer(prompt.TimeoutOptionId ?? string.Empty, PromptSource.Cancelled));
        }
    }

    /// <summary>Dismisses everything. Used at shutdown.</summary>
    public void CancelAll()
    {
        List<IrisPrompt> all;
        lock (_gate)
        {
            all = _queue.ToList();
        }

        foreach (IrisPrompt prompt in all)
        {
            Resolve(prompt, new PromptAnswer(prompt.TimeoutOptionId ?? string.Empty, PromptSource.Cancelled));
        }
    }

    /// <summary>
    /// The front door for spoken and typed answers. Call it BEFORE the text reaches the model.
    /// While a question is open, unrelated speech is consumed here too: a new model turn
    /// would queue behind the one that is waiting for this very answer.
    /// </summary>
    public PromptUtteranceResult HandleUtterance(string? text, PromptSource source)
    {
        IrisPrompt? prompt = Active;
        if (prompt is null)
        {
            return new PromptUtteranceResult(false);
        }

        PromptPolicy policy = _policy();
        if (source == PromptSource.Voice)
        {
            if (!policy.VoiceAnswers)
            {
                return new PromptUtteranceResult(true, Reply: "Voice answers are off. Use the buttons or type.");
            }

            if (DateTimeOffset.Now - prompt.ActivatedAt < policy.MinimumVoiceDelay)
            {
                // Her own voice reading the question. Ignore silently.
                return new PromptUtteranceResult(true);
            }
        }

        UtteranceMatch match = PromptSpeechMatcher.Match(prompt, text);
        switch (match.Outcome)
        {
            case UtteranceOutcome.Answer when match.Option is not null:
                _unheardAttempts = 0;
                Resolve(prompt, new PromptAnswer(match.Option.Id, source, text));
                return new PromptUtteranceResult(true, Answered: true);

            case UtteranceOutcome.Cancel:
                _unheardAttempts = 0;
                Resolve(prompt, new PromptAnswer(match.Option?.Id ?? prompt.TimeoutOptionId ?? string.Empty,
                    match.Option is null ? PromptSource.Cancelled : source, text));
                return new PromptUtteranceResult(true, Answered: true);

            case UtteranceOutcome.Page:
                prompt.Page(match.PageDelta);
                return new PromptUtteranceResult(true, Reply: prompt.DescribeForSpeech());

            case UtteranceOutcome.Repeat:
                return new PromptUtteranceResult(true, Reply: prompt.DescribeForSpeech());

            default:
                string hint = string.IsNullOrWhiteSpace(match.Hint) ? PromptSpeechMatcher.DefaultHint(prompt) : match.Hint;
                prompt.SetHint(hint);
                _unheardAttempts++;
                IrisEvent.Log("Prompts", "utterance.unmatched", LogLevel.Debug,
                    data: new Dictionary<string, string?>
                    {
                        ["outcome"] = match.Outcome.ToString(),
                        ["source"] = source.ToString(),
                        ["words"] = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString()
                    });

                // Spoken re-asks stay rare. Background chatter must not turn her into a nag.
                bool shouldSpeak = source == PromptSource.Voice &&
                                   (match.Outcome is UtteranceOutcome.NeedsExplicitApproval or UtteranceOutcome.Ambiguous ||
                                    _unheardAttempts <= 2);
                return new PromptUtteranceResult(true, Reply: shouldSpeak ? hint : null);
        }
    }

    // ---------------------------------------------------------------- internals

    private static PromptKind ChooseKind(IReadOnlyList<PromptOption> options)
    {
        if (options.Count == 0)
        {
            return PromptKind.Confirm;
        }

        bool shortLabels = options.All(option => option.Label.Length <= 28 && string.IsNullOrEmpty(option.Detail));
        return options.Count <= 4 && shortLabels ? PromptKind.Choice : PromptKind.Numbered;
    }

    private bool Resolve(IrisPrompt prompt, PromptAnswer answer)
    {
        if (!prompt.TryAnswer(answer))
        {
            return false;
        }

        if (prompt.CanRememberChoice && prompt.RememberChoice && answer.IsDeliberate &&
            prompt.RememberChoiceKey is { } key && prompt.FindOption(answer.OptionId) is not null)
        {
            RememberChoice(key, answer.OptionId);
        }

        lock (_gate)
        {
            _queue.Remove(prompt);
            if (_timers.Remove(prompt.Id, out Timer? timer))
            {
                timer.Dispose();
            }
        }

        IrisEvent.Log("Prompts", "prompt.resolved", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["owner"] = prompt.OwnerId,
                ["kind"] = prompt.Kind.ToString(),
                ["option"] = answer.OptionId,
                ["source"] = answer.Source.ToString()
            });

        _unheardAttempts = 0;
        _events.Publish(new PromptChangedEvent { Active = Active, Resolved = prompt, Answer = answer });
        ActivateFront();
        return true;
    }

    private bool TryGetRememberedChoice(string key, out string? optionId)
    {
        lock (_rememberedChoiceGate)
        {
            return _rememberedChoices.TryGetValue(key, out optionId);
        }
    }

    private void RememberChoice(string key, string optionId)
    {
        lock (_rememberedChoiceGate)
        {
            _rememberedChoices[key] = optionId;
            while (_rememberedChoices.Count > 100)
            {
                _rememberedChoices.Remove(_rememberedChoices.Keys.First());
            }
            _settingsStore?.Mutate(settings =>
            {
                settings.RememberedPromptChoices[key] = optionId;
                while (settings.RememberedPromptChoices.Count > 100)
                {
                    settings.RememberedPromptChoices.Remove(settings.RememberedPromptChoices.Keys.First());
                }
                return true;
            });
        }
    }

    private static string RememberKey(string ownerId, string question, IReadOnlyList<PromptOption> options)
    {
        string input = $"choice-v1\n{ownerId}\n{question}\n" +
                       string.Join('\n', options.Select(option => $"{option.Id}\0{option.Label}"));
        return "choice-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private void ActivateFront()
    {
        IrisPrompt? front;
        lock (_gate)
        {
            front = _queue.Count == 0 ? null : _queue[0];
            if (front is null || front.ActivatedAt != DateTimeOffset.MinValue)
            {
                return;
            }

            front.ActivatedAt = DateTimeOffset.Now;
            IrisPrompt timed = front;
            _timers[front.Id] = new Timer(_ => OnTimeout(timed), null, front.Timeout, Timeout.InfiniteTimeSpan);
        }

        IrisEvent.Log("Prompts", "prompt.shown", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["owner"] = front.OwnerId,
                ["kind"] = front.Kind.ToString(),
                ["options"] = front.Options.Count.ToString(),
                ["risk"] = front.Risk.ToString()
            });
        _events.Publish(new PromptChangedEvent { Active = front });
    }

    private void OnTimeout(IrisPrompt prompt)
    {
        if (!prompt.IsResolved)
        {
            CompanionLog.Info("Prompts", $"No answer to '{prompt.OwnerName}' in {prompt.Timeout.TotalSeconds:0} s.");
            Resolve(prompt, new PromptAnswer(prompt.TimeoutOptionId ?? string.Empty, PromptSource.Timeout));
        }
    }

    private sealed class ModuleClient : IModulePrompts
    {
        private readonly PromptService _service;
        private readonly string _moduleId;
        private readonly string _moduleName;
        private readonly string _iconKey;

        public ModuleClient(PromptService service, string moduleId, string moduleName, string iconKey)
        {
            _service = service;
            _moduleId = moduleId;
            _moduleName = moduleName;
            _iconKey = iconKey;
        }

        public Task<PromptAnswer> AskAsync(PromptRequest request, CancellationToken cancellationToken) =>
            _service.AskAsync(request, _moduleId, _moduleName, _iconKey, cancellationToken);
    }
}

/// <summary>Friendly helpers so a module can ask in one line.</summary>
public static class ModulePromptExtensions
{
    /// <summary>Yes / no. Anything but a deliberate yes is false.</summary>
    public static async Task<bool> ConfirmAsync(this IModulePrompts prompts, string question,
        bool sensitive = false, CancellationToken cancellationToken = default)
    {
        PromptAnswer answer = await prompts.AskAsync(new PromptRequest
        {
            Question = question,
            Risk = sensitive ? PromptRisk.Sensitive : PromptRisk.Normal
        }, cancellationToken).ConfigureAwait(false);
        return answer.IsDeliberate && answer.OptionId == PromptIds.Yes;
    }

    /// <summary>Pick one. Returns null when the user cancels or does not answer.</summary>
    public static async Task<PromptOption?> ChooseAsync(this IModulePrompts prompts, string question,
        IReadOnlyList<PromptOption> options, CancellationToken cancellationToken = default)
    {
        PromptAnswer answer = await prompts.AskAsync(new PromptRequest { Question = question, Options = options },
            cancellationToken).ConfigureAwait(false);
        return answer.IsDeliberate
            ? options.FirstOrDefault(option => string.Equals(option.Id, answer.OptionId, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
