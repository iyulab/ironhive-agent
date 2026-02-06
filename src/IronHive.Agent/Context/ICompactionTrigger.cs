namespace IronHive.Agent.Context;

/// <summary>
/// Determines when context compaction should be triggered.
/// </summary>
public interface ICompactionTrigger
{
    /// <summary>
    /// Checks if compaction should be triggered based on current token usage.
    /// </summary>
    /// <param name="currentTokens">Current token count in context.</param>
    /// <param name="maxTokens">Maximum allowed tokens.</param>
    /// <returns>True if compaction should be triggered.</returns>
    bool ShouldCompact(int currentTokens, int maxTokens);

    /// <summary>
    /// Gets the threshold percentage (0.0 to 1.0) at which compaction triggers.
    /// </summary>
    float ThresholdPercentage { get; }
}

/// <summary>
/// Compaction trigger that activates at a percentage threshold.
/// Default is 92% based on Claude Code research.
/// </summary>
public class ThresholdCompactionTrigger : ICompactionTrigger
{
    public ThresholdCompactionTrigger(float thresholdPercentage = 0.92f)
    {
        if (thresholdPercentage is < 0.5f or > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholdPercentage),
                "Threshold must be between 0.5 and 1.0");
        }

        ThresholdPercentage = thresholdPercentage;
    }

    /// <inheritdoc />
    public float ThresholdPercentage { get; }

    /// <inheritdoc />
    public bool ShouldCompact(int currentTokens, int maxTokens)
    {
        if (maxTokens <= 0)
        {
            return false;
        }

        var usageRatio = (float)currentTokens / maxTokens;
        return usageRatio >= ThresholdPercentage;
    }
}
