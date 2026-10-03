using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Token-based history compactor that protects recent tokens, user messages and the outputs of
/// <see cref="CompactionConfig.ProtectedToolOutputs"/>, and summarizes the rest.
/// </summary>
public class TokenBasedHistoryCompactor : HistoryCompactorBase
{
    private readonly CompactionConfig _config;

    public TokenBasedHistoryCompactor(
        IContextTokenCounter tokenCounter,
        CompactionConfig? config = null,
        IChatClient? summarizer = null)
        : base(tokenCounter, summarizer)
    {
        _config = config ?? new CompactionConfig();
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

        // Split history into protected and prunable regions, on group boundaries
        var (systemMessages, conversationMessages) = SplitSystemMessages(history);
        var (prunableRegion, protectedRegion) = SplitRecent(conversationMessages, _config.ProtectRecentTokens);

        // Calculate token budgets
        var systemTokens = TokenCounter.CountTokens(systemMessages);
        var protectedTokens = TokenCounter.CountTokens(protectedRegion);
        var prunableTargetTokens = Math.Max(0, targetTokens - systemTokens - protectedTokens);

        // Compact the prunable region
        var compactedPrunable = await CompactPrunableAsync(
            prunableRegion,
            prunableTargetTokens,
            cancellationToken);

        // Reassemble
        var compactedHistory = new List<ChatMessage>();
        compactedHistory.AddRange(systemMessages);
        compactedHistory.AddRange(compactedPrunable);
        compactedHistory.AddRange(protectedRegion);

        return CreateResult(history, compactedHistory, originalTokens, prunableRegion.Count - compactedPrunable.Count);
    }

    private static (List<ChatMessage> system, List<ChatMessage> conversation) SplitSystemMessages(
        IReadOnlyList<ChatMessage> history)
    {
        var system = new List<ChatMessage>();
        var conversation = new List<ChatMessage>();

        foreach (var message in history)
        {
            if (message.Role == ChatRole.System)
            {
                system.Add(message);
            }
            else
            {
                conversation.Add(message);
            }
        }

        return (system, conversation);
    }

    /// <summary>
    /// Keeps the important groups of the prunable region whole and replaces the rest with a summary (or, without a
    /// summarizer, a marker naming how many messages were left out). Important: user messages — small, and the one
    /// thing that cannot be fetched again — and the tool groups of <see cref="CompactionConfig.ProtectedToolOutputs"/>.
    /// When the important groups alone exceed the target, the oldest protected tool groups, then the oldest user
    /// messages, join the summary, so a compaction reaches its target instead of firing again on the next turn.
    /// </summary>
    private async Task<List<ChatMessage>> CompactPrunableAsync(
        List<ChatMessage> prunable,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        if (prunable.Count == 0)
        {
            return [];
        }

        var prunableTokens = TokenCounter.CountTokens(prunable);

        // If prunable already fits, return as-is
        if (prunableTokens <= targetTokens)
        {
            return prunable;
        }

        // Check if there are enough tokens to warrant pruning
        if (prunableTokens < _config.MinimumPruneTokens)
        {
            // Not enough to prune meaningfully, keep as-is
            return prunable;
        }

        var groups = GroupMessages(prunable);
        var important = groups.Select(IsImportantGroup).ToArray();

        // Leave room for the summary of what is not kept.
        var keepBudget = Math.Max(0, targetTokens - SummaryFloorTokens);
        DemoteOldestUntilWithin(groups, important, keepBudget, IsToolGroup);
        DemoteOldestUntilWithin(groups, important, keepBudget, group => !IsToolGroup(group));

        var regular = groups.Where((_, g) => !important[g]).SelectMany(x => x).ToList();
        var importantTokens = KeptTokens(groups, important);
        var summary = await SummarizeOrMarkAsync(regular, Math.Max(SummaryFloorTokens, targetTokens - importantTokens), cancellationToken);

        var result = new List<ChatMessage>();
        result.AddRange(summary);
        for (var g = 0; g < groups.Count; g++)
        {
            if (important[g])
            {
                result.AddRange(groups[g]);
            }
        }

        return result;
    }

    private async Task<List<ChatMessage>> SummarizeOrMarkAsync(
        List<ChatMessage> regular, int summaryTokens, CancellationToken cancellationToken)
    {
        if (regular.Count == 0)
        {
            return [];
        }

        if (Summarizer is not null)
        {
            try
            {
                return await SummarizeWithLlmAsync(regular, summaryTokens, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // LLM summarization failed — the marker below is the intentional fallback
            }
        }

        return [new ChatMessage(ChatRole.System, $"[{regular.Count} earlier messages omitted]")];
    }

    private bool IsImportantGroup(List<ChatMessage> group)
    {
        if (IsUserMessageGroup(group))
        {
            return true;
        }

        return group[0].Contents.OfType<FunctionCallContent>()
            .Any(call => _config.ProtectedToolOutputs.Contains(call.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsToolGroup(List<ChatMessage> group) =>
        group[0].Contents.OfType<FunctionCallContent>().Any();
}
