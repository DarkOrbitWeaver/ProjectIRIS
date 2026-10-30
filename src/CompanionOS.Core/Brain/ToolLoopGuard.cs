namespace CompanionOS.Core.Brain;

/// <summary>
/// Why one turn's tool loop ended. "Completed" is the only reason that may read as
/// success; every other reason must reach the user as an honest stop, never as an
/// implied completion.
/// </summary>
public enum ToolLoopStopReason
{
    /// <summary>The model produced a final answer; the loop ended normally.</summary>
    Completed,

    /// <summary>The budget ran out while rounds were still making progress.</summary>
    BudgetExhausted,

    /// <summary>Two consecutive rounds ran hands and not one of them succeeded.</summary>
    NoProgress,

    /// <summary>The same tool with the same arguments failed twice; a third try is refused.</summary>
    RepeatedIdenticalFailure
}

/// <summary>
/// The turn loop's progress ledger. The old contract was a flat "8 rounds then give
/// up": a chain that was genuinely getting work done was cut mid-task, while a model
/// stuck re-emitting the same broken call burned all eight rounds producing nothing
/// (2026-10 journals: the YouTube tab-count turn ended with "I'll keep checking" and
/// never did). This class replaces the flat count with three facts the loop can act on:
///
///  1. Progress earns time: a round where at least one hand succeeded extends the
///     budget, up to a hard cap. The cap is physical (context, latency), the base
///     budget is behavioral — the two are no longer the same number.
///  2. Identical failure is a wall, not a retry: the same tool with byte-identical
///     arguments failing twice means the third attempt is refused before it runs.
///     Retrying a side-effecting call blindly is how duplicate downloads happen.
///  3. No progress twice in a row stops the loop: two full rounds of hands with zero
///     successes means the plan is wrong, and the honest move is to stop and say so.
///
/// The guard never writes the user-facing sentence itself beyond DescribeStop — the
/// loop owns the words. It only owns the facts.
/// </summary>
public sealed class ToolLoopGuard
{
    /// <summary>Rounds every turn gets, matching the previous flat limit.</summary>
    public const int BaseRounds = 8;

    /// <summary>
    /// The absolute wall even for a turn that keeps succeeding. Context size and
    /// the user's patience are real; past this the turn must report and hand back.
    /// </summary>
    public const int HardCapRounds = 16;

    private readonly Dictionary<string, int> _identicalFailures = new(StringComparer.OrdinalIgnoreCase);
    private int _roundSuccesses;
    private int _roundCalls;
    private int _consecutiveNoProgressRounds;
    private int _grantedRounds = BaseRounds;
    private ToolLoopStopReason? _forcedStop;
    private string _forcedStopDetail = string.Empty;

    /// <summary>Rounds currently granted; grows with progress, never past the hard cap.</summary>
    public int GrantedRounds => _grantedRounds;

    /// <summary>Set when a rule decided the loop must stop before its budget ran out.</summary>
    public ToolLoopStopReason? ForcedStop => _forcedStop;

    /// <summary>True when the loop may begin the given zero-based round.</summary>
    public bool MayStartRound(int roundIndex) => _forcedStop is null && roundIndex < _grantedRounds;

    /// <summary>
    /// True when this exact call (name + raw arguments) already failed twice this turn.
    /// The caller must refuse the call instead of running it: a deterministic failure
    /// does not change on the third try, and a side-effecting call that failed in an
    /// unclear way must not be fired again on hope.
    /// </summary>
    public bool IsBlockedRepeat(string toolName, string? argumentsJson) =>
        _identicalFailures.TryGetValue(SignatureOf(toolName, argumentsJson), out int failures) && failures >= 2;

    /// <summary>Records one executed (or refused) call's outcome for this round.</summary>
    public void RecordCall(string toolName, string? argumentsJson, bool succeeded)
    {
        _roundCalls++;
        if (succeeded)
        {
            _roundSuccesses++;
            return;
        }

        string signature = SignatureOf(toolName, argumentsJson);
        _identicalFailures.TryGetValue(signature, out int failures);
        _identicalFailures[signature] = failures + 1;
        if (failures + 1 >= 2)
        {
            _forcedStop = ToolLoopStopReason.RepeatedIdenticalFailure;
            _forcedStopDetail = $"{toolName} failed twice with the same arguments";
        }
    }

    /// <summary>
    /// Closes one round of hands. A successful round earns one extra round of budget
    /// (bounded by the hard cap); a round of pure failure counts toward the
    /// two-strikes no-progress stop.
    /// </summary>
    public void CompleteRound()
    {
        if (_roundCalls == 0)
        {
            return; // An answer round, not a hands round; nothing to judge.
        }

        if (_roundSuccesses > 0)
        {
            _consecutiveNoProgressRounds = 0;
            _grantedRounds = Math.Min(HardCapRounds, _grantedRounds + 1);
        }
        else
        {
            _consecutiveNoProgressRounds++;
            if (_consecutiveNoProgressRounds >= 2 && _forcedStop is null)
            {
                _forcedStop = ToolLoopStopReason.NoProgress;
                _forcedStopDetail = "two full rounds of tool calls in a row produced no successful result";
            }
        }

        _roundSuccesses = 0;
        _roundCalls = 0;
    }

    /// <summary>The reason the loop ended, for the turn report and the journal.</summary>
    public ToolLoopStopReason StopReasonAfter(bool reachedFinalAnswer, int roundsRun)
    {
        if (reachedFinalAnswer)
        {
            return ToolLoopStopReason.Completed;
        }

        return _forcedStop ?? (roundsRun >= _grantedRounds
            ? ToolLoopStopReason.BudgetExhausted
            : ToolLoopStopReason.Completed);
    }

    /// <summary>
    /// The honest stop sentence: what stopped the turn and what the user can do next.
    /// Receipts (what DID succeed) are the caller's to add — they live in the ledger.
    /// </summary>
    public string DescribeStop(ToolLoopStopReason reason) => reason switch
    {
        ToolLoopStopReason.RepeatedIdenticalFailure =>
            $"I stopped because {_forcedStopDetail}; repeating it a third time would not change the result. ",
        ToolLoopStopReason.NoProgress =>
            "I stopped because " + (_forcedStopDetail.Length > 0
                ? _forcedStopDetail
                : "repeated tool rounds made no progress") + "; my current approach is not working. ",
        ToolLoopStopReason.BudgetExhausted =>
            "I ran out of working rounds for this turn before finishing. The work done so far is real and on record; " +
            "say \"continue\" and I will pick up from the last completed step instead of starting over. ",
        _ => string.Empty
    };

    private static string SignatureOf(string toolName, string? argumentsJson) =>
        toolName + "|" + (argumentsJson ?? string.Empty);
}
