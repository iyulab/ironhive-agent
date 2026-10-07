using System.Text.Json;
using Microsoft.Extensions.AI;
using IronHive.Agent.Invocation;
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
/// a file's contents). A <see cref="ToolResultGuardMiddleware"/> applies it in a <see cref="Invocation.ToolInvocationPipeline"/>; the
/// container's pipeline includes one whenever an <see cref="IToolResultGuard"/> is registered. Like the MCP guard it is
/// fail-closed: a guard that throws withholds the result.
/// </summary>
public interface IToolResultGuard
{
    /// <summary>Decides what the model receives for <paramref name="inspection"/>.</summary>
    ValueTask<ToolResultVerdict> InspectAsync(ToolResultInspection inspection, CancellationToken cancellationToken);
}

/// <summary>
/// Puts every tool result through an <see cref="IToolResultGuard"/> before the model reads it — the result stage of a
/// <see cref="ToolInvocationPipeline"/>, so it covers results of in-process calls and results a host supplies for the
/// tools it runs itself. The permission gate decides whether a call runs; this decides what its result becomes. A
/// withheld result becomes a <see cref="ToolCallRefusal"/> (<see cref="ToolCallRefusalKind.ResultWithheld"/>), so a
/// loop reports the call with <c>Success = false</c>; a guard that throws withholds the result (fail-closed).
/// </summary>
public sealed partial class ToolResultGuardMiddleware : IToolResultMiddleware
{
    private readonly IToolResultGuard _guard;
    private readonly ILogger _logger;

    /// <summary>Creates the result step over <paramref name="guard"/>.</summary>
    /// <param name="guard">Inspects every result.</param>
    /// <param name="logger">Receives a line per withheld result; optional.</param>
    public ToolResultGuardMiddleware(IToolResultGuard guard, ILogger<ToolResultGuardMiddleware>? logger = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The guard this step applies.</summary>
    public IToolResultGuard Guard => _guard;

    /// <inheritdoc />
    public async ValueTask<object?> OnResultAsync(ToolResultContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = context.Result;
        ToolResultVerdict verdict;
        try
        {
            verdict = await _guard.InspectAsync(
                new ToolResultInspection(context.ToolName, context.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value), AsText(result)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGuardFailed(_logger, ex, context.ToolName);
            return new ToolCallRefusal(ToolCallRefusalKind.ResultWithheld, $"guard error ({ex.Message})");
        }

        if (verdict.Withheld)
        {
            LogWithheld(_logger, context.ToolName);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool result of '{Tool}' withheld by guard")]
    private static partial void LogWithheld(ILogger logger, string tool);
}
