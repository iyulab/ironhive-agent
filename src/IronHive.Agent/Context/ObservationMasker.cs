using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Masks old tool observation results with compact placeholders to reduce context window usage.
/// Protects recent user turns from masking, only replacing older tool results — and, when
/// <c>protectedTokens</c> is set, also the tool results inside those turns that no longer fit a size budget.
/// </summary>
/// <remarks>
/// A placeholder names the call that produced the result (the tool and its arguments) and says the content is no longer
/// visible, so a model that needs it again can re-issue the same call instead of working from memory. It depends only on
/// the call and the result, so the same result is masked to the same text on every request.
/// </remarks>
public class ObservationMasker
{
    /// <summary>Longest argument text a placeholder repeats; longer arguments are cut with an ellipsis.</summary>
    internal const int MaxArgumentsLength = 200;

    // One line, so a placeholder repeats the call as the model wrote it rather than spread over several lines.
    private static readonly JsonSerializerOptions ArgumentsJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    private readonly int _protectedTurns;
    private readonly int _minimumResultLength;
    private readonly int? _protectedTokens;
    private readonly IContextTokenCounter? _tokenCounter;

    /// <summary>
    /// Creates a new observation masker.
    /// </summary>
    /// <param name="protectedTurns">
    /// Number of recent user turns to protect from masking.
    /// A "turn" starts with a user message and includes all subsequent messages until the next user message.
    /// Default: 3.
    /// </param>
    /// <param name="minimumResultLength">
    /// Minimum result character length to trigger masking. Results shorter than this are kept as-is.
    /// Default: 200.
    /// </param>
    /// <param name="protectedTokens">
    /// Size budget, in tokens, for the most recent tool results, measured from the newest result back across turn
    /// boundaries. When set, the results that fit the budget stay whole and every older result is masked — even inside a
    /// protected turn — so one user message followed by many tool rounds (reading a long document, walking a folder) keeps
    /// only as much recent output at full size as the budget allows. Small results cost little, so a round of short
    /// results (a write that answers "ok") does not push out a larger result before it. The results of the newest round
    /// are always kept whole, even when they alone exceed the budget. The calls themselves stay, so the model still knows
    /// what it read. Default: <c>null</c> (off — only user turns protect). Requires <paramref name="tokenCounter"/>.
    /// </param>
    /// <param name="tokenCounter">Counts the tokens of each result against <paramref name="protectedTokens"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="protectedTokens"/> is set without a <paramref name="tokenCounter"/>.</exception>
    public ObservationMasker(
        int protectedTurns = 3, int minimumResultLength = 200, int? protectedTokens = null,
        IContextTokenCounter? tokenCounter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(protectedTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumResultLength);
        if (protectedTokens is { } tokens)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(tokens, nameof(protectedTokens));
            if (tokenCounter is null)
            {
                throw new ArgumentException(
                    "A token budget (protectedTokens) needs a token counter to measure results against it.", nameof(tokenCounter));
            }
        }

        _protectedTurns = protectedTurns;
        _minimumResultLength = minimumResultLength;
        _protectedTokens = protectedTokens;
        _tokenCounter = tokenCounter;
    }

    /// <summary>
    /// Masks old tool observations in the history.
    /// Results in the protected user turns — and, with a token budget, within the budget — are preserved.
    /// Other tool results of at least the minimum length are replaced with compact placeholders.
    /// </summary>
    /// <param name="history">The conversation history.</param>
    /// <returns>History with old observations masked, or the original history if nothing was masked.</returns>
    public IReadOnlyList<ChatMessage> MaskObservations(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count == 0)
        {
            return history;
        }

        var turnStartIndex = FindProtectedStartIndex(history);
        var overBudget = _protectedTokens is { } budget ? FindResultsOverBudget(history, budget) : null;
        if (turnStartIndex <= 0 && (overBudget is null || overBudget.Count == 0))
        {
            return history;
        }

        var calls = BuildCallMap(history);
        var result = new List<ChatMessage>(history.Count);
        var anyMasked = false;

        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];
            if (message.Role != ChatRole.Tool)
            {
                result.Add(message);
                continue;
            }

            var maskAll = i < turnStartIndex;
            var masked = MaskToolMessage(
                message, calls, frc => maskAll || (overBudget is not null && overBudget.Contains(frc)));
            result.Add(masked);
            anyMasked |= !ReferenceEquals(masked, message);
        }

        return anyMasked ? result : history;
    }

    /// <summary>
    /// Maps each call id to the call that carries it, from the FunctionCallContent in assistant messages.
    /// </summary>
    private static Dictionary<string, FunctionCallContent> BuildCallMap(IReadOnlyList<ChatMessage> history)
    {
        var map = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);

        foreach (var message in history)
        {
            if (message.Role != ChatRole.Assistant || message.Contents is null)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.CallId is not null)
                {
                    map[fcc.CallId] = fcc;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Finds the index where the protected region starts (counting user turns from the end).
    /// </summary>
    private int FindProtectedStartIndex(IReadOnlyList<ChatMessage> history)
    {
        // Special case: 0 protected turns means nothing is protected
        if (_protectedTurns <= 0)
        {
            return history.Count;
        }

        var turnsFound = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User)
            {
                turnsFound++;
                if (turnsFound >= _protectedTurns)
                {
                    return i;
                }
            }
        }

        // Fewer turns than threshold — protect everything
        return 0;
    }

    /// <summary>
    /// Walks the tool results from the newest back, summing their tokens. The first result that would take the sum past
    /// <paramref name="budget"/>, and every result before it, is over budget. The newest round's results (those after the
    /// last assistant message that calls tools) are always kept and counted. Because the walk starts from the end, a
    /// result that is over budget stays over budget as later rounds are added.
    /// </summary>
    private HashSet<FunctionResultContent> FindResultsOverBudget(IReadOnlyList<ChatMessage> history, int budget)
    {
        var over = new HashSet<FunctionResultContent>(ReferenceEqualityComparer.Instance);
        var newestRoundStart = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.Assistant && history[i].Contents.OfType<FunctionCallContent>().Any())
            {
                newestRoundStart = i;
                break;
            }
        }

        var used = 0;
        var exceeded = false;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role != ChatRole.Tool)
            {
                continue;
            }

            var contents = history[i].Contents;
            for (var c = contents.Count - 1; c >= 0; c--)
            {
                if (contents[c] is not FunctionResultContent frc)
                {
                    continue;
                }

                if (exceeded)
                {
                    over.Add(frc);
                    continue;
                }

                used += _tokenCounter!.CountTokens(ResultText(frc));
                if (used > budget && i < newestRoundStart)
                {
                    exceeded = true;
                    over.Add(frc);
                }
            }
        }

        return over;
    }

    /// <summary>
    /// Masks the results of a tool message that <paramref name="shouldMask"/> selects and that are at least the minimum
    /// length. Returns the message itself when nothing in it is masked.
    /// </summary>
    private ChatMessage MaskToolMessage(
        ChatMessage toolMessage, Dictionary<string, FunctionCallContent> calls, Func<FunctionResultContent, bool> shouldMask)
    {
        if (toolMessage.Contents is null || toolMessage.Contents.Count == 0)
        {
            return toolMessage;
        }

        List<AIContent>? maskedContents = null;
        for (var i = 0; i < toolMessage.Contents.Count; i++)
        {
            var content = toolMessage.Contents[i];
            if (content is FunctionResultContent frc && shouldMask(frc))
            {
                var resultText = ResultText(frc);
                if (resultText.Length >= _minimumResultLength)
                {
                    maskedContents ??= [.. toolMessage.Contents.Take(i)];
                    maskedContents.Add(new FunctionResultContent(frc.CallId, Placeholder(frc, resultText, calls)));
                    continue;
                }
            }

            maskedContents?.Add(content);
        }

        return maskedContents is null ? toolMessage : new ChatMessage(ChatRole.Tool, maskedContents);
    }

    private static string ResultText(FunctionResultContent frc) => ToolResultText.Of(frc.Result);

    /// <summary>
    /// The text that replaces a masked result: the call that produced it, how large it was, and that it can be fetched
    /// again with the same call.
    /// </summary>
    private static string Placeholder(
        FunctionResultContent frc, string resultText, Dictionary<string, FunctionCallContent> calls)
    {
        var call = frc.CallId is not null && calls.TryGetValue(frc.CallId, out var found) ? found : null;
        var toolName = call?.Name ?? "tool";
        var arguments = FormatArguments(call);
        var lineCount = resultText.Count(c => c == '\n') + 1;
        var invocation = arguments.Length > 0 ? $"{toolName} {arguments}" : toolName;
        var again = call is not null
            ? $"call {toolName} again with the same arguments if you need it"
            : "repeat the call that produced it if you need it";
        return string.Create(CultureInfo.InvariantCulture,
            $"[Masked: {invocation} result, {resultText.Length:N0} chars, ~{lineCount} lines. The content is no longer visible here; {again}.]");
    }

    private static string FormatArguments(FunctionCallContent? call)
    {
        if (call?.Arguments is not { Count: > 0 } arguments)
        {
            return string.Empty;
        }

        string text;
        try
        {
            text = JsonSerializer.Serialize(arguments, ArgumentsJson.GetTypeInfo(typeof(IDictionary<string, object?>)));
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return string.Empty;
        }

        return text.Length <= MaxArgumentsLength ? text : string.Concat(text.AsSpan(0, MaxArgumentsLength), "…");
    }
}
