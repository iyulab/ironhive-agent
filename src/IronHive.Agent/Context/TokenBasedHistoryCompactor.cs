using System.Text;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Token-based history compactor that protects recent tokens and important tool outputs.
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

        // Split history into protected and prunable regions
        var (systemMessages, conversationMessages) = SplitSystemMessages(history);
        var protectedRegion = GetProtectedRecentMessages(conversationMessages);
        var prunableRegion = GetPrunableMessages(conversationMessages, protectedRegion.Count);

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

    private List<ChatMessage> GetProtectedRecentMessages(List<ChatMessage> conversation)
    {
        var protectedTokens = _config.ProtectRecentTokens;
        var result = new List<ChatMessage>();
        var currentTokens = 0;

        // Work backwards from the end to protect recent messages
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            var messageTokens = TokenCounter.CountTokens(conversation[i]);

            if (currentTokens + messageTokens > protectedTokens)
            {
                break;
            }

            result.Insert(0, conversation[i]);
            currentTokens += messageTokens;
        }

        return result;
    }

    private static List<ChatMessage> GetPrunableMessages(List<ChatMessage> conversation, int protectedCount)
    {
        var prunableCount = conversation.Count - protectedCount;
        return conversation.Take(prunableCount).ToList();
    }

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

        // Separate important tool outputs (protected from aggressive summarization)
        var (importantMessages, regularMessages) = SeparateImportantMessages(prunable);

        // Try LLM summarization for regular messages if available
        if (Summarizer is not null)
        {
            return await SummarizeImportantWithLlmAsync(
                importantMessages,
                regularMessages,
                targetTokens,
                cancellationToken);
        }

        // Fallback: Simple truncation
        return TruncateMessagesWithImportant(importantMessages, regularMessages, targetTokens);
    }

    private (List<ChatMessage> important, List<ChatMessage> regular) SeparateImportantMessages(
        List<ChatMessage> messages)
    {
        var important = new List<ChatMessage>();
        var regular = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (IsImportantMessage(message))
            {
                important.Add(message);
            }
            else
            {
                regular.Add(message);
            }
        }

        return (important, regular);
    }

    private bool IsImportantMessage(ChatMessage message)
    {
        // Check if this is a tool response from a protected tool
        if (message.Role == ChatRole.Tool)
        {
            return true; // Tool outputs are generally important
        }

        // Check for tool calls in assistant messages
        if (message.Role == ChatRole.Assistant && message.Contents is not null)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    if (_config.ProtectedToolOutputs.Contains(functionCall.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task<List<ChatMessage>> SummarizeImportantWithLlmAsync(
        List<ChatMessage> important,
        List<ChatMessage> regular,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        var importantTokens = TokenCounter.CountTokens(important);
        var regularTargetTokens = Math.Max(0, targetTokens - importantTokens);

        // If no room for regular messages, just return important ones
        if (regularTargetTokens <= 100)
        {
            return CreateSummaryWithImportant(important, []);
        }

        // Summarize regular messages
        var conversationText = new StringBuilder();
        foreach (var message in regular)
        {
            conversationText.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"[{message.Role}]: {message.Text}");
        }

        var summarizationPrompt = $"""
            Summarize the following conversation history concisely.
            Preserve key information: decisions made, tasks completed, important context.
            Keep the summary under {regularTargetTokens / 4} tokens.

            Conversation:
            {conversationText}

            Summary:
            """;

        try
        {
            var response = await Summarizer!.GetResponseAsync(summarizationPrompt, cancellationToken: cancellationToken);
            var summary = response.Text ?? string.Empty;

            return CreateSummaryWithImportant(important, summary);
        }
        catch (Exception)
        {
            // LLM summarization failed — fallback to truncation is intentional
            return TruncateMessagesWithImportant(important, regular, targetTokens);
        }
    }

    private static List<ChatMessage> CreateSummaryWithImportant(List<ChatMessage> important, string summary)
    {
        var result = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Add(new ChatMessage(ChatRole.System, $"[Previous conversation summary]: {summary}"));
        }

        // Add key tool results back
        foreach (var msg in important)
        {
            result.Add(msg);
        }

        return result;
    }

    private static List<ChatMessage> CreateSummaryWithImportant(List<ChatMessage> important, List<ChatMessage> summarized)
    {
        var result = new List<ChatMessage>();

        if (summarized.Count > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, "[Earlier conversation omitted]"));
        }

        result.AddRange(important);
        return result;
    }

    private List<ChatMessage> TruncateMessagesWithImportant(
        List<ChatMessage> important,
        List<ChatMessage> regular,
        int targetTokens)
    {
        var importantTokens = TokenCounter.CountTokens(important);
        var regularTargetTokens = Math.Max(0, targetTokens - importantTokens);

        var result = new List<ChatMessage>();

        // Add truncation marker
        if (regular.Count > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, $"[{regular.Count} earlier messages omitted]"));
        }

        // Keep important messages
        result.AddRange(important);

        // Add regular messages from the end if there's room
        var currentTokens = TokenCounter.CountTokens(result);
        for (var i = regular.Count - 1; i >= 0 && currentTokens < targetTokens; i--)
        {
            var messageTokens = TokenCounter.CountTokens(regular[i]);
            if (currentTokens + messageTokens > targetTokens)
            {
                break;
            }

            result.Insert(1, regular[i]); // After the truncation marker
            currentTokens += messageTokens;
        }

        return result;
    }
}
