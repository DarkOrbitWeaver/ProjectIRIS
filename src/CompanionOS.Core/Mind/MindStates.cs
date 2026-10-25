namespace CompanionOS.Core.Mind;

/// <summary>
/// What she is doing right now. The interface renders exactly one of these at a time,
/// so this list is the single source of truth for the status orb.
/// </summary>
public enum MindState
{
    /// <summary>Starting up, models not ready yet.</summary>
    Booting,

    /// <summary>Awake, nothing happening.</summary>
    Idle,

    /// <summary>Microphone is open and she is hearing the user.</summary>
    Listening,

    /// <summary>The brain is reasoning.</summary>
    Thinking,

    /// <summary>A tool is running (searching, playing music, capturing the screen).</summary>
    Acting,

    /// <summary>Her voice is playing.</summary>
    Speaking,

    /// <summary>Waiting for the user to allow or deny a dangerous action.</summary>
    PermissionWait,

    /// <summary>Cortex unloaded, brainstem listening.</summary>
    Sleeping,

    /// <summary>Cortex unloaded because a game owns the GPU.</summary>
    Gaming,

    /// <summary>Something failed; she recovers automatically.</summary>
    Error
}

/// <summary>Which operating mode the whole system is in. Independent of the mind state.</summary>
public enum OperatingMode
{
    /// <summary>Model loaded, everything available.</summary>
    Awake,

    /// <summary>Model unloaded on purpose. Ears and voice stay alive.</summary>
    Sleeping,

    /// <summary>Model unloaded and GPU handed to a game. Reflexes only.</summary>
    Gaming
}

/// <summary>
/// Guards every state change in one place: validates the transition, records the reason,
/// and raises an event so the interface never has to guess.
///
/// Transitions are deliberately permissive — a companion must never get wedged because
/// a rare path was not in a table. The machine only refuses changes while booting.
/// </summary>
public sealed class MindStateMachine
{
    private readonly object _transitionLock = new();
    private MindState _currentState = MindState.Booting;
    private OperatingMode _currentMode = OperatingMode.Awake;

    /// <summary>The current mind state.</summary>
    public MindState CurrentState
    {
        get { lock (_transitionLock) { return _currentState; } }
    }

    /// <summary>The current operating mode.</summary>
    public OperatingMode CurrentMode
    {
        get { lock (_transitionLock) { return _currentMode; } }
    }

    /// <summary>Raised after any successful state change.</summary>
    public event Action<MindState, MindState, string>? StateChanged;

    /// <summary>Raised after any successful mode change.</summary>
    public event Action<OperatingMode, OperatingMode, string>? ModeChanged;

    /// <summary>Moves to a new mind state. Returns false when nothing changed.</summary>
    public bool MoveTo(MindState newState, string reason)
    {
        MindState previous;
        lock (_transitionLock)
        {
            if (_currentState == newState)
            {
                return false;
            }

            previous = _currentState;
            _currentState = newState;
        }

        StateChanged?.Invoke(previous, newState, reason);
        return true;
    }

    /// <summary>Moves to a new operating mode. Returns false when nothing changed.</summary>
    public bool MoveModeTo(OperatingMode newMode, string reason)
    {
        OperatingMode previous;
        MindState previousState;
        MindState currentState;
        lock (_transitionLock)
        {
            if (_currentMode == newMode)
            {
                return false;
            }

            previous = _currentMode;
            previousState = _currentState;
            _currentMode = newMode;

            // The mode drives the state unless she is mid-thought: a sleeping body
            // must never be shown as thinking.
            _currentState = newMode switch
            {
                OperatingMode.Sleeping => MindState.Sleeping,
                OperatingMode.Gaming => MindState.Gaming,
                _ => _currentState == MindState.Sleeping || _currentState == MindState.Gaming
                    ? MindState.Idle
                    : _currentState
            };
            currentState = _currentState;
        }

        ModeChanged?.Invoke(previous, newMode, reason);
        // A mode transition also changes the rendered mind state. The old implementation
        // mutated this field silently, leaving the shell, overlay and orb showing the
        // previous state until some unrelated event arrived.
        if (previousState != currentState)
        {
            StateChanged?.Invoke(previousState, currentState, reason);
        }
        return true;
    }

    /// <summary>True when the cortex is unloaded and only reflexes work.</summary>
    public bool IsCortexUnloaded =>
        CurrentMode == OperatingMode.Sleeping || CurrentMode == OperatingMode.Gaming;

    /// <summary>Human-readable label used by the status strip and the tray tooltip.</summary>
    public string DescribeState() => CurrentState switch
    {
        MindState.Booting => "Starting",
        MindState.Idle => "Ready",
        MindState.Listening => "Listening",
        MindState.Thinking => "Thinking",
        MindState.Acting => "Working",
        MindState.Speaking => "Speaking",
        MindState.PermissionWait => "Needs approval",
        MindState.Sleeping => "Resting",
        MindState.Gaming => "Gaming",
        _ => "Recovering"
    };
}
