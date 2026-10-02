using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Stops a model that keeps making the same successful call. When the conversation already holds
/// <see cref="ToolInvocationOptions.MaxRepeatedCalls"/> successful runs in a row of the same tool with identical
/// arguments, the next identical call is not run: the model receives a <see cref="ToolCallRefusal"/>
/// (<see cref="ToolCallRefusalKind.RepeatedCall"/>) telling it it already has that result. Identical calls keep being
/// refused until the model calls something else; any other call, failure or refusal in between ends the streak. A
/// result that reports a failure (<see cref="ToolInvocationOptions.FailureOf"/>, or an MCP <c>isError</c> result) is a
/// failure, not a successful run. The <see cref="ToolInvocationOptions.MaxRefusedRepeats"/>-th refusal in a row of the
/// same call also ends the request (<see cref="FunctionInvocationContext.Terminate"/>): the model is stuck, and asking
/// again would only spend the step budget.
/// </summary>
/// <remarks>
/// The streak is read from the conversation (<see cref="FunctionInvocationContext.Messages"/>), not kept in the
/// middleware, so it spans the rounds of one request and the turns of a loop, and one instance serves every
/// conversation.
/// </remarks>
public sealed partial class RepeatedCallGuardMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="options">Its threshold; defaults when null.</param>
    /// <param name="logger">Receives a line per refused call; optional.</param>
    public RepeatedCallGuardMiddleware(ToolInvocationOptions? options = null, ILogger<RepeatedCallGuardMiddleware>? logger = null)
    {
        _options = options ?? new ToolInvocationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var max = _options.MaxRepeatedCalls;
        if (max <= 0)
        {
            return next(context, cancellationToken);
        }

        var name = context.Function.Name;
        var key = ToolCallHistory.Key(name, context.CallContent?.Arguments ?? context.Arguments);
        var completed = ToolCallHistory.Completed(context.Messages, context.CallContent?.CallId);

        var streak = 0;
        var refused = 0;
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (call, result) = completed[i];
            if (ToolCallHistory.Key(call.Name, call.Arguments) != key)
            {
                break;
            }

            if (result.Result is ToolCallRefusal { Kind: ToolCallRefusalKind.RepeatedCall })
            {
                if (streak == 0)
                {
                    refused++; // refusals at the end of the history: how long the model has been stuck
                }

                continue; // already refused for repeating: the streak stands until the model changes course
            }

            if (result.Exception is not null || result.Result is ToolCallRefusal || ToolResultFailure.Of(result.Result, _options) is not null)
            {
                break;
            }

            streak++;
        }

        if (streak < max)
        {
            return next(context, cancellationToken);
        }

        if (_options.MaxRefusedRepeats > 0 && refused + 1 >= _options.MaxRefusedRepeats)
        {
            context.Terminate = true;
            LogStuck(_logger, name, refused + 1);
            return new ValueTask<object?>(new ToolCallRefusal(
                ToolCallRefusalKind.RepeatedCall,
                $"'{name}' was called again with the same arguments after being refused {refused} time(s); the request was " +
                $"stopped as stuck repeating '{name}'."));
        }

        LogRefused(_logger, name, streak);
        return new ValueTask<object?>(new ToolCallRefusal(
            ToolCallRefusalKind.RepeatedCall,
            $"'{name}' already ran {streak} times in a row with these same arguments. Its result is in the conversation; " +
            "do not call it again with these arguments — use that result, or call a different tool or change the arguments."));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} refused for repeating {Count} times in a row; ending the request as stuck")]
    private static partial void LogStuck(ILogger logger, string tool, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} not run: the same call already succeeded {Count} times in a row")]
    private static partial void LogRefused(ILogger logger, string tool, int count);
}
