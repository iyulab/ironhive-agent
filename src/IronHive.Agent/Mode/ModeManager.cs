namespace IronHive.Agent.Mode;

/// <summary>
/// Default implementation of mode manager using a simple state machine.
/// </summary>
/// <remarks>
/// State transitions:
///
/// IDLE ─────────────────────────────────────────────────────────┐
///   │                                                           │
///   ├─ StartPlanning ──► PLANNING                               │
///   │                       │                                   │
///   └─ StartWorking ──────►│├─ FinishPlanning ──► WORKING ◄────┘
///                           │                       │
///                           └─ Reset ───────────────┤
///                                                   │
///                           WORKING                 │
///                             │                     │
///                             ├─ RiskyOperation ──► HITL
///                             │                       │
///                             └─ Complete ──► IDLE    │
///                                                     │
///                           HITL                      │
///                             ├─ Approved ──► WORKING │
///                             └─ Rejected ──► IDLE ◄──┘
/// </remarks>
public class ModeManager : IModeManager
{
    private AgentMode _currentMode = AgentMode.Idle;
    private readonly object _lock = new();

    /// <inheritdoc />
    public AgentMode CurrentMode
    {
        get
        {
            lock (_lock)
            {
                return _currentMode;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ModeChangedEventArgs>? ModeChanged;

    /// <inheritdoc />
    public bool Fire(ModeTrigger trigger)
    {
        lock (_lock)
        {
            var newMode = GetNextMode(_currentMode, trigger);
            if (newMode is null)
            {
                return false;
            }

            var previousMode = _currentMode;
            _currentMode = newMode.Value;

            // Raise event outside lock? For now, keep it simple.
            ModeChanged?.Invoke(this, new ModeChangedEventArgs
            {
                PreviousMode = previousMode,
                NewMode = newMode.Value,
                Trigger = trigger
            });

            return true;
        }
    }

    /// <inheritdoc />
    public bool CanFire(ModeTrigger trigger)
    {
        lock (_lock)
        {
            return GetNextMode(_currentMode, trigger) is not null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModeTrigger> GetPermittedTriggers()
    {
        lock (_lock)
        {
            return GetPermittedTriggersForMode(_currentMode);
        }
    }

    private static AgentMode? GetNextMode(AgentMode current, ModeTrigger trigger)
    {
        return (current, trigger) switch
        {
            // From Idle
            (AgentMode.Idle, ModeTrigger.StartPlanning) => AgentMode.Planning,
            (AgentMode.Idle, ModeTrigger.StartWorking) => AgentMode.Working,

            // From Planning
            (AgentMode.Planning, ModeTrigger.FinishPlanning) => AgentMode.Working,
            (AgentMode.Planning, ModeTrigger.Reset) => AgentMode.Idle,

            // From Working
            (AgentMode.Working, ModeTrigger.RiskyOperationDetected) => AgentMode.HumanInTheLoop,
            (AgentMode.Working, ModeTrigger.Complete) => AgentMode.Idle,
            (AgentMode.Working, ModeTrigger.Reset) => AgentMode.Idle,
            (AgentMode.Working, ModeTrigger.ReplanRequested) => AgentMode.Planning,

            // From HumanInTheLoop
            (AgentMode.HumanInTheLoop, ModeTrigger.UserApproved) => AgentMode.Working,
            (AgentMode.HumanInTheLoop, ModeTrigger.UserRejected) => AgentMode.Idle,
            (AgentMode.HumanInTheLoop, ModeTrigger.Reset) => AgentMode.Idle,

            // Invalid transitions
            _ => null
        };
    }

    private static IReadOnlyList<ModeTrigger> GetPermittedTriggersForMode(AgentMode mode)
    {
        return mode switch
        {
            AgentMode.Idle => [ModeTrigger.StartPlanning, ModeTrigger.StartWorking],
            AgentMode.Planning => [ModeTrigger.FinishPlanning, ModeTrigger.Reset],
            AgentMode.Working => [ModeTrigger.RiskyOperationDetected, ModeTrigger.Complete, ModeTrigger.Reset, ModeTrigger.ReplanRequested],
            AgentMode.HumanInTheLoop => [ModeTrigger.UserApproved, ModeTrigger.UserRejected, ModeTrigger.Reset],
            _ => []
        };
    }
}
