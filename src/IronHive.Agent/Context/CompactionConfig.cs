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

    /// <summary>
    /// Whether to mask old tool observations before compaction.
    /// When enabled, older tool results are replaced with compact placeholders,
    /// reducing token usage before the compaction step.
    /// </summary>
    public bool EnableObservationMasking { get; set; } = true;

    /// <summary>
    /// Number of recent user turns to protect from observation masking.
    /// A "turn" starts with a user message and includes all subsequent messages
    /// until the next user message.
    /// </summary>
    public int ObservationMaskingProtectedTurns { get; set; } = 3;

    /// <summary>
    /// Minimum result character length to trigger observation masking.
    /// Results shorter than this threshold are kept as-is to avoid
    /// increasing token count with placeholder text.
    /// </summary>
    public int ObservationMaskingMinResultLength { get; set; } = 200;

    /// <summary>
    /// Compression level for tool schemas.
    /// Reduces token usage by shortening descriptions and removing verbose schema elements.
    /// </summary>
    public ToolSchemaCompressionLevel ToolSchemaCompression { get; set; } = ToolSchemaCompressionLevel.None;

    /// <summary>
    /// Whether to compact large tool results via head+tail truncation.
    /// When enabled, tool results exceeding <see cref="MaxToolResultChars"/> are compacted
    /// before other context management steps.
    /// </summary>
    public bool EnableToolResultCompaction { get; set; } = true;

    /// <summary>
    /// Maximum tool result character count before compaction triggers.
    /// Default: 30,000.
    /// </summary>
    public int MaxToolResultChars { get; set; } = 30_000;

    /// <summary>
    /// Number of lines to keep from the beginning of a compacted tool result.
    /// Default: 50.
    /// </summary>
    public int ToolResultKeepHeadLines { get; set; } = 50;

    /// <summary>
    /// Number of lines to keep from the end of a compacted tool result.
    /// Default: 20.
    /// </summary>
    public int ToolResultKeepTailLines { get; set; } = 20;

    /// <summary>
    /// Whether to use anchored compaction, which preserves structured state information
    /// (goal, modified files, errors, decisions) across compaction rounds.
    /// Prevents silent information drift during LLM-based summarization.
    /// When true, overrides UseTokenBasedCompaction for the compactor selection.
    /// </summary>
    public bool UseAnchoredCompaction { get; set; }

    /// <summary>
    /// Maximum character count for the anchor state block.
    /// Limits how much structured state information is preserved across compaction rounds.
    /// Default: 2000.
    /// </summary>
    public int MaxAnchorStateChars { get; set; } = 2000;
}
