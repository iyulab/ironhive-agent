using System.Text.Json;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Refuses a call whose arguments cannot run the tool, before anything else judges it: arguments that could not be parsed
/// from the model's response (<see cref="FunctionCallContent.Exception"/> is set), or arguments that leave out a parameter
/// the tool's schema marks required. A tool never runs on partial or empty arguments, and the model receives a
/// <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.InvalidArguments"/>) naming what is wrong so it can
/// re-issue the call. Turned off by <see cref="ToolInvocationOptions.RefuseInvalidArguments"/>.
/// </summary>
/// <remarks>
/// It belongs first in a pipeline, ahead of the permission gate: a permission rule cannot judge a call that does not say
/// what it targets (a write without a path), so such a call would otherwise reach the gate as «ask the operator» and, with
/// nobody to ask, come back to the model as an approval refusal instead of «path is required».
/// </remarks>
public sealed partial class InvalidArgumentsMiddleware : IToolInvocationMiddleware
{
    private readonly ToolInvocationOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="options">Whether it refuses; defaults when null.</param>
    /// <param name="logger">Receives a line per refused call; optional.</param>
    public InvalidArgumentsMiddleware(ToolInvocationOptions? options = null, ILogger<InvalidArgumentsMiddleware>? logger = null)
    {
        _options = options ?? new ToolInvocationOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!_options.RefuseInvalidArguments)
        {
            return next(context, cancellationToken);
        }

        if (context.CallContent?.Exception is { } parseError)
        {
            LogUnparseable(_logger, context.Function.Name, parseError.GetType().Name);
            return new ValueTask<object?>(new ToolCallRefusal(
                ToolCallRefusalKind.InvalidArguments,
                $"its arguments could not be parsed ({parseError.GetType().Name}: {parseError.Message}). Re-issue the call with complete arguments that are valid JSON matching the tool's schema."));
        }

        var missing = MissingRequired(context.Function.JsonSchema, context.Arguments);
        if (missing.Count > 0)
        {
            LogMissing(_logger, context.Function.Name, missing.Count);
            var names = string.Join(", ", missing.Select(n => $"'{n}'"));
            return new ValueTask<object?>(new ToolCallRefusal(
                ToolCallRefusalKind.InvalidArguments,
                $"{context.Function.Name} needs {names}, which the call did not include. Re-issue the call with every required argument."));
        }

        return next(context, cancellationToken);
    }

    // The schema's «required» names that the call's arguments leave out. The arguments dictionary's own comparer decides a
    // match, which is how the tool's parameters are bound when it runs.
    private static List<string> MissingRequired(JsonElement schema, AIFunctionArguments arguments)
    {
        var missing = new List<string>();
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return missing;
        }

        foreach (var name in required.EnumerateArray())
        {
            if (name.ValueKind == JsonValueKind.String && name.GetString() is { } parameter && !arguments.ContainsKey(parameter))
            {
                missing.Add(parameter);
            }
        }

        return missing;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} not run: its arguments could not be parsed ({ErrorType})")]
    private static partial void LogUnparseable(ILogger logger, string tool, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} not run: {Count} required argument(s) missing")]
    private static partial void LogMissing(ILogger logger, string tool, int count);
}
