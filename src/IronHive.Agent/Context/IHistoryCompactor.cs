using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Result of history compaction.
/// </summary>
public record CompactionResult
{
    /// <summary>
    /// The compacted message history.
    /// </summary>
    public required IReadOnlyList<ChatMessage> CompactedHistory { get; init; }

    /// <summary>
    /// Token count before compaction.
    /// </summary>
    public int OriginalTokens { get; init; }

    /// <summary>
    /// Token count after compaction.
    /// </summary>
    public int CompactedTokens { get; init; }

    /// <summary>
    /// Number of messages removed or summarized.
    /// </summary>
    public int MessagesCompacted { get; init; }

    /// <summary>
    /// Compression ratio (compacted/original).
    /// </summary>
    public float CompressionRatio => OriginalTokens > 0
        ? (float)CompactedTokens / OriginalTokens
        : 1.0f;
}

/// <summary>
/// Compacts conversation history to fit within token limits.
/// Uses Head/Middle/Tail strategy:
/// - Head: System prompt + initial context (preserved)
/// - Middle: Older conversation turns (summarized)
/// - Tail: Recent conversation (preserved)
/// </summary>
public interface IHistoryCompactor
{
    /// <summary>
    /// Compacts the history to fit within the target token count.
    /// </summary>
    /// <param name="history">The conversation history to compact.</param>
    /// <param name="targetTokens">Target token count after compaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compaction result.</returns>
    Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        int targetTokens,
        CancellationToken cancellationToken = default);
}
