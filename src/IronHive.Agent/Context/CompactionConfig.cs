namespace IronHive.Agent.Context;

/// <summary>
/// Configuration for context compaction.
/// </summary>
public class CompactionConfig
{
    /// <summary>
    /// Number of tokens to protect at the end of the history (most recent).
    /// Default: 40,000 tokens.
    /// </summary>
    public int ProtectRecentTokens { get; set; } = 40_000;

    /// <summary>
    /// Minimum number of tokens that must be available for pruning.
    /// Compaction only occurs if there are at least this many tokens to prune.
    /// Default: 20,000 tokens.
    /// </summary>
    public int MinimumPruneTokens { get; set; } = 20_000;

    /// <summary>
    /// Tool outputs that should be protected from aggressive summarization.
    /// These tools' outputs will be preserved more carefully during compaction.
    /// </summary>
    public List<string> ProtectedToolOutputs { get; set; } = ["read_file", "grep", "glob"];

    /// <summary>
    /// Target compression ratio when compacting (0.0-1.0).
    /// After compaction, the context should be approximately this percentage of max tokens.
    /// Default: 0.70 (70%).
    /// </summary>
    public float TargetRatio { get; set; } = 0.70f;

    /// <summary>
    /// Whether to use token-based compaction instead of percentage-based.
    /// When true, uses ProtectRecentTokens and MinimumPruneTokens.
    /// When false, uses traditional percentage-based threshold.
    /// </summary>
    public bool UseTokenBasedCompaction { get; set; } = true;

    /// <summary>
    /// Threshold percentage for percentage-based compaction (legacy mode).
    /// Only used when UseTokenBasedCompaction is false.
    /// </summary>
    public float ThresholdPercentage { get; set; } = 0.92f;
}
