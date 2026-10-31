using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Prompts;
using Xunit;

namespace CompanionOS.Core.Tests;

public sealed class PromptSpeechMatcherTests
{
    private static IrisPrompt Approval(PromptRisk risk) => new PermissionRequest(
        "plugin:browser", "Browser", "browser_click", "Browser wants to click on the page.",
        ToolTier.SystemCritical, "browser.act", alwaysConfirm: false, sessionTrust: true, risk).Prompt;

    private static IrisPrompt Numbered(params string[] labels) => new(
        PromptKind.Numbered, "Quick question", "Which one?",
        labels.Select((label, index) => new PromptOption((index + 1).ToString(), label)).ToList(),
        PromptRisk.Normal, TimeSpan.FromSeconds(60), null, true, "core", "Core", "chat");

    private static readonly string[] Songs =
    {
        "Lofi hip hop radio - beats to relax to", "Daft Punk - Get Lucky", "Queen - Bohemian Rhapsody",
        "Lofi jazz cafe mix", "Hans Zimmer - Time", "Rick Astley - Never Gonna Give You Up",
        "Nightcall - Kavinsky", "Synthwave Drive 2024"
    };

    [Fact]
    public void PermissionPromptKeepsDetailsOnScreenInsteadOfReadingThemAloud()
    {
        Assert.False(Approval(PromptRisk.Normal).SpeakAloud);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("okay")]
    [InlineData("sure")]
    [InlineData("go ahead")]
    [InlineData("yeah do it")]
    public void SensitiveActionsNeverAcceptACasualYes(string said)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Sensitive), said);
        Assert.Equal(UtteranceOutcome.NeedsExplicitApproval, match.Outcome);
        Assert.Null(match.Option);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("I approve")]
    [InlineData("yes approve it")]
    [InlineData("allow it")]
    [InlineData("confirm")]
    public void SensitiveActionsAcceptAClearPhraseAsAllowOnce(string said)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Sensitive), said);
        Assert.Equal(UtteranceOutcome.Answer, match.Outcome);
        Assert.Equal(PromptIds.AllowOnce, match.Option!.Id);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("no")]
    [InlineData("don't")]
    [InlineData("don't approve it")]
    [InlineData("no don't allow it")]
    [InlineData("approve no wait")]
    [InlineData("cancel")]
    [InlineData("never mind")]
    [InlineData("stop")]
    public void ARefusalAlwaysWinsAndIsAlwaysAccepted(string said)
    {
        foreach (PromptRisk risk in new[] { PromptRisk.Sensitive, PromptRisk.Normal })
        {
            UtteranceMatch match = PromptSpeechMatcher.Match(Approval(risk), said);
            Assert.Equal(UtteranceOutcome.Cancel, match.Outcome);
            Assert.Equal(PromptIds.Deny, match.Option!.Id);
        }
    }

    [Fact]
    public void LookOnlyActionsAcceptAPlainYes()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Normal), "yeah sure");
        Assert.Equal(UtteranceOutcome.Answer, match.Outcome);
        Assert.Equal(PromptIds.AllowOnce, match.Option!.Id);
    }

    [Theory]
    [InlineData("always allow")]
    [InlineData("approve this forever")]
    public void AlwaysCanNeverBeSpoken(string said)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Normal), said);
        Assert.NotEqual(UtteranceOutcome.Answer, match.Outcome);
    }

    [Fact]
    public void ButtonOnlyOptionsCannotBePickedByName()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Normal), "this session");
        Assert.NotEqual(UtteranceOutcome.Answer, match.Outcome);
        Assert.Contains("button", match.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChatterAroundAnApprovalIsNotAnApproval()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Approval(PromptRisk.Sensitive),
            "approve that and then also tell me what the weather is going to be like tomorrow");
        Assert.Equal(UtteranceOutcome.Ambiguous, match.Outcome);
    }

    [Theory]
    [InlineData("two", "2")]
    [InlineData("2", "2")]
    [InlineData("number 3", "3")]
    [InlineData("option three please", "3")]
    [InlineData("the second one", "2")]
    [InlineData("the last one", "8")]
    [InlineData("last", "8")]
    [InlineData("number to", "2")]
    [InlineData("to", "2")]
    [InlineData("for", "4")]
    [InlineData("#5", "5")]
    [InlineData("eight", "8")]
    public void NumbersOrdinalsAndSoundAlikesPickTheOption(string said, string expected)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), said);
        Assert.Equal(UtteranceOutcome.Answer, match.Outcome);
        Assert.Equal(expected, match.Option!.Id);
    }

    [Theory]
    [InlineData("nine")]
    [InlineData("option 12")]
    public void ANumberPastTheEndAsksAgain(string said)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), said);
        Assert.Equal(UtteranceOutcome.Ambiguous, match.Outcome);
        Assert.Contains("8", match.Hint);
    }

    [Fact]
    public void TwoDifferentNumbersAskAgain()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), "one two");
        Assert.Equal(UtteranceOutcome.Ambiguous, match.Outcome);
    }

    [Fact]
    public void ANameThatFitsOneOptionPicksIt()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), "the bohemian rhapsody one");
        Assert.Equal(UtteranceOutcome.Answer, match.Outcome);
        Assert.Equal("3", match.Option!.Id);
    }

    [Fact]
    public void ANameThatFitsTwoOptionsAsksForTheNumber()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), "the lofi one");
        Assert.Equal(UtteranceOutcome.Ambiguous, match.Outcome);
        Assert.Contains("number", match.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANumberThatContradictsANameAsksAgain()
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), "number 1 bohemian rhapsody");
        Assert.Equal(UtteranceOutcome.Ambiguous, match.Outcome);
    }

    [Fact]
    public void ShortChoiceButtonsAnswerByNameOrAlias()
    {
        IrisPrompt choice = new(PromptKind.Choice, "Quick question", "Which format?",
            new[]
            {
                new PromptOption("mp3", "MP3", Aliases: new[] { "audio only", "just the audio" }),
                new PromptOption("mp4", "MP4", Aliases: new[] { "video" }),
                new PromptOption("cancel", "Cancel", Role: PromptOptionRole.Deny)
            },
            PromptRisk.Normal, TimeSpan.FromSeconds(60), null, true, "yt", "YouTube", "youtube");

        Assert.Equal("mp3", PromptSpeechMatcher.Match(choice, "mp3").Option!.Id);
        Assert.Equal("mp3", PromptSpeechMatcher.Match(choice, "audio only please").Option!.Id);
        Assert.Equal("mp4", PromptSpeechMatcher.Match(choice, "the video").Option!.Id);
        Assert.Equal("mp4", PromptSpeechMatcher.Match(choice, "two").Option!.Id);
        UtteranceMatch cancel = PromptSpeechMatcher.Match(choice, "never mind");
        Assert.Equal(UtteranceOutcome.Cancel, cancel.Outcome);
        Assert.Equal("cancel", cancel.Option!.Id);
    }

    [Fact]
    public void LongListsPageByVoice()
    {
        IrisPrompt prompt = Numbered(Songs);
        Assert.Equal(2, prompt.PageCount);
        Assert.Equal(5, prompt.VisibleOptions.Count);

        UtteranceMatch more = PromptSpeechMatcher.Match(prompt, "more");
        Assert.Equal(UtteranceOutcome.Page, more.Outcome);
        Assert.Equal(1, more.PageDelta);

        Assert.True(prompt.Page(1));
        Assert.Equal(3, prompt.VisibleOptions.Count);
        Assert.Equal(6, prompt.FirstVisibleNumber);
        Assert.False(prompt.Page(1));
        Assert.Equal(-1, PromptSpeechMatcher.Match(prompt, "back").PageDelta);
    }

    [Theory]
    [InlineData("repeat that")]
    [InlineData("what are the options")]
    [InlineData("say that again")]
    public void RepeatReadsThePromptAgain(string said) =>
        Assert.Equal(UtteranceOutcome.Repeat, PromptSpeechMatcher.Match(Numbered(Songs), said).Outcome);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("uh")]
    [InlineData("what a lovely day")]
    public void NoiseIsNotAnAnswer(string said)
    {
        UtteranceMatch match = PromptSpeechMatcher.Match(Numbered(Songs), said);
        Assert.NotEqual(UtteranceOutcome.Answer, match.Outcome);
    }

    [Fact]
    public void SpeechReadoutIsShortAndTellsTheUserWhatToSay()
    {
        string tabs = Numbered(Songs).DescribeForSpeech();
        Assert.Contains("1, Lofi hip hop", tabs);
        Assert.Contains("5, Hans Zimmer", tabs);
        Assert.DoesNotContain("6,", tabs);
        Assert.Contains("say more", tabs, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("approve", Approval(PromptRisk.Sensitive).DescribeForSpeech(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yes", Approval(PromptRisk.Normal).DescribeForSpeech(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PromptServiceTests
{
    private static PromptRequest Which(params string[] labels) => new()
    {
        Question = "Which tab?",
        Options = labels.Select((label, index) => new PromptOption((index + 1).ToString(), label)).ToList()
    };

    private static PromptService Service(Func<PromptPolicy>? policy = null) => new(new EventBus(), policy);

    [Fact]
    public async Task AButtonAnswerCompletesTheAsk()
    {
        PromptService service = Service();
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B", "C"), "core", "Core", "chat", CancellationToken.None);
        IrisPrompt prompt = service.Active!;

        Assert.True(service.Answer(prompt.Id, "2"));
        PromptAnswer answer = await ask;

        Assert.Equal("2", answer.OptionId);
        Assert.Equal(PromptSource.Button, answer.Source);
        Assert.Null(service.Active);
    }

    [Fact]
    public async Task TheFirstAnswerWinsAndLaterOnesAreIgnored()
    {
        PromptService service = Service();
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B"), "core", "Core", "chat", CancellationToken.None);
        IrisPrompt prompt = service.Active!;

        Assert.True(service.Answer(prompt.Id, "1"));
        Assert.False(service.Answer(prompt.Id, "2"));
        Assert.Equal("1", (await ask).OptionId);
    }

    [Fact]
    public async Task TypedAndSpokenAnswersResolveThroughTheSameDoor()
    {
        PromptService service = Service(() => new PromptPolicy { MinimumVoiceDelay = TimeSpan.Zero });
        Task<PromptAnswer> typed = service.AskAsync(Which("A", "B", "C"), "core", "Core", "chat", CancellationToken.None);
        Assert.True(service.HandleUtterance("three", PromptSource.Typed).Answered);
        Assert.Equal("3", (await typed).OptionId);

        Task<PromptAnswer> spoken = service.AskAsync(Which("A", "B", "C"), "core", "Core", "chat", CancellationToken.None);
        Assert.True(service.HandleUtterance("the second one", PromptSource.Voice).Answered);
        PromptAnswer answer = await spoken;
        Assert.Equal("2", answer.OptionId);
        Assert.Equal(PromptSource.Voice, answer.Source);
    }

    [Fact]
    public async Task SpeechRightAfterThePromptAppearsIsTreatedAsHerOwnVoice()
    {
        PromptService service = Service(() => new PromptPolicy { MinimumVoiceDelay = TimeSpan.FromSeconds(30) });
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B"), "core", "Core", "chat", CancellationToken.None);

        PromptUtteranceResult echo = service.HandleUtterance("one", PromptSource.Voice);
        Assert.True(echo.Handled);
        Assert.False(echo.Answered);
        Assert.False(ask.IsCompleted);

        Assert.True(service.HandleUtterance("one", PromptSource.Typed).Answered);
        await ask;
    }

    [Fact]
    public async Task VoiceAnswersCanBeSwitchedOffWhileTypingStillWorks()
    {
        PromptService service = Service(() => new PromptPolicy { VoiceAnswers = false, MinimumVoiceDelay = TimeSpan.Zero });
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B"), "core", "Core", "chat", CancellationToken.None);

        PromptUtteranceResult spoken = service.HandleUtterance("one", PromptSource.Voice);
        Assert.True(spoken.Handled);
        Assert.False(spoken.Answered);
        Assert.False(ask.IsCompleted);

        Assert.True(service.HandleUtterance("one", PromptSource.Typed).Answered);
        await ask;
    }

    [Fact]
    public async Task UnrelatedSpeechIsConsumedSoItCannotQueueBehindTheWaitingTurn()
    {
        PromptService service = Service(() => new PromptPolicy { MinimumVoiceDelay = TimeSpan.Zero });
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B"), "core", "Core", "chat", CancellationToken.None);

        PromptUtteranceResult result = service.HandleUtterance("what is the weather like today", PromptSource.Voice);

        Assert.True(result.Handled);
        Assert.False(result.Answered);
        Assert.False(string.IsNullOrWhiteSpace(service.Active!.Hint));
        service.CancelActive();
        Assert.Equal(PromptSource.Cancelled, (await ask).Source);
    }

    [Fact]
    public void WithNoQuestionOpenSpeechIsNotTouched()
    {
        PromptUtteranceResult result = Service().HandleUtterance("play some music", PromptSource.Voice);
        Assert.False(result.Handled);
    }

    [Fact]
    public async Task TimeoutPicksTheDenyOptionAndSaysItWasATimeout()
    {
        PromptService service = Service();
        PromptAnswer answer = await service.AskAsync(new PromptRequest
        {
            Question = "Delete it?",
            Timeout = TimeSpan.FromMilliseconds(150)
        }, "core", "Core", "chat", CancellationToken.None);

        Assert.True(answer.TimedOut);
        Assert.False(answer.IsDeliberate);
        Assert.Equal(PromptIds.No, answer.OptionId);
    }

    [Fact]
    public async Task QueuedPromptsWaitTheirTurnAndTheirClockStartsWhenShown()
    {
        PromptService service = Service();
        Task<PromptAnswer> first = service.AskAsync(
            new PromptRequest { Question = "First?", Timeout = TimeSpan.FromSeconds(30) },
            "core", "Core", "chat", CancellationToken.None);
        Task<PromptAnswer> second = service.AskAsync(
            new PromptRequest { Question = "Second?", Timeout = TimeSpan.FromMilliseconds(400) },
            "core", "Core", "chat", CancellationToken.None);

        Assert.Equal("First?", service.Active!.Question);
        await Task.Delay(600);
        Assert.False(second.IsCompleted);

        service.Answer(service.Active.Id, PromptIds.Yes);
        Assert.Equal(PromptIds.Yes, (await first).OptionId);
        Assert.Equal("Second?", service.Active!.Question);
        Assert.True((await second).TimedOut);
    }

    [Fact]
    public async Task CancellingTheTokenDismissesThePrompt()
    {
        PromptService service = Service();
        using CancellationTokenSource cancel = new();
        Task<PromptAnswer> ask = service.AskAsync(Which("A", "B"), "core", "Core", "chat", cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        Assert.Null(service.Active);
    }

    [Fact]
    public async Task ModulesAskThroughAScopedClientAndOnlyGetADeliberateAnswer()
    {
        PromptService service = Service(() => new PromptPolicy { MinimumVoiceDelay = TimeSpan.Zero });
        IModulePrompts module = service.ForModule("plugin:youtube", "YouTube", "youtube");
        Task<PromptOption?> choice = module.ChooseAsync("Which one?", new[]
        {
            new PromptOption("a", "Song A"), new PromptOption("b", "Song B")
        });

        Assert.Equal("YouTube", service.Active!.OwnerName);
        service.HandleUtterance("two", PromptSource.Typed);
        Assert.Equal("b", (await choice)!.Id);

        Task<bool> confirm = module.ConfirmAsync("Download it?");
        service.CancelActive();
        Assert.False(await confirm);
    }
}

public sealed class SandboxSessionTrustTests
{
    private static (SandboxGuard Guard, EventBus Events, string Folder) Make()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        EventBus events = new();
        SandboxGuard guard = new(events, new FileAuditTrail(Path.Combine(folder, "audit.jsonl")),
            new PermissionGrantStore(Path.Combine(folder, "permissions.json")));
        return (guard, events, folder);
    }

    [Fact]
    public async Task SessionTrustAsksOnceThenStaysQuietUntilCleared()
    {
        (SandboxGuard guard, EventBus events, string folder) = Make();
        try
        {
            int asked = 0;
            using IDisposable subscription = events.Subscribe<PermissionRequestedEvent>(payload =>
            {
                asked++;
                Assert.True(payload.Request.CanTrustSession);
                payload.Request.Answer(PermissionDecision.AllowSession);
            });
            ModuleTool click = new("browser_click", "Click.", ToolTier.SystemCritical, "{}")
            {
                PermissionScope = "browser.act",
                SessionTrust = true
            };

            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", click, "Browser wants to click.",
                CancellationToken.None)).IsAllowed);
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", click, "Browser wants to click again.",
                CancellationToken.None)).IsAllowed);
            Assert.Equal(1, asked);
            Assert.Equal(1, guard.SessionGrantCount);
            Assert.False(guard.Grants.IsScopeGranted("plugin:browser", "browser.act"));

            guard.ClearSessionGrants();
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", click, "Browser wants to click once more.",
                CancellationToken.None)).IsAllowed);
            Assert.Equal(2, asked);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task EveryTimeToolsNeverGetASessionGrantEvenIfOneIsPressed()
    {
        (SandboxGuard guard, EventBus events, string folder) = Make();
        try
        {
            int asked = 0;
            using IDisposable subscription = events.Subscribe<PermissionRequestedEvent>(payload =>
            {
                asked++;
                Assert.False(payload.Request.CanTrustSession);
                Assert.False(payload.Request.CanAlwaysAllow);
                Assert.DoesNotContain(payload.Request.Prompt.Options,
                    option => option.Id == PromptIds.AllowTurn);
                payload.Request.Answer(PermissionDecision.AllowSession);
            });
            ModuleTool type = new("browser_type", "Type.", ToolTier.SystemCritical, "{}")
            {
                PermissionScope = "browser.act",
                AlwaysConfirm = true,
                SessionTrust = true
            };

            await guard.AuthorizeAsync("plugin:browser", "Browser", type, "Browser wants to type.", CancellationToken.None);
            await guard.AuthorizeAsync("plugin:browser", "Browser", type, "Browser wants to type again.", CancellationToken.None);

            Assert.Equal(2, asked);
            Assert.Equal(0, guard.SessionGrantCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ApprovalsCarryTheRiskThatDecidesWhetherAPlainYesIsEnough()
    {
        (SandboxGuard guard, EventBus events, string folder) = Make();
        try
        {
            List<PromptRisk> risks = new();
            using IDisposable subscription = events.Subscribe<PermissionRequestedEvent>(payload =>
            {
                risks.Add(payload.Request.Prompt.Risk);
                payload.Request.Answer(PermissionDecision.AllowOnce);
            });

            ModuleTool read = new("browser_read_page", "Read.", ToolTier.SystemCritical, "{}") { PermissionScope = "browser.read" };
            ModuleTool act = new("browser_click", "Click.", ToolTier.SystemCritical, "{}") { PermissionScope = "browser.act" };
            await guard.AuthorizeAsync("plugin:browser", "Browser", read, "Browser wants to read the page.", CancellationToken.None);
            await guard.AuthorizeAsync("plugin:browser", "Browser", act, "Browser wants to click.", CancellationToken.None);

            Assert.Equal(new[] { PromptRisk.Normal, PromptRisk.Sensitive }, risks);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class SandboxTurnGrantTests
{
    [Fact]
    public async Task TurnGrantIsLimitedToItsTurnAndCanBeCleared()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"iris-turn-grant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        EventBus events = new();
        SandboxGuard guard = new(events, new FileAuditTrail(Path.Combine(folder, "audit.jsonl")),
            new PermissionGrantStore(Path.Combine(folder, "permissions.json")));
        int asked = 0;
        using IDisposable subscription = events.Subscribe<PermissionRequestedEvent>(payload =>
        {
            asked++;
            Assert.Contains(payload.Request.Prompt.Options,
                option => option.Id == PromptIds.AllowTurn && !option.VoiceAllowed);
            payload.Request.Answer(PermissionDecision.AllowTurn);
        });
        ModuleTool tool = new("browser_read_page", "Read the current page.", ToolTier.SystemCritical, "{}");

        try
        {
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", tool,
                "Read the page.", "turn-a", CancellationToken.None)).IsAllowed);
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", tool,
                "Read the page again.", "turn-a", CancellationToken.None)).IsAllowed);
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", tool,
                "Read the page.", "turn-b", CancellationToken.None)).IsAllowed);
            Assert.Equal(2, asked);

            guard.ClearTurnGrants("turn-a");
            Assert.True((await guard.AuthorizeAsync("plugin:browser", "Browser", tool,
                "Read the page once more.", "turn-a", CancellationToken.None)).IsAllowed);
            Assert.Equal(3, asked);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class RememberedPromptChoiceTests
{
    [Fact]
    public async Task OnlyAnExplicitlyRememberedChoiceIsReusedForTheSamePrompt()
    {
        EventBus events = new();
        PromptService service = new(events);
        PromptRequest request = new()
        {
            Question = "Which format should I use?",
            Options = new[]
            {
                new PromptOption("mp3", "MP3"),
                new PromptOption("wav", "WAV")
            },
            AllowRemembering = true
        };
        IrisPrompt first = service.Build(request, "core", "Core", "chat");
        first.SetRememberChoice(true);

        Task<PromptAnswer> pending = service.AskAsync(first, CancellationToken.None);
        Assert.True(service.Answer(first.Id, "wav"));
        Assert.Equal("wav", (await pending).OptionId);
        Assert.Equal(1, service.RememberedChoiceCount);

        IrisPrompt repeated = service.Build(request, "core", "Core", "chat");
        PromptAnswer remembered = await service.AskAsync(repeated, CancellationToken.None);
        Assert.Equal("wav", remembered.OptionId);
        Assert.Equal(PromptSource.Remembered, remembered.Source);

        IrisPrompt otherOwner = service.Build(request, "plugin:other", "Other", "body");
        Assert.False(otherOwner.IsResolved);
        service.ClearRememberedChoices();
        Assert.Equal(0, service.RememberedChoiceCount);
    }
}

public sealed class ToolPresentationTests
{
    private static ModuleTool Tool(string name) => new(name, "A very long model-facing description. Second sentence.",
        ToolTier.Reflex, "{}");

    private static System.Text.Json.JsonElement Args(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ToolsReadInPlainWordsNeverAsTheirModelDescription()
    {
        ModuleTool media = Tool("control_windows_media");
        Assert.Equal("Resuming your media", ToolPresentationCatalog.Resolve(media, Args("""{"action":"play"}""")).Doing);
        Assert.Equal("Resumed your media", ToolPresentationCatalog.Resolve(media, Args("""{"action":"play"}""")).Done);
        Assert.Equal("Paused your media", ToolPresentationCatalog.Resolve(media, Args("""{"action":"pause"}""")).Done);
        Assert.DoesNotContain("Windows'", media.ShortLabel);
    }

    [Fact]
    public void LabelsUseTheArgumentsTheModelSent()
    {
        Assert.Equal("Set a 10 min timer",
            ToolPresentationCatalog.Resolve(Tool("set_timer"), Args("""{"minutes":10}""")).Done);
        Assert.Equal("Opened calculator",
            ToolPresentationCatalog.Resolve(Tool("open_application"), Args("""{"name":"calculator"}""")).Done);
        Assert.Equal("Started Night Drive",
            ToolPresentationCatalog.Resolve(Tool("music_player"), Args("""{"action":"play","query":"Night Drive"}""")).Done);
    }

    [Fact]
    public void LongArgumentsAreShortenedSoACardNeverBreaks()
    {
        string longQuery = new('x', 200);
        string label = ToolPresentationCatalog.Resolve(Tool("find_files"), Args($"{{\"query\":\"{longQuery}\"}}")).Doing;
        Assert.True(label.Length < 80);
    }

    [Fact]
    public void QualifiedPluginToolNamesAreHumanizedAndPluginsCanOverrideTheWords()
    {
        ModuleTool qualified = new("plugin_clock_now_time_0123456789", "Returns the time.", ToolTier.SystemCritical, "{}")
        {
            LocalName = "now_time"
        };
        Assert.Equal("Finished now time", ToolPresentationCatalog.Resolve(qualified, default).Done);

        ModuleTool labelled = new("plugin_clock_now_time_0123456789", "Returns the time.", ToolTier.SystemCritical, "{}")
        {
            LocalName = "now_time",
            Presentation = new ToolPresentation("Checking the time", "Checked the time", "Couldn't check", "timer")
        };
        Assert.Equal("Checked the time", ToolPresentationCatalog.Resolve(labelled, default).Done);
    }

    [Fact]
    public void AccessIsReadOnlyWhenTheScopeSaysReadOrTheToolIsReflex()
    {
        ModuleTool fileSearch = new CompanionOS.Core.Modules.BuiltIn.SystemControlModule().Tools
            .Single(tool => tool.Name == "find_files");
        Assert.Equal(ToolAccess.Reads, ToolPresentationCatalog.AccessOf(fileSearch));

        Assert.Equal(ToolAccess.Reads, ToolPresentationCatalog.AccessOf(
            new ModuleTool("a", "d", ToolTier.SystemCritical, "{}") { PermissionScope = "browser.read" }));
        Assert.Equal(ToolAccess.Acts, ToolPresentationCatalog.AccessOf(
            new ModuleTool("a", "d", ToolTier.SystemCritical, "{}") { PermissionScope = "browser.act" }));
        Assert.Equal(ToolAccess.Reads, ToolPresentationCatalog.AccessOf(Tool("recall_memory")));
        // A plugin's own declaration beats the guess from the scope name.
        Assert.Equal(ToolAccess.Acts, ToolPresentationCatalog.AccessOf(
            new ModuleTool("a", "d", ToolTier.SystemCritical, "{}") { Access = ToolAccess.Acts, PermissionScope = "x.read" }));
    }
}
