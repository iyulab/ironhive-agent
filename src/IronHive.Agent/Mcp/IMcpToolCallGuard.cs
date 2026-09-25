using IronHive.Agent.Mode;

namespace IronHive.Agent.Mcp;

/// <summary>An MCP tool call, as the guard sees it.</summary>
/// <param name="ServerName">The MCP server (plugin) the call goes to.</param>
/// <param name="ToolName">The tool on that server.</param>
/// <param name="Arguments">The arguments the call carries.</param>
public sealed record McpToolCallInspection(string ServerName, string ToolName, IReadOnlyDictionary<string, object?>? Arguments);

/// <summary>What an <see cref="IMcpToolCallGuard"/> decided about a call before it is dispatched.</summary>
public sealed record McpToolCallVerdict
{
    private McpToolCallVerdict(bool blocked, string? reason)
    {
        Blocked = blocked;
        Reason = reason;
    }

    /// <summary>The call is not dispatched; the caller receives an error result carrying <see cref="Reason"/>.</summary>
    public bool Blocked { get; }

    /// <summary>Why the call was blocked.</summary>
    public string? Reason { get; }

    /// <summary>Dispatch the call.</summary>
    public static McpToolCallVerdict Allow() => new(false, null);

    /// <summary>Do not dispatch the call; the caller reads an error result carrying <paramref name="reason"/>.</summary>
    public static McpToolCallVerdict Block(string reason) => new(true, reason ?? throw new ArgumentNullException(nameof(reason)));
}

/// <summary>
/// Guards the MCP tool calls <see cref="McpPluginManager"/> dispatches: the request before it goes out, and the result
/// before the caller reads it. The MCP counterpart of <see cref="IToolResultGuard"/> (in-process tools), and fail-closed
/// the same way — a guard that throws blocks the call or withholds the result. A FluxGuard-backed implementation is in
/// the <c>IronHive.Agent.FluxGuard</c> package; any other policy engine can implement this directly.
/// </summary>
public interface IMcpToolCallGuard
{
    /// <summary>Decides whether <paramref name="call"/> may be dispatched.</summary>
    ValueTask<McpToolCallVerdict> CheckCallAsync(McpToolCallInspection call, CancellationToken cancellationToken);

    /// <summary>Decides what the caller receives for <paramref name="result"/>, the text the tool returned for <paramref name="call"/>.</summary>
    ValueTask<ToolResultVerdict> CheckResultAsync(McpToolCallInspection call, string result, CancellationToken cancellationToken);
}
