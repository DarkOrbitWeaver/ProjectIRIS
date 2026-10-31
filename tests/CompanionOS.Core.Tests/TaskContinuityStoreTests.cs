using CompanionOS.Core.Recovery;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The journaled 30 October Telegram failure, pinned as a regression:
/// Iris asked for a nickname and username, the user supplied only "@wwh006", she
/// claimed a save with no tool behind it, asked for the message, received
/// "hey this is Iris. testing.", answered "Hi Boss", then claimed a send that never
/// happened and abandoned the task when challenged.
///
/// These tests pin the durable half of the fix: the task survives short replies,
/// keeps the user's supplied values reachable, records only honest receipts, never
/// marks a no-tool turn as completed, and survives a restart.
/// </summary>
public sealed class TaskContinuityStoreTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _journalPath;

    public TaskContinuityStoreTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "iris-task-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_workspace);
        _journalPath = Path.Combine(_workspace, "journal.jsonl");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public void AShortReplyContinuesTheOpenTaskInsteadOfStartingANewConversation()
    {
        TaskContinuityStore store = new(_workspace);
        TurnJournal journal = new(_journalPath);

        journal.RecordUserMessage("t1", "send a telegram message to my friend");
        PendingTaskRecord? task = store.BeginTurn("t1", "send a telegram message to my friend", true);
        Assert.NotNull(task);
        store.FinishTurn(task!.Id, "t1", "What nickname should I save, and what is their Telegram username?", true);

        // The bare username is the answer to the open question, not a new topic.
        journal.RecordUserMessage("t2", "@wwh006");
        PendingTaskRecord? continued = store.BeginTurn("t2", "@wwh006", false);

        Assert.NotNull(continued);
        Assert.Equal(task.Id, continued!.Id);
        Assert.Contains("t1", continued.TurnIds);
        Assert.Contains("t2", continued.TurnIds);
    }

    [Fact]
    public void TheSuppliedUsernameAndMessageStayRecoverableInTheTaskContext()
    {
        TaskContinuityStore store = new(_workspace);
        TurnJournal journal = new(_journalPath);

        journal.RecordUserMessage("t1", "send a telegram message to my friend");
        PendingTaskRecord task = store.BeginTurn("t1", "send a telegram message to my friend", true)!;
        store.FinishTurn(task.Id, "t1", "What nickname and username?", true);

        journal.RecordUserMessage("t2", "@wwh006");
        store.BeginTurn("t2", "@wwh006", false);
        store.FinishTurn(task.Id, "t2", "What message should I send?", false);

        journal.RecordUserMessage("t3", "hey this is Iris. testing.");
        store.BeginTurn("t3", "hey this is Iris. testing.", false);

        string context = store.BuildContext(task.Id, journal);

        // Both supplied values survive into the next turn; "Hi Boss" happened because
        // neither was reachable once the window moved on.
        Assert.Contains("@wwh006", context, StringComparison.Ordinal);
        Assert.Contains("hey this is Iris. testing.", context, StringComparison.Ordinal);
        Assert.Contains("do not claim a step happened without", context, StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnThatRanNoToolIsNeverRecordedAsCompleted()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "save @wwh006 as a contact", true)!;

        // Exactly the logged failure: she said it was saved, and no tool ran.
        store.FinishTurn(task.Id, "t1", "Saved that contact for you.", true);

        PendingTaskRecord stored = store.Find(task.Id)!;
        Assert.NotEqual(PendingTaskState.Completed, stored.State);
        Assert.Equal(PendingTaskState.Partial, stored.State);
        Assert.Contains("No tool completed", stored.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public void ASucceededReceiptClosesTheTaskAndAQuestionKeepsItOpen()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord open = store.BeginTurn("t1", "add that song to my playlist", true)!;
        store.FinishTurn(open.Id, "t1", "Which playlist did you mean, Night Drive or Boss's Playlist?", true);
        Assert.Equal(PendingTaskState.AwaitingUser, store.Find(open.Id)!.State);

        store.BeginTurn("t2", "boss's playlist", false);
        store.RecordReceipt(open.Id, "t2", "music_playlists", "{\"action\":\"add_file\"}", true,
            PendingTaskReceiptState.Succeeded);
        store.FinishTurn(open.Id, "t2", "Added and verified.", true);

        Assert.Equal(PendingTaskState.Completed, store.Find(open.Id)!.State);
    }

    [Fact]
    public void AnIdenticalSideEffectIsNotAttemptedTwiceInTheSameTask()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "send the telegram message", true)!;
        const string arguments = "{\"nickname\":\"loser22\",\"message\":\"hey this is Iris. testing.\"}";

        Assert.False(store.WasSideEffectAlreadyAttempted(task.Id, "telegram_send_message", arguments));
        store.RecordReceipt(task.Id, "t1", "telegram_send_message", arguments, true,
            PendingTaskReceiptState.Succeeded);

        Assert.True(store.WasSideEffectAlreadyAttempted(task.Id, "telegram_send_message", arguments));
        // Key order must not create a false "new" call.
        Assert.True(store.WasSideEffectAlreadyAttempted(task.Id, "telegram_send_message",
            "{\"message\":\"hey this is Iris. testing.\",\"nickname\":\"loser22\"}"));
        // A genuinely different message is a different action and stays allowed.
        Assert.False(store.WasSideEffectAlreadyAttempted(task.Id, "telegram_send_message",
            "{\"nickname\":\"loser22\",\"message\":\"different text\"}"));
    }

    [Fact]
    public void AnIdenticalFailedReadOnlyCallIsRememberedWithoutPersistingItsArguments()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "find the downloaded song", true)!;
        const string arguments = "{\"query\":\"private song title\"}";

        Assert.False(store.WasReadOnlyFailureAlreadyAttempted(task.Id, "music_find_download", arguments));
        store.RecordReceipt(task.Id, "t1", "music_find_download", arguments, sideEffecting: false,
            PendingTaskReceiptState.Failed);

        Assert.True(store.WasReadOnlyFailureAlreadyAttempted(task.Id, "music_find_download", arguments));
        Assert.True(store.WasReadOnlyFailureAlreadyAttempted(task.Id, "music_find_download",
            "{ \"query\" : \"private song title\" }"));
        Assert.False(store.WasReadOnlyFailureAlreadyAttempted(task.Id, "music_find_download",
            "{\"query\":\"different title\"}"));
        Assert.False(store.WasReadOnlyFailureAlreadyAttempted(task.Id, "music_library", arguments));
        Assert.DoesNotContain("private song title", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncertainOutcomeBlocksTheBlindRetryAndIsCarriedAsUncertain()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "download that video", true)!;
        store.RecordReceipt(task.Id, "t1", "youtube_download", "{\"url\":\"x\"}", true,
            PendingTaskReceiptState.Uncertain);

        PendingTaskRecord stored = store.Find(task.Id)!;
        Assert.Equal(PendingTaskState.Uncertain, stored.State);
        Assert.True(store.WasSideEffectAlreadyAttempted(task.Id, "youtube_download", "{\"url\":\"x\"}"));
    }

    [Fact]
    public void PermissionOutcomesAreKeptDistinctFromPlainFailure()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord denied = store.BeginTurn("t1", "send the telegram message", true)!;
        store.RecordReceipt(denied.Id, "t1", "telegram_send_message", "{}", true,
            PendingTaskReceiptState.PermissionTimedOut);

        PendingTaskRecord stored = store.Find(denied.Id)!;
        Assert.Equal(PendingTaskState.Partial, stored.State);
        Assert.Contains("timed out", stored.Blocker, StringComparison.OrdinalIgnoreCase);
        // A prompt nobody answered did not run the action, so retrying is still allowed.
        Assert.False(store.WasSideEffectAlreadyAttempted(denied.Id, "telegram_send_message", "{}"));
    }

    [Fact]
    public void MissingRequiredFieldsAreRecordedSoTheNextTurnAsksForTheRightOne()
    {
        const string schema = """{"type":"object","properties":{"nickname":{"type":"string"},"username":{"type":"string"}},"required":["nickname","username"]}""";
        IReadOnlyList<string> missing = TaskContinuityStore.FindMissingRequiredFields(schema, "{\"username\":\"wwh006\"}");

        Assert.Single(missing);
        Assert.Equal("nickname", missing[0]);
    }

    [Fact]
    public void AnUnfinishedTaskSurvivesARestartAndIsOfferedForRecovery()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "send a telegram message to loser22", true)!;
        store.RecordReceipt(task.Id, "t1", "telegram_add_contact", "{\"nickname\":\"loser22\"}", true,
            PendingTaskReceiptState.Succeeded);
        store.FinishTurn(task.Id, "t1", "What message should I send?", true);

        TaskContinuityStore reopened = new(_workspace);
        PendingTaskRecord? recovered = reopened.LatestOpen;

        Assert.NotNull(recovered);
        Assert.Equal(task.Id, recovered!.Id);
        Assert.Equal(PendingTaskState.AwaitingUser, recovered.State);
        Assert.Contains(recovered.Receipts, receipt =>
            receipt.ToolName == "telegram_add_contact" && receipt.State == PendingTaskReceiptState.Succeeded);
    }

    [Fact]
    public void CancellationBeforeAnySideEffectIsTerminalAndNotLeftRunning()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "download that album", true)!;
        store.MarkTurnCancelled("t1");

        Assert.Equal(PendingTaskState.Cancelled, store.Find(task.Id)!.State);
        Assert.Null(store.LatestOpen);
    }

    [Fact]
    public void CancellationAfterAPossibleSideEffectIsUncertainRatherThanCancelled()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "send the telegram message", true)!;
        store.RecordReceipt(task.Id, "t1", "telegram_send_message", "{}", true,
            PendingTaskReceiptState.BackgroundStarted);
        store.MarkTurnCancelled("t1");

        Assert.Equal(PendingTaskState.Uncertain, store.Find(task.Id)!.State);
    }

    [Fact]
    public void BackgroundWorkReachesATerminalStateInTheTaskLedger()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "download that video", true)!;
        store.RecordReceipt(task.Id, "t1", "youtube_download", "{\"url\":\"x\"}", true,
            PendingTaskReceiptState.BackgroundStarted, backgroundTaskId: "task-abc");

        store.RecordBackgroundState("task-abc", "Completed");

        PendingTaskRecord stored = store.Find(task.Id)!;
        Assert.Contains(stored.Receipts, receipt => receipt.State == PendingTaskReceiptState.BackgroundCompleted);
        Assert.Equal(string.Empty, stored.Uncertainty);
    }

    [Fact]
    public void TheTaskFileStoresReceiptsAndTurnReferencesButNeverRawToolPayloads()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "send the telegram message", true)!;
        store.RecordReceipt(task.Id, "t1", "telegram_send_message",
            "{\"nickname\":\"loser22\",\"message\":\"secret body text\"}", true, PendingTaskReceiptState.Succeeded);

        string onDisk = File.ReadAllText(store.FilePath);

        // Arguments are fingerprinted, never copied: the duplicate check still works
        // without the message body living in a second durable file.
        Assert.DoesNotContain("secret body text", onDisk, StringComparison.Ordinal);
        Assert.Contains("telegram_send_message", onDisk, StringComparison.Ordinal);
        Assert.Contains("Succeeded", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndependentCommandStartsANewTaskInsteadOfHijackingTheOldOne()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord first = store.BeginTurn("t1", "send a telegram message to my friend", true)!;
        store.FinishTurn(first.Id, "t1", "What message should I send?", true);

        PendingTaskRecord? second = store.BeginTurn("t2", "play some music", true);

        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second!.Id);
    }

    [Theory]
    [InlineData("heyyyy")]
    [InlineData("hiii")]
    [InlineData("yo")]
    [InlineData("hello Boss")]
    public void GreetingsDoNotBindToAnOpenQuestion(string greeting)
    {
        PendingTaskRecord task = new()
        {
            State = PendingTaskState.AwaitingUser,
            LastQuestion = "Which folder should I search?"
        };

        Assert.False(TaskContinuityStore.IsLikelyFollowUp(greeting, task));
    }

    [Fact]
    public void ArbitraryReplyDoesNotBindToAQuestionOlderThanTenMinutes()
    {
        PendingTaskRecord task = new()
        {
            State = PendingTaskState.AwaitingUser,
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-11)
        };

        Assert.False(TaskContinuityStore.IsLikelyFollowUp("loser22", task));
        Assert.True(TaskContinuityStore.IsLikelyFollowUp("did that finish?", task));
        Assert.True(TaskContinuityStore.IsLikelyFollowUp("do it", task));
    }

    [Fact]
    public void AStatusQuestionReadsTheTaskWithoutResettingIt()
    {
        TaskContinuityStore store = new(_workspace);
        PendingTaskRecord task = store.BeginTurn("t1", "download that video", true)!;
        store.RecordReceipt(task.Id, "t1", "youtube_download", "{\"url\":\"x\"}", true,
            PendingTaskReceiptState.BackgroundStarted, backgroundTaskId: "task-1");
        store.FinishTurn(task.Id, "t1", "Started the download in the background.", true);

        PendingTaskRecord? asked = store.BeginTurn("t2", "did that finish?", false);

        Assert.NotNull(asked);
        Assert.Equal(task.Id, asked!.Id);
        Assert.Equal(PendingTaskState.InProgress, asked.State);
    }

    [Fact]
    public void AMissingJournalIsReportedHonestlyInsteadOfPretendingTheChatWasRestored()
    {
        TaskContinuityStore store = new(_workspace);
        TurnJournal emptyJournal = new(Path.Combine(_workspace, "missing.jsonl"));
        PendingTaskRecord task = store.BeginTurn("t1", "send a telegram message", true)!;

        string context = store.BuildContext(task.Id, emptyJournal);

        Assert.Contains("no longer available", context, StringComparison.Ordinal);
        Assert.Contains("do not pretend", context, StringComparison.Ordinal);
    }
}
