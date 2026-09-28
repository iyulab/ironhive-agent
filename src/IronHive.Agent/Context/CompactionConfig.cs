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
    /// Number of recent tool rounds to protect from observation masking, counted across turn boundaries — a round is
    /// an assistant message that calls tools plus the results that answer it. <c>null</c> (default): off, only user
    /// turns protect. When set, tool results older than the last N rounds are masked even inside a protected turn, so
    /// a long single-message task (read a document section by section, walk a folder) keeps only its recent results at
    /// full size. Masking inside one turn needs the model calls of that turn to pass through the context manager —
    /// add <see cref="ToolRoundContextChatClient"/> inside function invocation
    /// (<c>.UseFunctionInvocation().UseToolRoundContext(contextManager)</c>).
    /// </summary>
    public int? ObservationMaskingProtectedRounds { get; set; }

    /// <summary>
    /// Goal reminder options for a context manager built from this config (<see cref="ContextManager.ForModel(string, CompactionConfig, Microsoft.Extensions.AI.IChatClient?)"/>).
    /// <c>null</c> (default): the reminder's own defaults (on, after 6 messages). Set <c>new GoalReminderOptions { Enabled = false }</c>
    /// to turn it off.
    /// </summary>
    public GoalReminderOptions? GoalReminder { get; set; }

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
    /// Maximum character count for the anchor state block (<see cref="UseAnchoredCompaction"/>). Anchors are merged
    /// across compaction rounds, so without a limit the block grows for the whole session. Over the limit, the oldest
    /// entries are left out first: completed steps, then errors, modified files and key decisions; failed approaches
    /// (which keep the model from repeating them) go last. Zero or less means no limit. Default: 2000.
    /// </summary>
    public int MaxAnchorStateChars { get; set; } = 2000;

    /// <summary>
    /// Optional override for the model's maximum context token count.
    /// When set, this value takes precedence over the hardcoded model context size dictionary.
    /// Use this for local/custom models whose context window is not in the built-in lookup table.
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// When the model's context window is not known — <see cref="MaxContextTokens"/> is not set and the model is not in the
    /// catalog, so the counter falls back to a guess — do not compact pre-emptively against the guess. Instead, when a model
    /// call fails with <c>ContextOverflowException</c>, compact that request once and retry it, and learn the window from the
    /// error (its stated window, or else the size that overflowed) so later turns compact against the real number.
    /// A second overflow on the retry propagates.
    /// <para>
    /// Needs a <see cref="ToolRoundContextChatClient"/> in the chat pipeline (<c>.UseFunctionInvocation().UseToolRoundContext()</c>)
    /// — that is where overflows are caught, on every model call including each tool round. Without one, the manager keeps
    /// compacting pre-emptively against the guess. When the window is known, compaction stays pre-emptive; an overflow that still
    /// happens (a configured window larger than the server's) is caught and handled the same way. Default: <c>true</c>.
    /// </para>
    /// </summary>
    public bool CompactOnOverflow { get; set; } = true;
}
