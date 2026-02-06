namespace IronHive.Agent.Context;

/// <summary>
/// Token-based compaction trigger that protects recent tokens and ensures minimum prunable content.
/// </summary>
public class TokenBasedCompactionTrigger : ICompactionTrigger
{
    private readonly int _protectRecentTokens;
    private readonly int _minimumPruneTokens;

    /// <summary>
    /// Creates a new token-based compaction trigger.
    /// </summary>
    /// <param name="protectRecentTokens">Number of recent tokens to always protect (default: 40,000).</param>
    /// <param name="minimumPruneTokens">Minimum tokens that must be available for pruning (default: 20,000).</param>
    public TokenBasedCompactionTrigger(
        int protectRecentTokens = 40_000,
        int minimumPruneTokens = 20_000)
    {
        if (protectRecentTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protectRecentTokens), "Must be non-negative");
        }

        if (minimumPruneTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPruneTokens), "Must be non-negative");
        }

        _protectRecentTokens = protectRecentTokens;
        _minimumPruneTokens = minimumPruneTokens;
    }

    /// <summary>
    /// Number of recent tokens protected from compaction.
    /// </summary>
    public int ProtectRecentTokens => _protectRecentTokens;

    /// <summary>
    /// Minimum tokens required before compaction triggers.
    /// </summary>
    public int MinimumPruneTokens => _minimumPruneTokens;

    /// <inheritdoc />
    public float ThresholdPercentage =>
        // This is a compatibility property; actual trigger is token-based
        0.92f;

    /// <inheritdoc />
    public bool ShouldCompact(int currentTokens, int maxTokens)
    {
        if (maxTokens <= 0)
        {
            return false;
        }

        // Calculate how many tokens would be available for pruning
        // Total = System prompts (head) + Old messages (middle) + Recent (tail)
        // Prunable = middle portion = currentTokens - _protectRecentTokens
        var prunableTokens = currentTokens - _protectRecentTokens;

        // Only compact if:
        // 1. We're approaching the max limit (leave some buffer)
        // 2. There are enough tokens to make pruning worthwhile
        var tokensRemaining = maxTokens - currentTokens;
        var approachingLimit = tokensRemaining < _protectRecentTokens / 2; // Less than half the protect buffer remaining

        return approachingLimit && prunableTokens >= _minimumPruneTokens;
    }

    /// <summary>
    /// Gets detailed information about the compaction decision.
    /// </summary>
    public CompactionTriggerInfo GetInfo(int currentTokens, int maxTokens)
    {
        var prunableTokens = Math.Max(0, currentTokens - _protectRecentTokens);
        var tokensRemaining = maxTokens - currentTokens;

        return new CompactionTriggerInfo
        {
            CurrentTokens = currentTokens,
            MaxTokens = maxTokens,
            ProtectedTokens = _protectRecentTokens,
            PrunableTokens = prunableTokens,
            TokensRemaining = tokensRemaining,
            ShouldCompact = ShouldCompact(currentTokens, maxTokens),
            Reason = ShouldCompact(currentTokens, maxTokens)
                ? $"Approaching limit with {prunableTokens:N0} prunable tokens"
                : tokensRemaining < _protectRecentTokens / 2
                    ? $"Approaching limit but only {prunableTokens:N0} prunable tokens (need {_minimumPruneTokens:N0})"
                    : $"Sufficient headroom ({tokensRemaining:N0} tokens remaining)"
        };
    }
}

/// <summary>
/// Detailed information about a compaction trigger decision.
/// </summary>
public record CompactionTriggerInfo
{
    /// <summary>
    /// Current token count.
    /// </summary>
    public int CurrentTokens { get; init; }

    /// <summary>
    /// Maximum allowed tokens.
    /// </summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// Number of tokens being protected (recent history).
    /// </summary>
    public int ProtectedTokens { get; init; }

    /// <summary>
    /// Number of tokens available for pruning.
    /// </summary>
    public int PrunableTokens { get; init; }

    /// <summary>
    /// Number of tokens remaining before hitting max.
    /// </summary>
    public int TokensRemaining { get; init; }

    /// <summary>
    /// Whether compaction should be triggered.
    /// </summary>
    public bool ShouldCompact { get; init; }

    /// <summary>
    /// Human-readable reason for the decision.
    /// </summary>
    public string? Reason { get; init; }
}
