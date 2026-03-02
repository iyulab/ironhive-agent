namespace IronHive.Agent.Planning;

/// <summary>
/// Configuration options for <see cref="PlannerTriggerDetector"/>.
/// Controls which prompts automatically trigger the planning pipeline.
/// </summary>
public class PlannerTriggerOptions
{
    /// <summary>
    /// Prompts longer than this character count are automatically treated as
    /// planning triggers. Set to 0 or negative to disable length-based triggering.
    /// </summary>
    public int MinContentLength { get; set; } = 800;

    /// <summary>
    /// Regex patterns for multi-step indicators.
    /// If any pattern matches, planning is triggered.
    /// </summary>
    public List<string> MultiStepPatterns { get; set; } =
    [
        @"(step\s+by\s+step|first\s*,?\s*then|after\s+that|next\s*,?\s*then)",
        @"(create\s+a\s+plan|plan\s+this|break\s+(?:it\s+)?down)",
    ];

    /// <summary>
    /// Regex patterns for explicit planning keywords.
    /// </summary>
    public List<string> ExplicitPlanPatterns { get; set; } =
    [
        @"(plan\s+this|make\s+a\s+plan|step\s+by\s+step)",
    ];
}
