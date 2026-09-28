using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Checks that a loop's history can be continued without a new user message: it must end with the
/// caller's tool results answering every call of the assistant message before them, or with a user
/// message (a restored conversation). Anything else would send the model a request it cannot answer —
/// providers reject a tool call left without a result, and a history ending in the model's own answer
/// has nothing to continue from.
/// </summary>
internal static class ContinuationGuard
{
    public static void EnsureContinuable(IReadOnlyList<ChatMessage> history)
    {
        var last = history.Count - 1;
        if (last < 0)
        {
            throw new InvalidOperationException(
                "The history is empty, so there is nothing to continue. Start the conversation with RunAsync.");
        }

        if (history[last].Role == ChatRole.User)
        {
            return;
        }

        if (history[last].Role != ChatRole.Tool)
        {
            throw new InvalidOperationException(
                $"The history ends with a '{history[last].Role}' message, so there is nothing to continue. " +
                "Continue only after appending the tool results for the pending calls (a Tool message with " +
                "FunctionResultContent), or start a new turn with RunAsync.");
        }

        // The trailing tool messages answer the assistant message right before them.
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var index = last;
        for (; index >= 0 && history[index].Role == ChatRole.Tool; index--)
        {
            foreach (var result in history[index].Contents.OfType<FunctionResultContent>())
            {
                answered.Add(result.CallId);
            }
        }

        if (index < 0 || history[index].Role != ChatRole.Assistant)
        {
            throw new InvalidOperationException(
                "The history ends with tool results that follow no assistant tool call.");
        }

        var unanswered = history[index].Contents.OfType<FunctionCallContent>()
            .Select(call => call.CallId)
            .Where(callId => !answered.Contains(callId))
            .ToList();
        if (unanswered.Count > 0)
        {
            throw new InvalidOperationException(
                $"The history has tool calls without results: {string.Join(", ", unanswered)}. " +
                "Append a FunctionResultContent for every pending call before continuing.");
        }
    }
}
