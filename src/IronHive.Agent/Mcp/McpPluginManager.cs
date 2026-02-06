using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Default implementation of MCP plugin manager.
/// </summary>
public class McpPluginManager : IMcpPluginManager
{
    private readonly ConcurrentDictionary<string, McpClientWrapper> _clients = new();
    private readonly ILogger<McpPluginManager>? _logger;
    private bool _disposed;

    public McpPluginManager(ILogger<McpPluginManager>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ConnectedPlugins => [.. _clients.Keys];

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? PluginConnected;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? PluginDisconnected;

    /// <inheritdoc />
    public event EventHandler<McpPluginEventArgs>? ToolsChanged;

    /// <summary>
    /// Raises the ToolsChanged event for a plugin.
    /// </summary>
    protected void OnToolsChanged(string pluginName)
    {
        ToolsChanged?.Invoke(this, new McpPluginEventArgs { PluginName = pluginName });
    }

    /// <inheritdoc />
    public async Task ConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ArgumentNullException.ThrowIfNull(config, nameof(config));

        if (_clients.ContainsKey(name))
        {
            throw new InvalidOperationException($"Plugin '{name}' is already connected.");
        }

        var transport = CreateTransport(name, config);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

        var wrapper = new McpClientWrapper(name, client, config);
        if (!_clients.TryAdd(name, wrapper))
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Plugin '{name}' connection failed.");
        }

        PluginConnected?.Invoke(this, new McpPluginEventArgs { PluginName = name });
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_clients.TryRemove(name, out var wrapper))
        {
            await wrapper.Client.DisposeAsync();
            PluginDisconnected?.Invoke(this, new McpPluginEventArgs { PluginName = name });
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var names = _clients.Keys.ToList();
        foreach (var name in names)
        {
            await DisconnectAsync(name, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var allTools = new List<AITool>();

        foreach (var (_, wrapper) in _clients)
        {
            try
            {
                // McpClientTool inherits from AIFunction which inherits from AITool
                var tools = await wrapper.Client.ListToolsAsync(cancellationToken: cancellationToken);
                allTools.AddRange(tools);
            }
            catch (Exception ex)
            {
                // Log but don't fail - other plugins may still work
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                _logger?.LogWarning(ex, "Failed to list tools from plugin '{PluginName}'", wrapper.Name);
#pragma warning restore CA1848
            }
        }

        return allTools.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            throw new InvalidOperationException($"Plugin '{pluginName}' is not connected.");
        }

        var tools = await wrapper.Client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Cast<AITool>().ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<McpToolResult> CallToolAsync(
        string pluginName,
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            return McpToolResult.Error($"Plugin '{pluginName}' is not connected.");
        }

        try
        {
            // Convert IDictionary to IReadOnlyDictionary
            IReadOnlyDictionary<string, object?>? args = arguments != null
                ? new Dictionary<string, object?>(arguments)
                : null;

            var result = await wrapper.Client.CallToolAsync(
                toolName,
                args,
                progress: null,
                cancellationToken: cancellationToken);

            // Extract text content from result
            var contentBuilder = new StringBuilder();
            foreach (var content in result.Content)
            {
                if (content is TextContentBlock textBlock && textBlock.Text != null)
                {
                    if (contentBuilder.Length > 0)
                    {
                        contentBuilder.AppendLine();
                    }
                    contentBuilder.Append(textBlock.Text);
                }
            }

            return new McpToolResult
            {
                Content = contentBuilder.ToString(),
                IsError = result.IsError ?? false,
                StructuredContent = result.StructuredContent
            };
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"Tool call failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var wrapper in _clients.Values)
        {
            try
            {
                await wrapper.Client.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _clients.Clear();
        GC.SuppressFinalize(this);
    }

    private static StdioClientTransport CreateTransport(string name, McpPluginConfig config)
    {
        return config.Transport switch
        {
            McpTransportType.Stdio => CreateStdioTransport(name, config),
            McpTransportType.Http => throw new NotSupportedException(
                "HTTP transport is not yet supported by MCP SDK. Use stdio transport instead."),
            _ => throw new ArgumentException($"Unknown transport type: {config.Transport}")
        };
    }

    private static StdioClientTransport CreateStdioTransport(string name, McpPluginConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new ArgumentException("Command is required for stdio transport.");
        }

        var options = new StdioClientTransportOptions
        {
            Name = name,
            Command = config.Command,
            Arguments = config.Arguments?.ToList() ?? []
        };

        if (config.Environment != null)
        {
            options.EnvironmentVariables = config.Environment.ToDictionary(
                kvp => kvp.Key,
                kvp => (string?)kvp.Value);
        }

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            options.WorkingDirectory = config.WorkingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private sealed record McpClientWrapper(string Name, McpClient Client, McpPluginConfig Config);
}
