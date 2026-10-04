using System.Text.Json;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Ends a request whose model keeps coming back for content it already has. When a call returns the same result as the
/// same tool with identical arguments (or the same target, for a tool that declares target arguments —
/// <see cref="ToolInvocationHints"/>) returned on <see cref="ToolInvocationOptions.MaxRepeatedResults"/> − 1 earlier
/// visits — each visit separated from the last by other calls — the request ends
/// (<see cref="FunctionInvocationContext.Terminate"/>) on a <see cref="ToolCallRefusal"/>
/// (<see cref="ToolCallRefusalKind.RepeatedResult"/>) that says why.
/// </summary>
/// <remarks>
/// <para>
/// The typical cause is a working set larger than the context budget: with observation masking on
/// (<c>CompactionConfig.ObservationMaskingProtectedTokens</c>), each new read pushes an older one out, the placeholder
/// tells the model to call again, and the model rotates through the same reads until the step limit — or stops reading
/// and writes from memory. Masking only rewrites the request sent to the model, so this guard cannot ask whether a
/// result was masked; it counts the observable symptom instead, which also covers a model that simply forgot.
/// </para>
/// <para>
/// Consecutive identical calls are one visit — <see cref="RepeatedCallGuardMiddleware"/> handles those. A re-read whose
/// result changed (the file was edited in between) does not count, nor does a failure or a refusal. A tool declared not
/// read-only (<see cref="ToolInvocationHints.IsReadOnly"/> is <c>false</c> — a command, a write) is not guarded: running
/// it again after other calls is not a re-read, and the same output (a check that still fails after an edit) is
/// information. The history is read from <see cref="FunctionInvocationContext.Messages"/>, so one instance serves every
/// conversation.
/// </para>
/// </remarks>
public sealed partial class RepeatedResultGuardMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="options">Its threshold; defaults when null.</param>
    /// <param name="logger">Receives a line when a request is ended; optional.</param>
    public RepeatedResultGuardMiddleware(ToolInvocationOptions? options = null, ILogger<RepeatedResultGuardMiddleware>? logger = null)
    {
        _options = options ?? new ToolInvocationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var max = ToolInvocationHints.IsReadOnly(context.Function) == false ? 0 : _options.MaxRepeatedResults;
        var name = context.Function.Name;
        var keyOf = ToolCallHistory.KeyFor(context.Function, out _);
        var key = keyOf(name, context.CallContent?.Arguments ?? context.Arguments);
        var completed = max > 0
            ? ToolCallHistory.Completed(context.Messages, context.CallContent?.CallId)
            : [];

        // Nothing to compare against, or the call continues a run of the same call (one visit): just run it.
        if (completed.Count == 0
            || keyOf(completed[^1].Call.Name, completed[^1].Call.Arguments) == key
            || !completed.Any(c => keyOf(c.Call.Name, c.Call.Arguments) == key))
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }

        var result = await next(context, cancellationToken).ConfigureAwait(false);
        if (result is ToolCallRefusal || ToolResultFailure.Of(result, _options) is not null)
        {
            return result;
        }

        var text = ToolCallHistory.ResultText(result);
        var visits = 1; // this one
        string? previousKey = null;
        var visitCounted = false;
        foreach (var (call, earlier) in completed)
        {
            var callKey = keyOf(call.Name, call.Arguments);
            if (callKey != previousKey)
            {
                visitCounted = false; // a new visit starts
            }

            if (callKey == key && !visitCounted && IsSameSuccess(earlier, text))
            {
                visits++;
                visitCounted = true;
            }

            previousKey = callKey;
        }

        if (visits < max)
        {
            return result;
        }

        context.Terminate = true;
        LogEnded(_logger, name, visits);
        return new ToolCallRefusal(
            ToolCallRefusalKind.RepeatedResult,
            $"'{name}' returned this same result {visits} times in this request, each time after other calls. If earlier " +
            "results were masked, what this task needs at once does not fit the context budget (raise " +
            "ObservationMaskingProtectedTokens, or split the task); the request was stopped here rather than continue " +
            "without it.");
    }

    private bool IsSameSuccess(FunctionResultContent earlier, string text) =>
        earlier.Exception is null
        && earlier.Result is not ToolCallRefusal
        && ToolResultFailure.Of(earlier.Result, _options) is null
        && string.Equals(ToolCallHistory.ResultText(earlier.Result), text, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} returned the same result on {Count} separate visits; ending the request")]
    private static partial void LogEnded(ILogger logger, string tool, int count);
}
