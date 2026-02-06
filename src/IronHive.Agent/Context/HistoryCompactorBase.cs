using System.Text;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Base class for history compactors providing common functionality.
/// </summary>
public abstract class HistoryCompactorBase : IHistoryCompactor
{
    private readonly IContextTokenCounter _tokenCounter;
    private readonly IChatClient? _summarizer;

    /// <summary>
    /// The token counter for measuring message sizes.
    /// </summary>
    protected IContextTokenCounter TokenCounter => _tokenCounter;

    /// <summary>
    /// Optional chat client for LLM-based summarization.
    /// </summary>
    protected IChatClient? Summarizer => _summarizer;

    protected HistoryCompactorBase(IContextTokenCounter tokenCounter, IChatClient? summarizer = null)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _summarizer = summarizer;
    }

    /// <inheritdoc />
    public abstract Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        int targetTokens,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a no-op compaction result when history is already within target.
    /// </summary>
    protected static CompactionResult CreateNoOpResult(IReadOnlyList<ChatMessage> history, int tokens)
    {
        return new CompactionResult
        {
            CompactedHistory = history,
            OriginalTokens = tokens,
            CompactedTokens = tokens,
            MessagesCompacted = 0
        };
    }

    /// <summary>
    /// Creates a compaction result.
    /// </summary>
    protected CompactionResult CreateResult(
        IReadOnlyList<ChatMessage> original,
        IReadOnlyList<ChatMessage> compacted,
        int originalTokens,
        int messagesCompacted)
    {
        return new CompactionResult
        {
            CompactedHistory = compacted,
            OriginalTokens = originalTokens,
            CompactedTokens = TokenCounter.CountTokens(compacted),
            MessagesCompacted = messagesCompacted
        };
    }

    /// <summary>
    /// Summarizes messages using LLM.
    /// </summary>
    /// <param name="messages">Messages to summarize.</param>
    /// <param name="targetTokens">Target token count for the summary.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list containing a single summary message.</returns>
    protected async Task<List<ChatMessage>> SummarizeWithLlmAsync(
        IReadOnlyList<ChatMessage> messages,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        if (Summarizer is null)
        {
            throw new InvalidOperationException("Summarizer is not available.");
        }

        var conversationText = new StringBuilder();
        foreach (var message in messages)
        {
            conversationText.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"[{message.Role}]: {message.Text}");
        }

        var summarizationPrompt = $"""
            Summarize the following conversation history concisely.
            Preserve key information: decisions made, tasks completed, important context.
            Keep the summary under {targetTokens / 4} tokens.

            Conversation:
            {conversationText}

            Summary:
            """;

        var response = await Summarizer.GetResponseAsync(summarizationPrompt, cancellationToken: cancellationToken);
        var summary = response.Text ?? string.Empty;

        return [new ChatMessage(ChatRole.System, $"[Previous conversation summary]: {summary}")];
    }

    /// <summary>
    /// Truncates messages from the beginning to fit within target tokens.
    /// </summary>
    /// <param name="messages">Messages to truncate.</param>
    /// <param name="targetTokens">Target token count.</param>
    /// <returns>Truncated message list.</returns>
    protected List<ChatMessage> TruncateFromBeginning(List<ChatMessage> messages, int targetTokens)
    {
        if (targetTokens <= 0)
        {
            return [new ChatMessage(ChatRole.System, "[Earlier conversation omitted due to context limits]")];
        }

        var result = new List<ChatMessage>();
        var currentTokens = 0;

        // Keep messages from the end until we hit the target
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var messageTokens = TokenCounter.CountTokens(messages[i]);
            if (currentTokens + messageTokens > targetTokens)
            {
                break;
            }

            result.Insert(0, messages[i]);
            currentTokens += messageTokens;
        }

        // Add marker if we truncated
        if (result.Count < messages.Count)
        {
            var omittedCount = messages.Count - result.Count;
            result.Insert(0, new ChatMessage(ChatRole.System, $"[{omittedCount} earlier messages omitted]"));
        }

        return result;
    }
}
