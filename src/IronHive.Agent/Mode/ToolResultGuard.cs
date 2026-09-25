using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Mode;

/// <summary>A tool's result, as the guard sees it before the model does.</summary>
/// <param name="ToolName">The tool that produced it.</param>
/// <param name="Arguments">The arguments it ran with.</param>
/// <param name="Result">The result as text — a string result as-is, anything else as JSON.</param>
public sealed record ToolResultInspection(string ToolName, IReadOnlyDictionary<string, object?>? Arguments, string Result);

/// <summary>What an <see cref="IToolResultGuard"/> decided about a tool's result.</summary>
public sealed record ToolResultVerdict
{
    private ToolResultVerdict(bool withheld, string? reason, string? replacement)
    {
        Withheld = withheld;
        Reason = reason;
        Replacement = replacement;
    }

    /// <summary>The result is withheld; the model receives a refusal with <see cref="Reason"/> instead.</summary>
    public bool Withheld { get; }

    /// <summary>Why the result was withheld.</summary>
    public string? Reason { get; }

    /// <summary>A sanitized text the model receives instead of the original result; null to pass the original.</summary>
    public string? Replacement { get; }

    /// <summary>Pass the result to the model unchanged.</summary>
    public static ToolResultVerdict Allow() => new(false, null, null);

    /// <summary>Pass <paramref name="sanitized"/> to the model instead of the result.</summary>
    public static ToolResultVerdict Replace(string sanitized) => new(false, null, sanitized ?? throw new ArgumentNullException(nameof(sanitized)));

    /// <summary>Withhold the result; the model reads a refusal carrying <paramref name="reason"/>.</summary>
    public static ToolResultVerdict Withhold(string reason) => new(true, reason, null);
}

/// <summary>
/// Inspects what an in-process tool returned before the model reads it — the prompt-injection seam the MCP path has
/// (<see cref="Mcp.McpPluginManager"/> with an <see cref="Mcp.IMcpToolCallGuard"/>), for tools that run in-process (a web page's text,
/// a file's contents). Installed with <see cref="ToolResultGuardedFunctionInvoker"/> on a function-invoking client,
/// or passed to the Ironbees adapter. Like the MCP guard it is fail-closed: a guard that throws withholds the result.
/// </summary>
public interface IToolResultGuard
{
    /// <summary>Decides what the model receives for <paramref name="inspection"/>.</summary>
    ValueTask<ToolResultVerdict> InspectAsync(ToolResultInspection inspection, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the <c>FunctionInvoker</c> that runs each tool and puts its result through an <see cref="IToolResultGuard"/>
/// before the model reads it. Composes with the permission gate — the gate decides whether a call runs, this decides
/// what its result becomes:
/// <code>
/// .UseFunctionInvocation(configure: c =&gt; c.FunctionInvoker = ApprovalGatedFunctionInvoker.Create(
///     modeToolFilter, approvalService, inner: ToolResultGuardedFunctionInvoker.Create(guard)))
/// </code>
/// A withheld result becomes a <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.ResultWithheld"/>), so a
/// loop reports the call with <c>Success = false</c>.
/// </summary>
public static partial class ToolResultGuardedFunctionInvoker
{
    /// <summary>Creates the invoker delegate.</summary>
    /// <param name="guard">Inspects every result.</param>
    /// <param name="inner">What runs the call. Defaults to invoking the function directly.</param>
    /// <param name="logger">Receives a line per withheld result; optional.</param>
    public static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Create(
        IToolResultGuard guard,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? inner = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var log = logger ?? NullLogger.Instance;

        return async (context, cancellationToken) =>
        {
            var result = inner is null
                ? await context.Function.InvokeAsync(context.Arguments, cancellationToken)
                : await inner(context, cancellationToken);
            if (result is ToolCallRefusal)
            {
                return result; // refused before it ran; nothing to inspect
            }

            return await ApplyAsync(guard, context.Function.Name, context.Arguments, result, log, cancellationToken);
        };
    }

    /// <summary>Runs <paramref name="guard"/> over one result; the shared rule of every path that invokes tools.</summary>
    internal static async ValueTask<object?> ApplyAsync(
        IToolResultGuard guard,
        string toolName,
        IEnumerable<KeyValuePair<string, object?>>? arguments,
        object? result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var text = AsText(result);
        ToolResultVerdict verdict;
        try
        {
            verdict = await guard.InspectAsync(
                new ToolResultInspection(toolName, arguments?.ToDictionary(kv => kv.Key, kv => kv.Value), text),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGuardFailed(logger, ex, toolName);
            return new ToolCallRefusal(ToolCallRefusalKind.ResultWithheld, $"guard error ({ex.Message})");
        }

        if (verdict.Withheld)
        {
            LogWithheld(logger, toolName, verdict.Reason ?? "policy violation");
            return new ToolCallRefusal(ToolCallRefusalKind.ResultWithheld, verdict.Reason ?? "policy violation");
        }
        return verdict.Replacement ?? result;
    }

    private static string AsText(object? result) => result switch
    {
        null => string.Empty,
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool result guard threw while inspecting '{Tool}' - withholding the result (fail-closed)")]
    private static partial void LogGuardFailed(ILogger logger, Exception ex, string tool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool result of '{Tool}' withheld by guard: {Reason}")]
    private static partial void LogWithheld(ILogger logger, string tool, string reason);
}
