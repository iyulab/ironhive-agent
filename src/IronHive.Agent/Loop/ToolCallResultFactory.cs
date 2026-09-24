using System.Text.Json;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Canonical factory for building <see cref="ToolCallResult"/> instances from a
/// <see cref="ChatResponse"/>. Centralising this logic ensures every <see cref="IAgentLoop"/>
/// implementation reports the same, honest outcome for each <see cref="FunctionCallContent"/> the
/// model requested.
/// </summary>
/// <remarks>
/// An <see cref="IAgentLoop"/> only extracts <see cref="FunctionCallContent"/> from the model's
/// response — it never invokes a tool itself. Whether a call was actually executed, and what
/// happened, is knowable only when the <c>IChatClient</c> passed to the loop was wrapped with
/// Microsoft.Extensions.AI's function-invocation middleware (<c>UseFunctionInvocation()</c>): in
/// that case the middleware appends a matching <see cref="FunctionResultContent"/> to the same
/// response before returning it. This factory correlates the two by
/// <c>CallId</c>; when no matching result exists, the outcome is
/// genuinely unknown and <see cref="ToolCallResult.Success"/> is <c>null</c> rather than a
/// hardcoded guess.
/// </remarks>
public static class ToolCallResultFactory
{
    /// <summary>
    /// Extracts one <see cref="ToolCallResult"/> per <see cref="FunctionCallContent"/> found across
    /// all messages in <paramref name="response"/>.
    /// </summary>
    public static List<ToolCallResult> Extract(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return Extract(
            response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()),
            response.Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()));
    }

    /// <summary>
    /// Extracts one <see cref="ToolCallResult"/> per call, correlating each against
    /// <paramref name="functionResults"/> by <c>CallId</c>.
    /// </summary>
    /// <remarks>
    /// The streaming path never has a <see cref="ChatResponse"/> to hand — it sees the same two
    /// content types arrive as separate updates. It correlates them by this overload rather than by a
    /// second copy of the rule, so a streamed turn and a non-streamed turn cannot report the same
    /// call differently.
    /// </remarks>
    public static List<ToolCallResult> Extract(
        IEnumerable<FunctionCallContent> functionCalls,
        IEnumerable<FunctionResultContent> functionResults)
    {
        ArgumentNullException.ThrowIfNull(functionCalls);
        ArgumentNullException.ThrowIfNull(functionResults);

        var resultsByCallId = new Dictionary<string, FunctionResultContent>();
        foreach (var functionResult in functionResults)
        {
            resultsByCallId[functionResult.CallId] = functionResult;
        }

        var results = new List<ToolCallResult>();

        foreach (var call in functionCalls)
        {
            var arguments = call.Arguments is not null
                ? JsonSerializer.Serialize(call.Arguments)
                : "{}";

            if (call.Exception is not null)
            {
                // The provider produced a call Microsoft.Extensions.AI could not parse
                // (e.g. malformed arguments) -- it never reaches an invoker.
                results.Add(new ToolCallResult
                {
                    CallId = call.CallId,
                    ToolName = call.Name,
                    Arguments = arguments,
                    Result = call.Exception.Message,
                    Success = false
                });
                continue;
            }

            if (resultsByCallId.TryGetValue(call.CallId, out var functionResult))
            {
                // A refusal from the permission gate is a result the model reads, not an outcome the
                // tool produced: the tool did not run, and this record must say so.
                var refused = functionResult.Result is ToolCallRefusal;
                results.Add(new ToolCallResult
                {
                    CallId = call.CallId,
                    ToolName = call.Name,
                    Arguments = arguments,
                    Result = functionResult.Result?.ToString() ?? string.Empty,
                    Success = functionResult.Exception is null && !refused
                });
                continue;
            }

            // No function-invocation middleware resolved this call within this response --
            // the outcome is unknown, not successful.
            results.Add(new ToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = arguments,
                Result = string.Empty,
                Success = null
            });
        }

        return results;
    }

    /// <summary>
    /// The outcome of each call in <paramref name="functionResults"/> whose call is among
    /// <paramref name="functionCallsSoFar"/>, by the same rule as <see cref="Extract(IEnumerable{FunctionCallContent}, IEnumerable{FunctionResultContent})"/>.
    /// The streaming loops use it to report each call the moment its result arrives, so the per-call
    /// chunk and the final turn record cannot disagree about the same call.
    /// </summary>
    public static IEnumerable<ToolCallResult> ForArrivedResults(
        IReadOnlyList<FunctionCallContent> functionCallsSoFar,
        IEnumerable<FunctionResultContent> functionResults)
    {
        ArgumentNullException.ThrowIfNull(functionCallsSoFar);
        ArgumentNullException.ThrowIfNull(functionResults);

        foreach (var result in functionResults)
        {
            var call = functionCallsSoFar.LastOrDefault(c => c.CallId == result.CallId);
            if (call is null)
            {
                continue; // a result for a call this turn never announced has nothing to correlate with
            }

            yield return Extract([call], [result])[0];
        }
    }
}
