using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Masks old tool observation results with compact placeholders to reduce context window usage.
/// Protects recent user turns from masking, only replacing older tool results.
/// </summary>
public class ObservationMasker
{
    private readonly int _protectedTurns;
    private readonly int _minimumResultLength;

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
    public ObservationMasker(int protectedTurns = 3, int minimumResultLength = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(protectedTurns);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumResultLength);

        _protectedTurns = protectedTurns;
        _minimumResultLength = minimumResultLength;
    }

    /// <summary>
    /// Masks old tool observations in the history.
    /// Recent turns (defined by <see cref="_protectedTurns"/>) are preserved.
    /// Older tool results exceeding <see cref="_minimumResultLength"/> are replaced with compact placeholders.
    /// </summary>
    /// <param name="history">The conversation history.</param>
    /// <returns>History with old observations masked, or the original history if nothing was masked.</returns>
    public IReadOnlyList<ChatMessage> MaskObservations(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count == 0)
        {
            return history;
        }

        // Build callId → tool name mapping from FunctionCallContent in assistant messages
        var toolNameMap = BuildToolNameMap(history);

        // Find the boundary index where protection starts
        var protectedStartIndex = FindProtectedStartIndex(history);

        // If everything is protected, return as-is
        if (protectedStartIndex <= 0)
        {
            return history;
        }

        // Mask old tool results
        var result = new List<ChatMessage>(history.Count);
        var anyMasked = false;

        for (var i = 0; i < history.Count; i++)
        {
            if (i >= protectedStartIndex || history[i].Role != ChatRole.Tool)
            {
                result.Add(history[i]);
            }
            else
            {
                var masked = MaskToolMessage(history[i], toolNameMap);
                result.Add(masked);
                if (!ReferenceEquals(masked, history[i]))
                {
                    anyMasked = true;
                }
            }
        }

        return anyMasked ? result : history;
    }

    /// <summary>
    /// Builds a mapping from CallId to tool name by scanning FunctionCallContent in assistant messages.
    /// </summary>
    private static Dictionary<string, string> BuildToolNameMap(IReadOnlyList<ChatMessage> history)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

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
                    map[fcc.CallId] = fcc.Name;
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
    /// Masks a single tool message by replacing large FunctionResultContent with placeholders.
    /// </summary>
    private ChatMessage MaskToolMessage(ChatMessage toolMessage, Dictionary<string, string> toolNameMap)
    {
        if (toolMessage.Contents is null || toolMessage.Contents.Count == 0)
        {
            return toolMessage;
        }

        // Check if any result needs masking
        var needsMasking = false;
        foreach (var content in toolMessage.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var resultText = frc.Result?.ToString() ?? string.Empty;
                if (resultText.Length >= _minimumResultLength)
                {
                    needsMasking = true;
                    break;
                }
            }
        }

        if (!needsMasking)
        {
            return toolMessage;
        }

        // Create masked contents
        var maskedContents = new List<AIContent>(toolMessage.Contents.Count);

        foreach (var content in toolMessage.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var resultText = frc.Result?.ToString() ?? string.Empty;
                if (resultText.Length >= _minimumResultLength)
                {
                    var toolName = ResolveToolName(frc, toolNameMap);
                    var lineCount = resultText.Count(c => c == '\n') + 1;
                    var placeholder = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[Masked: {toolName} result, {resultText.Length:N0} chars, ~{lineCount} lines]");
                    maskedContents.Add(new FunctionResultContent(frc.CallId, placeholder));
                }
                else
                {
                    maskedContents.Add(content);
                }
            }
            else
            {
                maskedContents.Add(content);
            }
        }

        return new ChatMessage(ChatRole.Tool, maskedContents);
    }

    /// <summary>
    /// Resolves the tool name from the callId→name mapping built from FunctionCallContent.
    /// </summary>
    private static string ResolveToolName(FunctionResultContent frc, Dictionary<string, string> toolNameMap)
    {
        if (frc.CallId is not null && toolNameMap.TryGetValue(frc.CallId, out var name))
        {
            return name;
        }

        return "tool";
    }
}
