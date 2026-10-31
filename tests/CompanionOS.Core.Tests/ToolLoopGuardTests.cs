using CompanionOS.Core.Brain;
using Xunit;

namespace CompanionOS.Core.Tests;

/// <summary>
/// The turn loop's progress rules. The failures these pin down: a turn that was
/// genuinely progressing got cut at a flat 8 rounds, a model stuck on one broken
/// call burned the whole budget, and the stop message never said why it stopped.
/// </summary>
public sealed class ToolLoopGuardTests
{
    [Fact]
    public void BaseBudgetMatchesTheOldFlatLimit()
    {
        ToolLoopGuard guard = new();
        Assert.True(guard.MayStartRound(0));
        Assert.True(guard.MayStartRound(ToolLoopGuard.BaseRounds - 1));
        Assert.False(guard.MayStartRound(ToolLoopGuard.BaseRounds));
    }

    [Fact]
    public void CorrectableFailuresAllowExactlyOneRecoveryAttempt()
    {
        ToolLoopGuard guard = new();

        Assert.True(guard.TryBeginRecoveryAttempt());
        Assert.False(guard.TryBeginRecoveryAttempt());
    }

    [Fact]
    public void ProgressEarnsExtraRoundsUpToTheHardCap()
    {
        ToolLoopGuard guard = new();
        for (int round = 0; round < 20; round++)
        {
            guard.RecordCall("list_files", "{}", succeeded: true);
            guard.CompleteRound();
        }

        Assert.Equal(ToolLoopGuard.HardCapRounds, guard.GrantedRounds);
        Assert.True(guard.MayStartRound(ToolLoopGuard.HardCapRounds - 1));
        Assert.False(guard.MayStartRound(ToolLoopGuard.HardCapRounds));
    }

    [Fact]
    public void TheSameFailingCallIsRefusedATthirdTime()
    {
        ToolLoopGuard guard = new();
        guard.RecordCall("download", "{\"url\":\"x\"}", succeeded: false);
        Assert.False(guard.IsBlockedRepeat("download", "{\"url\":\"x\"}"));
        guard.RecordCall("download", "{\"url\":\"x\"}", succeeded: false);

        Assert.True(guard.IsBlockedRepeat("download", "{\"url\":\"x\"}"));
        // Different arguments are a different attempt — those stay allowed.
        Assert.False(guard.IsBlockedRepeat("download", "{\"url\":\"y\"}"));
        Assert.Equal(ToolLoopStopReason.RepeatedIdenticalFailure, guard.ForcedStop);
    }

    [Fact]
    public void TwoAllFailureRoundsStopTheLoopAsNoProgress()
    {
        ToolLoopGuard guard = new();
        guard.RecordCall("a", "{}", succeeded: false);
        guard.RecordCall("b", "{}", succeeded: false);
        guard.CompleteRound();
        Assert.Null(guard.ForcedStop);

        guard.RecordCall("c", "{}", succeeded: false);
        guard.CompleteRound();

        Assert.Equal(ToolLoopStopReason.NoProgress, guard.ForcedStop);
        Assert.False(guard.MayStartRound(2));
    }

    [Fact]
    public void ASuccessfulRoundResetsTheNoProgressStrikes()
    {
        ToolLoopGuard guard = new();
        guard.RecordCall("a", "{}", succeeded: false);
        guard.CompleteRound();
        guard.RecordCall("a", "{\"k\":1}", succeeded: true);
        guard.CompleteRound();
        guard.RecordCall("b", "{}", succeeded: false);
        guard.CompleteRound();

        Assert.Null(guard.ForcedStop); // One strike again, not two.
    }

    [Fact]
    public void AnswerOnlyRoundsAreNeverJudgedAsNoProgress()
    {
        ToolLoopGuard guard = new();
        guard.CompleteRound();
        guard.CompleteRound();
        guard.CompleteRound();
        Assert.Null(guard.ForcedStop);
    }

    [Fact]
    public void EveryStopReasonProducesAnHonestSentenceWithANextStep()
    {
        ToolLoopGuard guard = new();
        Assert.Contains("ran out of working rounds", guard.DescribeStop(ToolLoopStopReason.BudgetExhausted));
        Assert.Contains("continue", guard.DescribeStop(ToolLoopStopReason.BudgetExhausted));
        Assert.Contains("not working", guard.DescribeStop(ToolLoopStopReason.NoProgress));
        Assert.Equal(string.Empty, guard.DescribeStop(ToolLoopStopReason.Completed));
    }

    [Fact]
    public void StopReasonReportsBudgetExhaustionOnlyWhenTheBudgetWasActuallySpent()
    {
        ToolLoopGuard guard = new();
        Assert.Equal(ToolLoopStopReason.Completed, guard.StopReasonAfter(reachedFinalAnswer: true, roundsRun: 3));
        Assert.Equal(ToolLoopStopReason.BudgetExhausted,
            guard.StopReasonAfter(reachedFinalAnswer: false, roundsRun: ToolLoopGuard.BaseRounds));
    }
}
