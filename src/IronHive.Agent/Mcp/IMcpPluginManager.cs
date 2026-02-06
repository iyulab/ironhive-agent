using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Manages MCP (Model Context Protocol) plugin connections.
/// </summary>
public interface IMcpPluginManager : IAsyncDisposable
{
    /// <summary>
    /// Gets all currently connected plugin names.
    /// </summary>
    IReadOnlyCollection<string> ConnectedPlugins { get; }

    /// <summary>
    /// Connects to an MCP server.
    /// </summary>
    /// <param name="name">Unique name for this plugin</param>
    /// <param name="config">Plugin configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects a specific plugin.
    /// </summary>
    /// <param name="name">Plugin name to disconnect</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DisconnectAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects all plugins.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DisconnectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all tools from all connected plugins as AITools.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of AI tools from all plugins</returns>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tools from a specific plugin.
    /// </summary>
    /// <param name="pluginName">Plugin name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of AI tools from the plugin</returns>
    Task<IReadOnlyList<AITool>> GetToolsAsync(string pluginName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls a tool on a specific plugin.
    /// </summary>
    /// <param name="pluginName">Plugin name</param>
    /// <param name="toolName">Tool name</param>
    /// <param name="arguments">Tool arguments</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tool result content</returns>
    Task<McpToolResult> CallToolAsync(
        string pluginName,
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when a plugin is connected.
    /// </summary>
    event EventHandler<McpPluginEventArgs>? PluginConnected;

    /// <summary>
    /// Event raised when a plugin is disconnected.
    /// </summary>
    event EventHandler<McpPluginEventArgs>? PluginDisconnected;

    /// <summary>
    /// Event raised when tools list changes on a plugin.
    /// </summary>
    event EventHandler<McpPluginEventArgs>? ToolsChanged;
}

/// <summary>
/// Configuration for an MCP plugin.
/// </summary>
public record McpPluginConfig
{
    /// <summary>
    /// Transport type (stdio, http).
    /// </summary>
    public McpTransportType Transport { get; init; } = McpTransportType.Stdio;

    /// <summary>
    /// Command to execute (for stdio transport).
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Command arguments (for stdio transport).
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// Environment variables for the process.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    /// Working directory for the process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// HTTP endpoint URL (for http transport).
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Whether to auto-reconnect on disconnect.
    /// </summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 30000;
}

/// <summary>
/// MCP transport types.
/// </summary>
public enum McpTransportType
{
    /// <summary>
    /// Standard I/O transport (spawns a process).
    /// </summary>
    Stdio,

    /// <summary>
    /// HTTP/SSE transport.
    /// </summary>
    Http
}

/// <summary>
/// Result from an MCP tool call.
/// </summary>
public record McpToolResult
{
    /// <summary>
    /// Text content of the result.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Whether the tool execution resulted in an error.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Structured content (if available).
    /// </summary>
    public object? StructuredContent { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static McpToolResult Success(string content, object? structuredContent = null) => new()
    {
        Content = content,
        IsError = false,
        StructuredContent = structuredContent
    };

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static McpToolResult Error(string errorMessage) => new()
    {
        Content = errorMessage,
        IsError = true
    };
}

/// <summary>
/// Event args for MCP plugin events.
/// </summary>
public class McpPluginEventArgs : EventArgs
{
    /// <summary>
    /// Plugin name.
    /// </summary>
    public required string PluginName { get; init; }

    /// <summary>
    /// Optional error message if the event was caused by an error.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
