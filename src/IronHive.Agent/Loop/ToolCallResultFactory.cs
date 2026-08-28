using System.Text.Json;
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
/// <see cref="FunctionCallContent.CallId"/>; when no matching result exists, the outcome is
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

        var resultsByCallId = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ToDictionary(r => r.CallId, r => r);

        var results = new List<ToolCallResult>();

        foreach (var message in response.Messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
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
                        ToolName = call.Name,
                        Arguments = arguments,
                        Result = call.Exception.Message,
                        Success = false
                    });
                    continue;
                }

                if (resultsByCallId.TryGetValue(call.CallId, out var functionResult))
                {
                    results.Add(new ToolCallResult
                    {
                        ToolName = call.Name,
                        Arguments = arguments,
                        Result = functionResult.Result?.ToString() ?? string.Empty,
                        Success = functionResult.Exception is null
                    });
                    continue;
                }

                // No function-invocation middleware resolved this call within this response --
                // the outcome is unknown, not successful.
                results.Add(new ToolCallResult
                {
                    ToolName = call.Name,
                    Arguments = arguments,
                    Result = string.Empty,
                    Success = null
                });
            }
        }

        return results;
    }
}
