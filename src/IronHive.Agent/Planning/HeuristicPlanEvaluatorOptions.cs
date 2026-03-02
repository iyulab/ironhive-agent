namespace IronHive.Agent.Planning;

/// <summary>
/// Configuration options for <see cref="HeuristicPlanEvaluator"/>.
/// </summary>
public class HeuristicPlanEvaluatorOptions
{
    /// <summary>
    /// Error patterns considered unrecoverable. If a step error contains
    /// any of these patterns (case-insensitive), the evaluator aborts the plan.
    /// </summary>
    public HashSet<string> CriticalErrorPatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "out of memory",
        "stack overflow",
        "disk full",
        "no space left on device",
        "access denied",
        "permission denied",
    };

    /// <summary>
    /// Maximum number of consecutive step failures (including the current step)
    /// before the evaluator aborts the plan. Set to 0 or negative to disable.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;
}
