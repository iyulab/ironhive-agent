using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Ends the request when a tool keeps failing the same way. A failure is a call that throws, a call stopped at its time
/// limit (<see cref="ToolCallRefusalKind.TimedOut"/>), or a result that reports one — an MCP result with
/// <c>isError: true</c>, or whatever <see cref="ToolInvocationOptions.FailureOf"/> recognises.
/// When the conversation shows the same tool failing with the same error (exception type and message, or the reported
/// error text) immediately before it, so that the streak reaches <see cref="ToolInvocationOptions.MaxRepeatedErrors"/>,
/// the guard sets <see cref="FunctionInvocationContext.Terminate"/> and returns a <see cref="ToolCallRefusal"/>
/// (<see cref="ToolCallRefusalKind.RepeatedError"/>) as the call's result instead of the failure. Below the threshold
/// the failure passes through unchanged, so the model sees it and can correct itself.
/// </summary>
/// <remarks>
/// The streak is read from the conversation (earlier <see cref="FunctionResultContent"/>s), not kept in the middleware.
/// A thrown exception is only visible there while the conversation is held in memory — a conversation restored from
/// storage starts a new streak for it; a failure carried in the result is read from the result itself.
/// </remarks>
public sealed partial class RepeatedErrorGuardMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="options">Its threshold and failure recognition; defaults when null.</param>
    /// <param name="logger">Receives a line when it ends a request; optional.</param>
    public RepeatedErrorGuardMiddleware(ToolInvocationOptions? options = null, ILogger<RepeatedErrorGuardMiddleware>? logger = null)
    {
        _options = options ?? new ToolInvocationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        object? result;
        try
        {
            result = await next(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _options.MaxRepeatedErrors > 0)
        {
            var refusal = RefuseIfRepeated(context, Failure.Thrown(ex));
            if (refusal is null)
            {
                throw;
            }

            return refusal;
        }

        if (_options.MaxRepeatedErrors <= 0 || FailureOf(result) is not { } failure)
        {
            return result;
        }

        return RefuseIfRepeated(context, failure) ?? result;
    }

    private ToolCallRefusal? RefuseIfRepeated(FunctionInvocationContext context, Failure failure)
    {
        var name = context.Function.Name;
        var streak = 1 + PrecedingSameFailures(context, name, failure);
        if (streak < _options.MaxRepeatedErrors)
        {
            return null;
        }

        context.Terminate = true;
        LogTerminated(_logger, name, streak, failure.ShortKind);
        return new ToolCallRefusal(
            ToolCallRefusalKind.RepeatedError,
            $"'{name}' failed with the same error {streak} times in a row ({failure.Describe()}); the request was stopped.");
    }

    private int PrecedingSameFailures(FunctionInvocationContext context, string name, Failure failure)
    {
        var completed = ToolCallHistory.Completed(context.Messages, context.CallContent?.CallId);
        var count = 0;
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (call, result) = completed[i];
            if (call.Name != name || FailureOf(result) != failure)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private Failure? FailureOf(FunctionResultContent result) =>
        result.Exception is { } ex ? Failure.Thrown(ex) : FailureOf(result.Result);

    private Failure? FailureOf(object? result) =>
        result is ToolCallRefusal { Kind: ToolCallRefusalKind.TimedOut } timedOut ? Failure.TimedOut(timedOut.Reason)
        : ToolResultFailure.Of(result, _options) is { } reported ? Failure.Reported(reported)
        : null;

    /// <summary>What makes two failures "the same": the exception type and message, or the reported error text.</summary>
    private sealed record Failure(string Kind, string Message)
    {
        private const string ReportedKind = "reported";

        public static Failure Thrown(Exception ex) => new(ex.GetType().FullName ?? ex.GetType().Name, ex.Message);

        public static Failure Reported(string text) => new(ReportedKind, text);

        public static Failure TimedOut(string reason) => new("timeout", reason);

        public string ShortKind => Kind[(Kind.LastIndexOf('.') + 1)..];

        public string Describe() => $"{ShortKind}: {Message}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} failed with the same error {Count} times in a row ({ErrorType}); ending the request")]
    private static partial void LogTerminated(ILogger logger, string tool, int count, string errorType);
}
