using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Ends the request when a tool keeps failing the same way. When a call throws and the conversation shows the same tool
/// failing with the same error (exception type and message) immediately before it, so that the streak reaches
/// <see cref="ToolInvocationOptions.MaxRepeatedErrors"/>, the guard sets <see cref="FunctionInvocationContext.Terminate"/>
/// and returns a <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.RepeatedError"/>) as the call's result
/// instead of the exception. Below the threshold the exception passes through unchanged, so the model sees the failure
/// and can correct itself.
/// </summary>
/// <remarks>
/// The streak is read from the conversation (<see cref="FunctionResultContent.Exception"/> of earlier results), not kept
/// in the middleware. An exception is only visible there while the conversation is held in memory — a conversation
/// restored from storage starts a new streak.
/// </remarks>
public sealed partial class RepeatedErrorGuardMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the guard.</summary>
    /// <param name="options">Its threshold; defaults when null.</param>
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

        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _options.MaxRepeatedErrors > 0)
        {
            var name = context.Function.Name;
            var streak = 1 + PrecedingSameErrors(context, name, ex);
            if (streak < _options.MaxRepeatedErrors)
            {
                throw;
            }

            context.Terminate = true;
            LogTerminated(_logger, name, streak, ex.GetType().Name);
            return new ToolCallRefusal(
                ToolCallRefusalKind.RepeatedError,
                $"'{name}' failed with the same error {streak} times in a row ({ex.GetType().Name}: {ex.Message}); the request was stopped.");
        }
    }

    private static int PrecedingSameErrors(FunctionInvocationContext context, string name, Exception error)
    {
        var completed = ToolCallHistory.Completed(context.Messages, context.CallContent?.CallId);
        var count = 0;
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var (call, result) = completed[i];
            if (call.Name != name
                || result.Exception is not { } previous
                || previous.GetType() != error.GetType()
                || previous.Message != error.Message)
            {
                break;
            }

            count++;
        }

        return count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} failed with the same error {Count} times in a row ({ErrorType}); ending the request")]
    private static partial void LogTerminated(ILogger logger, string tool, int count, string errorType);
}
