namespace IronHive.Agent.Planning;

/// <summary>
/// Configuration options for <see cref="PlanAndExecuteOrchestrator"/>.
/// </summary>
public sealed class PlanAndExecuteOptions
{
    /// <summary>Maximum number of replan attempts before aborting.</summary>
    public int MaxReplans { get; set; } = 3;

    /// <summary>Maximum number of steps allowed in a single plan.</summary>
    public int MaxSteps { get; set; } = 10;
}
