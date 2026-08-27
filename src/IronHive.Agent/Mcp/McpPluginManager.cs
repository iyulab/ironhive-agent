using System.Collections.Concurrent;
using System.Text;
using FluxGuard.Remote.MCP;
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
    private readonly IMCPGuardrail? _guardrail;
    private bool _disposed;

    /// <param name="logger">Optional logger.</param>
    /// <param name="guardrail">
    /// Optional MCP tool-call guardrail (<c>FluxGuard.Remote</c>'s <c>IMCPGuardrail</c>, e.g.
    /// <c>MCPToolValidator</c>). Opt-in: when null (the default), tool calls dispatch exactly as
    /// before this parameter existed. When provided, every <see cref="CallToolAsync"/> validates
    /// the request before dispatch and the result before returning it — see that method's remarks
    /// for the fail-closed policy on a guard-side exception.
    /// </param>
    public McpPluginManager(ILogger<McpPluginManager>? logger = null, IMCPGuardrail? guardrail = null)
    {
        _logger = logger;
        _guardrail = guardrail;
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

    /// <summary>
    /// Calls a tool on a connected MCP plugin.
    /// </summary>
    /// <remarks>
    /// When a guardrail was supplied to the constructor, both the request (before dispatch) and
    /// the result (before it is returned) are validated. A guard-reported block is reported as an
    /// error result — the underlying tool call is never dispatched in the request case, and its
    /// result is never surfaced to the caller in the result case. An exception raised BY the
    /// guardrail itself (as opposed to a request/result it validates and blocks) is treated as
    /// fail-closed: the call is blocked rather than silently dispatched unguarded. This differs
    /// from FluxGuard's own base <c>FailMode</c> (which defaults fail-open outside the
    /// <c>Strict</c> preset) because registering an <see cref="IMCPGuardrail"/> here is itself an
    /// explicit per-consumer opt-in, not a broadly-applied default guard — a consumer who wired
    /// this up clearly wants it enforced, so a guard malfunction should not silently disable the
    /// protection they asked for.
    /// </remarks>
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

        // Convert IDictionary to IReadOnlyDictionary
        IReadOnlyDictionary<string, object?>? args = arguments != null
            ? new Dictionary<string, object?>(arguments)
            : null;

        if (_guardrail != null)
        {
            var guardRequest = new MCPToolRequest
            {
                ServerName = pluginName,
                ToolName = toolName,
                Arguments = arguments?.ToDictionary(kv => kv.Key, kv => kv.Value ?? (object)string.Empty)
            };

            MCPValidationResult requestValidation;
            try
            {
                requestValidation = await _guardrail.ValidateToolCallAsync(guardRequest, cancellationToken);
            }
            catch (Exception ex)
            {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                _logger?.LogWarning(ex, "MCP guardrail threw while validating a tool call to '{Plugin}.{Tool}' — blocking (fail-closed)", pluginName, toolName);
#pragma warning restore CA1848
                return McpToolResult.Error($"Tool call blocked: guardrail error ({ex.Message})");
            }

            if (requestValidation.ShouldBlock || !requestValidation.IsValid)
            {
                return McpToolResult.Error($"Tool call blocked by guardrail: {requestValidation.Reason ?? "policy violation"}");
            }

            try
            {
                var result = await wrapper.Client.CallToolAsync(
                    toolName,
                    args,
                    progress: null,
                    cancellationToken: cancellationToken);

                var content = ExtractTextContent(result);

                MCPValidationResult resultValidation;
                try
                {
                    resultValidation = await _guardrail.ValidateToolResultAsync(guardRequest, content, cancellationToken);
                }
                catch (Exception ex)
                {
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance-critical paths
                    _logger?.LogWarning(ex, "MCP guardrail threw while validating a tool result from '{Plugin}.{Tool}' — blocking (fail-closed)", pluginName, toolName);
#pragma warning restore CA1848
                    return McpToolResult.Error($"Tool result blocked: guardrail error ({ex.Message})");
                }

                if (resultValidation.ShouldBlock || !resultValidation.IsValid)
                {
                    return McpToolResult.Error($"Tool result blocked by guardrail: {resultValidation.Reason ?? "policy violation"}");
                }

                return new McpToolResult
                {
                    Content = resultValidation.SanitizedResult ?? content,
                    IsError = result.IsError ?? false,
                    StructuredContent = result.StructuredContent
                };
            }
            catch (Exception ex)
            {
                return McpToolResult.Error($"Tool call failed: {ex.Message}");
            }
        }

        try
        {
            var result = await wrapper.Client.CallToolAsync(
                toolName,
                args,
                progress: null,
                cancellationToken: cancellationToken);

            return new McpToolResult
            {
                Content = ExtractTextContent(result),
                IsError = result.IsError ?? false,
                StructuredContent = result.StructuredContent
            };
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"Tool call failed: {ex.Message}");
        }
    }

    private static string ExtractTextContent(ModelContextProtocol.Protocol.CallToolResult result)
    {
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

        return contentBuilder.ToString();
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_clients.TryGetValue(pluginName, out var wrapper))
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await wrapper.Client.ListToolsAsync(cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
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

    private static IClientTransport CreateTransport(string name, McpPluginConfig config)
    {
        return config.Transport switch
        {
            McpTransportType.Stdio => CreateStdioTransport(name, config),
            McpTransportType.Http => CreateHttpTransport(name, config),
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

    private static HttpClientTransport CreateHttpTransport(string name, McpPluginConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new ArgumentException("Url is required for HTTP transport.");
        }

        var options = new HttpClientTransportOptions
        {
            Name = name,
            Endpoint = new Uri(config.Url)
        };

        if (config.Headers != null)
        {
            options.AdditionalHeaders = new Dictionary<string, string>(config.Headers);
        }

        return new HttpClientTransport(options);
    }

    private sealed record McpClientWrapper(string Name, McpClient Client, McpPluginConfig Config);
}
