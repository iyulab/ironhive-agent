using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Options for history compaction.
/// </summary>
public class HistoryCompactorOptions
{
    /// <summary>
    /// Number of recent turns to preserve in the tail (not summarized).
    /// Default is 4 turns (8 messages: 4 user + 4 assistant).
    /// </summary>
    public int PreserveTailTurns { get; init; } = 4;

    /// <summary>
    /// Target compression ratio for the middle section.
    /// Default is 0.5 (compress to 50% of original).
    /// </summary>
    public float TargetCompressionRatio { get; init; } = 0.5f;

    /// <summary>
    /// Whether to use LLM for summarization (true) or simple truncation (false).
    /// </summary>
    public bool UseLlmSummarization { get; init; } = true;
}

/// <summary>
/// Compacts conversation history using Head/Middle/Tail strategy.
/// </summary>
public class HistoryCompactor : HistoryCompactorBase
{
    private readonly HistoryCompactorOptions _options;

    public HistoryCompactor(
        IContextTokenCounter tokenCounter,
        IChatClient? summarizer = null,
        HistoryCompactorOptions? options = null)
        : base(tokenCounter, summarizer)
    {
        _options = options ?? new HistoryCompactorOptions();
    }

    /// <inheritdoc />
    public override async Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        int targetTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);

        var originalTokens = TokenCounter.CountTokens(history);

        // If already within target, return as-is
        if (originalTokens <= targetTokens)
        {
            return CreateNoOpResult(history, originalTokens);
        }

        // Split into Head/Middle/Tail
        var (head, middle, tail) = SplitHistory(history);

        // Calculate token budgets
        var headTokens = TokenCounter.CountTokens(head);
        var tailTokens = TokenCounter.CountTokens(tail);
        var middleTargetTokens = Math.Max(0, targetTokens - headTokens - tailTokens);

        // Compact the middle section
        var compactedMiddle = await CompactMiddleAsync(
            middle,
            middleTargetTokens,
            cancellationToken);

        // Reassemble
        var compactedHistory = new List<ChatMessage>();
        compactedHistory.AddRange(head);
        compactedHistory.AddRange(compactedMiddle);
        compactedHistory.AddRange(tail);

        return CreateResult(history, compactedHistory, originalTokens, middle.Count - compactedMiddle.Count);
    }

    private (List<ChatMessage> head, List<ChatMessage> middle, List<ChatMessage> tail) SplitHistory(
        IReadOnlyList<ChatMessage> history)
    {
        var head = new List<ChatMessage>();
        var middle = new List<ChatMessage>();
        var tail = new List<ChatMessage>();

        // Head: System message(s)
        var index = 0;
        while (index < history.Count && history[index].Role == ChatRole.System)
        {
            head.Add(history[index]);
            index++;
        }

        // Tail: Last N turns (preserve recent context)
        var tailMessageCount = _options.PreserveTailTurns * 2; // 2 messages per turn (user + assistant)
        var tailStartIndex = Math.Max(index, history.Count - tailMessageCount);

        // Middle: Everything between head and tail
        for (var i = index; i < tailStartIndex; i++)
        {
            middle.Add(history[i]);
        }

        // Tail
        for (var i = tailStartIndex; i < history.Count; i++)
        {
            tail.Add(history[i]);
        }

        return (head, middle, tail);
    }

    private async Task<List<ChatMessage>> CompactMiddleAsync(
        List<ChatMessage> middle,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        if (middle.Count == 0)
        {
            return [];
        }

        var middleTokens = TokenCounter.CountTokens(middle);

        // If middle already fits, return as-is
        if (middleTokens <= targetTokens)
        {
            return middle;
        }

        // Try LLM summarization if available
        if (_options.UseLlmSummarization && Summarizer is not null)
        {
            try
            {
                return await SummarizeWithLlmAsync(middle, targetTokens, cancellationToken);
            }
            catch
            {
                // Fallback to truncation on error
                return TruncateFromBeginning(middle, targetTokens);
            }
        }

        // Fallback: Simple truncation (remove oldest messages)
        return TruncateFromBeginning(middle, targetTokens);
    }
}
