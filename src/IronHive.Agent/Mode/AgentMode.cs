namespace IronHive.Agent.Mode;

/// <summary>
/// Agent operating modes for Plan/Work/HITL pattern.
/// </summary>
public enum AgentMode
{
    /// <summary>
    /// Initial state, waiting for user input.
    /// </summary>
    Idle,

    /// <summary>
    /// Planning mode - read-only exploration, no destructive operations.
    /// </summary>
    Planning,

    /// <summary>
    /// Working mode - full tool access, executing tasks.
    /// </summary>
    Working,

    /// <summary>
    /// Human-in-the-loop mode - waiting for user approval on risky operations.
    /// </summary>
    HumanInTheLoop
}

/// <summary>
/// Triggers for mode transitions.
/// </summary>
public enum ModeTrigger
{
    /// <summary>
    /// Start planning (--plan flag or plan command).
    /// </summary>
    StartPlanning,

    /// <summary>
    /// Finish planning, ready to execute.
    /// </summary>
    FinishPlanning,

    /// <summary>
    /// Start working/executing tasks.
    /// </summary>
    StartWorking,

    /// <summary>
    /// Risky operation detected, need user approval.
    /// </summary>
    RiskyOperationDetected,

    /// <summary>
    /// User approved the risky operation.
    /// </summary>
    UserApproved,

    /// <summary>
    /// User rejected the risky operation.
    /// </summary>
    UserRejected,

    /// <summary>
    /// Task completed or cancelled.
    /// </summary>
    Complete,

    /// <summary>
    /// Reset to idle state.
    /// </summary>
    Reset,

    /// <summary>
    /// Replan requested due to failure or new information.
    /// </summary>
    ReplanRequested
}
