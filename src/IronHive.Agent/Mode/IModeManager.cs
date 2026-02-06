namespace IronHive.Agent.Mode;

/// <summary>
/// Manages agent mode transitions.
/// </summary>
public interface IModeManager
{
    /// <summary>
    /// Gets the current agent mode.
    /// </summary>
    AgentMode CurrentMode { get; }

    /// <summary>
    /// Attempts to fire a trigger and transition to a new mode.
    /// </summary>
    /// <param name="trigger">The trigger to fire</param>
    /// <returns>True if transition was successful, false if not permitted</returns>
    bool Fire(ModeTrigger trigger);

    /// <summary>
    /// Checks if a trigger can be fired from the current state.
    /// </summary>
    /// <param name="trigger">The trigger to check</param>
    /// <returns>True if the trigger is permitted</returns>
    bool CanFire(ModeTrigger trigger);

    /// <summary>
    /// Gets the permitted triggers from the current state.
    /// </summary>
    IReadOnlyList<ModeTrigger> GetPermittedTriggers();

    /// <summary>
    /// Event raised when mode changes.
    /// </summary>
    event EventHandler<ModeChangedEventArgs>? ModeChanged;
}

/// <summary>
/// Event arguments for mode changes.
/// </summary>
public class ModeChangedEventArgs : EventArgs
{
    /// <summary>
    /// The mode before the transition.
    /// </summary>
    public required AgentMode PreviousMode { get; init; }

    /// <summary>
    /// The mode after the transition.
    /// </summary>
    public required AgentMode NewMode { get; init; }

    /// <summary>
    /// The trigger that caused the transition.
    /// </summary>
    public required ModeTrigger Trigger { get; init; }
}
