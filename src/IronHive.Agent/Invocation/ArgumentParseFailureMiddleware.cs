using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Refuses a call whose arguments could not be parsed from the model's response
/// (<see cref="FunctionCallContent.Exception"/> is set), so a tool never runs on partial or empty arguments. The model
/// receives a <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.InvalidArguments"/>) naming the parse
/// error and can re-issue the call. Turned off by <see cref="ToolInvocationOptions.RefuseUnparseableArguments"/>.
/// </summary>
public sealed partial class ArgumentParseFailureMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="options">Whether it refuses; defaults when null.</param>
    /// <param name="logger">Receives a line per refused call; optional.</param>
    public ArgumentParseFailureMiddleware(ToolInvocationOptions? options = null, ILogger<ArgumentParseFailureMiddleware>? logger = null)
    {
        _options = options ?? new ToolInvocationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!_options.RefuseUnparseableArguments || context.CallContent?.Exception is not { } parseError)
        {
            return next(context, cancellationToken);
        }

        LogRefused(_logger, context.Function.Name, parseError.GetType().Name);
        return new ValueTask<object?>(new ToolCallRefusal(
            ToolCallRefusalKind.InvalidArguments,
            $"{parseError.GetType().Name}: {parseError.Message}. Re-issue the call with complete arguments that are valid JSON matching the tool's schema."));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} not run: its arguments could not be parsed ({ErrorType})")]
    private static partial void LogRefused(ILogger logger, string tool, string errorType);
}
