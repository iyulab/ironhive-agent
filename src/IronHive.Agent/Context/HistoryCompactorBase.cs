using System.Text;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Base class for history compactors providing common functionality.
/// </summary>
/// <remarks>
/// A tool call and its result move together: an assistant message that calls tools and the tool messages that answer
/// it form one group (<see cref="GroupMessages"/>), and the protected recent region is cut on group boundaries
/// (<see cref="SplitRecent"/>). Every result also passes <see cref="RemoveUnpairedToolContent"/>, so no compactor
/// returns a tool result without its call, or a call without its result, before the end of the history.
/// </remarks>
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
        compacted = RemoveUnpairedToolContent(compacted);
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

        // Keep whole groups (a tool call with its results) from the end until we hit the target
        var groups = GroupMessages(messages);
        for (var g = groups.Count - 1; g >= 0; g--)
        {
            var groupTokens = TokenCounter.CountTokens(groups[g]);
            if (currentTokens + groupTokens > targetTokens)
            {
                break;
            }

            result.InsertRange(0, groups[g]);
            currentTokens += groupTokens;
        }

        // Add marker if we truncated
        if (result.Count < messages.Count)
        {
            var omittedCount = messages.Count - result.Count;
            result.Insert(0, new ChatMessage(ChatRole.System, $"[{omittedCount} earlier messages omitted]"));
        }

        return result;
    }

    /// <summary>
    /// Splits messages into the units compaction moves: an assistant message that calls tools together with the tool
    /// messages that answer those calls, or a single message. A unit is kept, summarized or dropped whole.
    /// </summary>
    protected static List<List<ChatMessage>> GroupMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var groups = new List<List<ChatMessage>>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var group = new List<ChatMessage> { message };
            var callIds = CallIds(message);
            if (message.Role == ChatRole.Assistant && callIds.Count > 0)
            {
                while (i + 1 < messages.Count
                       && messages[i + 1].Role == ChatRole.Tool
                       && messages[i + 1].Contents.OfType<FunctionResultContent>().Any(r => callIds.Contains(r.CallId)))
                {
                    group.Add(messages[++i]);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Splits the conversation into the prunable older part and the protected recent part: whole groups from the newest
    /// back while they fit <paramref name="protectTokens"/>. The newest group is always protected — it holds the request
    /// being answered.
    /// </summary>
    protected (List<ChatMessage> Prunable, List<ChatMessage> Protected) SplitRecent(
        IReadOnlyList<ChatMessage> conversation, int protectTokens)
    {
        var groups = GroupMessages(conversation);
        var protectedStart = groups.Count;
        var used = 0;
        for (var g = groups.Count - 1; g >= 0; g--)
        {
            var tokens = TokenCounter.CountTokens(groups[g]);
            if (g < groups.Count - 1 && used + tokens > protectTokens)
            {
                break;
            }

            used += tokens;
            protectedStart = g;
        }

        return ([.. groups.Take(protectedStart).SelectMany(x => x)], [.. groups.Skip(protectedStart).SelectMany(x => x)]);
    }

    /// <summary>
    /// Removes tool results whose call is not in <paramref name="messages"/>, and tool calls whose result is not — except
    /// calls in the last message, which may still be waiting for their results. A message left empty is dropped. Returns
    /// <paramref name="messages"/> itself when nothing is unpaired.
    /// </summary>
    protected static IReadOnlyList<ChatMessage> RemoveUnpairedToolContent(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var calls = new HashSet<string>(StringComparer.Ordinal);
        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && call.CallId is not null)
                {
                    calls.Add(call.CallId);
                }
                else if (content is FunctionResultContent result && result.CallId is not null)
                {
                    results.Add(result.CallId);
                }
            }
        }

        bool Unpaired(AIContent content, bool isLast) => content switch
        {
            FunctionResultContent result => result.CallId is not null && !calls.Contains(result.CallId),
            FunctionCallContent call => !isLast && call.CallId is not null && !results.Contains(call.CallId),
            _ => false,
        };

        List<ChatMessage>? cleaned = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var isLast = i == messages.Count - 1;
            if (!message.Contents.Any(c => Unpaired(c, isLast)))
            {
                cleaned?.Add(message);
                continue;
            }

            cleaned ??= [.. messages.Take(i)];
            var kept = message.Contents.Where(c => !Unpaired(c, isLast)).ToList();
            if (kept.Count > 0)
            {
                cleaned.Add(new ChatMessage(message.Role, kept) { AuthorName = message.AuthorName, MessageId = message.MessageId });
            }
        }

        return cleaned ?? messages;
    }

    private static HashSet<string> CallIds(ChatMessage message) =>
        [.. message.Contents.OfType<FunctionCallContent>().Select(c => c.CallId).OfType<string>()];
}
