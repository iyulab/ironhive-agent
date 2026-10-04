using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Stops a model that keeps making the same successful call. When the conversation already holds
/// <see cref="ToolInvocationOptions.MaxRepeatedCalls"/> successful runs in a row of the same tool with identical
/// arguments — or, for a tool that declares target arguments (<see cref="ToolInvocationHints"/>), with the same target
/// whatever its other arguments — the next such call is not run: the model receives a <see cref="ToolCallRefusal"/>
/// (<see cref="ToolCallRefusalKind.RepeatedCall"/>) telling it it already has that result. Identical calls keep being
/// refused until the model calls something else; any other call, failure or refusal in between ends the streak. A
/// result that reports a failure (<see cref="ToolInvocationOptions.FailureOf"/>, or an MCP <c>isError</c> result) is a
/// failure, not a successful run. The <see cref="ToolInvocationOptions.MaxRefusedRepeats"/>-th refusal in a row of the
/// same call also ends the request (<see cref="FunctionInvocationContext.Terminate"/>): the model is stuck, and asking
/// again would only spend the step budget.
/// <para>
/// For a tool without target arguments a run counts toward the streak only when it returned what the run after it
/// returned (the text the model sees): repeating a call and getting the same answer is repetition, getting a different
/// one is progress — a status poll that moves, a download that advances. A tool with target arguments keeps counting
/// whatever it returns, since varying the other arguments is what makes its answers differ (a re-described image).
/// The cost of that line: a tool whose answer differs on every call (a clock, a random sample) is no longer stopped by
/// this guard and runs until the loop's step budget.
/// </para>
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
        var keyOf = ToolCallHistory.KeyFor(context.Function, out var targets);
        var key = keyOf(name, context.CallContent?.Arguments ?? context.Arguments);
        var completed = ToolCallHistory.Completed(context.Messages, context.CallContent?.CallId);

        var streak = 0;
        var refused = 0;
        string? laterText = null;
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (call, result) = completed[i];
            if (keyOf(call.Name, call.Arguments) != key)
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

            if (targets.Count == 0)
            {
                var text = ToolCallHistory.ResultText(result.Result);
                if (laterText is not null && !string.Equals(text, laterText, StringComparison.Ordinal))
                {
                    break; // the answer changed between these two runs: progress, not repetition
                }

                laterText = text;
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
                $"'{name}' was called again {Same(targets)} after being refused {refused} time(s); the request was " +
                $"stopped as stuck repeating '{name}'."));
        }

        LogRefused(_logger, name, streak);
        return new ValueTask<object?>(new ToolCallRefusal(
            ToolCallRefusalKind.RepeatedCall,
            targets.Count == 0
                ? $"'{name}' already ran {streak} times in a row with these same arguments. Its result is in the conversation; " +
                  "do not call it again with these arguments — use that result, or call a different tool or change the arguments."
                : $"'{name}' already ran {streak} times in a row {Same(targets)}, whatever the other arguments. Its results are in " +
                  $"the conversation; do not call it again {Same(targets)} — use those results, or move on to a different " +
                  $"{string.Join(" or ", targets)} or another tool."));
    }

    /// <summary>"with the same arguments", or "on the same path" (and "… and page") for a tool with target arguments.</summary>
    private static string Same(IReadOnlyList<string> targets) =>
        targets.Count == 0 ? "with the same arguments" : "on the same " + string.Join(" and ", targets);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} refused for repeating {Count} times in a row; ending the request as stuck")]
    private static partial void LogStuck(ILogger logger, string tool, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} not run: the same call already succeeded {Count} times in a row")]
    private static partial void LogRefused(ILogger logger, string tool, int count);
}
