using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Why a turn ended, when it ended without an error. A turn that fails — an exception from the model or a tool, a usage
/// limit, cancellation, or <see cref="AgentOptions.MaxTurnDuration"/> passing — throws instead.
/// </summary>
public enum TurnStopReason
{
    /// <summary>The model finished its answer.</summary>
    Completed = 0,

    /// <summary>The model stopped at its output-token limit; the answer is cut off.</summary>
    OutputLimit = 1,

    /// <summary>The provider's content filter stopped the answer.</summary>
    ContentFilter = 2,

    /// <summary>
    /// A tool step ended the request (<c>FunctionInvocationContext.Terminate</c> — for example the repeated-error guard);
    /// the turn ends on that tool's result without a final answer.
    /// </summary>
    ToolTerminated = 3,

    /// <summary>
    /// The model called tools the host runs itself (declaration-only tools); supply their results and call
    /// <c>ContinueAsync</c>.
    /// </summary>
    AwaitingHostTools = 4,

    /// <summary>
    /// The model was still calling tools when the function-invoking client's iteration limit was reached; the calls of
    /// the last round were not run.
    /// </summary>
    StepLimit = 5,
}

internal static class TurnStopReasons
{
    /// <summary>Classifies a turn from the messages it added to the history and the model's last finish reason.</summary>
    public static TurnStopReason Classify(IEnumerable<ChatMessage> messages, ChatFinishReason? finishReason, IList<AITool>? tools)
    {
        var turnMessages = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var last = turnMessages.Count > 0 ? turnMessages[^1] : null;
        if (last is not null && last.Role == ChatRole.Tool)
        {
            return TurnStopReason.ToolTerminated;
        }

        var answered = turnMessages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var pending = turnMessages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Where(c => !answered.Contains(c.CallId)).ToList();
        if (pending.Count > 0)
        {
            var hostRun = (tools ?? [])
                .Where(t => t is AIFunctionDeclaration and not AIFunction)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);
            return pending.All(c => hostRun.Contains(c.Name)) ? TurnStopReason.AwaitingHostTools : TurnStopReason.StepLimit;
        }

        if (finishReason == ChatFinishReason.Length)
        {
            return TurnStopReason.OutputLimit;
        }

        return finishReason == ChatFinishReason.ContentFilter ? TurnStopReason.ContentFilter : TurnStopReason.Completed;
    }
}
