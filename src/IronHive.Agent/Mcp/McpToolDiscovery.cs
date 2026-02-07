using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mcp;

/// <summary>
/// Provides hierarchical tool discovery and lazy loading for MCP plugins.
/// Acts as a meta-tool that can discover and load tools on demand.
/// </summary>
public class McpToolDiscovery : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IMcpPluginManager _pluginManager;
    private readonly McpPluginsConfig _config;
    private readonly Dictionary<string, PluginToolInfo> _toolRegistry = new();
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a new tool discovery instance.
    /// </summary>
    /// <param name="pluginManager">Plugin manager for connections</param>
    /// <param name="config">Plugin configuration</param>
    public McpToolDiscovery(IMcpPluginManager pluginManager, McpPluginsConfig config)
    {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Gets all available plugins (connected and configured but not connected).
    /// </summary>
    public IReadOnlyList<PluginInfo> GetAvailablePlugins()
    {
        var result = new List<PluginInfo>();
        var connected = new HashSet<string>(_pluginManager.ConnectedPlugins);
        var excluded = new HashSet<string>(_config.ExcludePlugins);

        foreach (var (name, config) in _config.Plugins)
        {
            result.Add(new PluginInfo
            {
                Name = name,
                IsConnected = connected.Contains(name),
                IsExcluded = excluded.Contains(name),
                Transport = config.Transport,
                Command = config.Command,
                Url = config.Url
            });
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Discovers all tools from all configured plugins.
    /// Connects to plugins as needed for discovery.
    /// </summary>
    /// <param name="connectIfNeeded">Whether to connect to unconfigured plugins for discovery</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered tools with their plugin information</returns>
    public async Task<IReadOnlyList<DiscoveredTool>> DiscoverAllToolsAsync(
        bool connectIfNeeded = false,
        CancellationToken cancellationToken = default)
    {
        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            var result = new List<DiscoveredTool>();
            var excluded = new HashSet<string>(_config.ExcludePlugins);

            foreach (var (name, config) in _config.Plugins)
            {
                if (excluded.Contains(name))
                {
                    continue;
                }

                var isConnected = _pluginManager.ConnectedPlugins.Contains(name);

                if (!isConnected && connectIfNeeded)
                {
                    try
                    {
                        await _pluginManager.ConnectAsync(name, config, cancellationToken);
                        isConnected = true;
                    }
                    catch (Exception)
                    {
                        // Connection failed — skip plugin and continue discovery
                        continue;
                    }
                }

                if (isConnected)
                {
                    var tools = await _pluginManager.GetToolsAsync(name, cancellationToken);
                    foreach (var tool in tools)
                    {
                        var discoveredTool = new DiscoveredTool
                        {
                            PluginName = name,
                            ToolName = tool.Name,
                            Description = tool.Description,
                            Tool = tool
                        };

                        result.Add(discoveredTool);

                        // Update registry
                        var key = $"{name}:{tool.Name}";
                        _toolRegistry[key] = new PluginToolInfo
                        {
                            PluginName = name,
                            ToolName = tool.Name,
                            Config = config
                        };
                    }
                }
            }

            return result.AsReadOnly();
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    /// <summary>
    /// Searches for tools matching the given query.
    /// </summary>
    /// <param name="query">Search query (matches tool name or description)</param>
    /// <param name="connectIfNeeded">Whether to connect plugins for search</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching tools</returns>
    public async Task<IReadOnlyList<DiscoveredTool>> SearchToolsAsync(
        string query,
        bool connectIfNeeded = false,
        CancellationToken cancellationToken = default)
    {
        var allTools = await DiscoverAllToolsAsync(connectIfNeeded, cancellationToken);
        var queryLower = query.ToLowerInvariant();

        return allTools
            .Where(t =>
                t.ToolName.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ||
                (t.Description?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets a specific tool, connecting to its plugin if needed.
    /// </summary>
    /// <param name="pluginName">Plugin name</param>
    /// <param name="toolName">Tool name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The tool if found, null otherwise</returns>
    public async Task<AITool?> GetToolAsync(
        string pluginName,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        // Connect plugin if not connected
        if (!_pluginManager.ConnectedPlugins.Contains(pluginName))
        {
            if (!_config.Plugins.TryGetValue(pluginName, out var config))
            {
                return null;
            }

            try
            {
                await _pluginManager.ConnectAsync(pluginName, config, cancellationToken);
            }
            catch (Exception)
            {
                // Connection failed — tool not available
                return null;
            }
        }

        var tools = await _pluginManager.GetToolsAsync(pluginName, cancellationToken);
        return tools.FirstOrDefault(t => t.Name == toolName);
    }

    /// <summary>
    /// Ensures a plugin is connected, connecting lazily if configured.
    /// </summary>
    /// <param name="pluginName">Plugin name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if plugin is now connected</returns>
    public async Task<bool> EnsurePluginConnectedAsync(
        string pluginName,
        CancellationToken cancellationToken = default)
    {
        if (_pluginManager.ConnectedPlugins.Contains(pluginName))
        {
            return true;
        }

        if (!_config.Plugins.TryGetValue(pluginName, out var config))
        {
            return false;
        }

        try
        {
            await _pluginManager.ConnectAsync(pluginName, config, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // Connection failed — plugin not available
            return false;
        }
    }

    /// <summary>
    /// Creates a meta-tool that can discover and search for tools.
    /// This tool can be added to the agent's tool list for dynamic discovery.
    /// </summary>
    public AITool CreateDiscoveryTool()
    {
        return AIFunctionFactory.Create(
            async (string action, string? query, bool connectPlugins) =>
            {
                return action.ToLowerInvariant() switch
                {
                    "list_plugins" => JsonSerializer.Serialize(GetAvailablePlugins(), JsonOptions),
                    "discover_all" => JsonSerializer.Serialize(await DiscoverAllToolsAsync(connectPlugins), JsonOptions),
                    "search" when query != null => JsonSerializer.Serialize(await SearchToolsAsync(query, connectPlugins), JsonOptions),
                    _ => "Invalid action. Use: list_plugins, discover_all, or search (with query parameter)"
                };
            },
            name: "mcp_discover_tools",
            description: "Discovers available MCP plugins and tools. Actions: 'list_plugins' lists all plugins, 'discover_all' lists all tools from all plugins, 'search' searches for tools matching a query.");
    }

    /// <summary>
    /// Creates a tool that loads a specific plugin on demand.
    /// </summary>
    public AITool CreatePluginLoaderTool()
    {
        return AIFunctionFactory.Create(
            async (string pluginName) =>
            {
                var success = await EnsurePluginConnectedAsync(pluginName);
                if (!success)
                {
                    return $"Failed to connect plugin '{pluginName}'. It may not be configured.";
                }

                var tools = await _pluginManager.GetToolsAsync(pluginName);
                var toolNames = tools.Select(t => t.Name).ToList();
                return $"Plugin '{pluginName}' connected. Available tools: {string.Join(", ", toolNames)}";
            },
            name: "mcp_load_plugin",
            description: "Loads an MCP plugin on demand. Provide the plugin name to connect it and list its available tools.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _discoveryLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class PluginToolInfo
    {
        public required string PluginName { get; init; }
        public required string ToolName { get; init; }
        public required McpPluginConfig Config { get; init; }
    }
}

/// <summary>
/// Information about an available plugin.
/// </summary>
public record PluginInfo
{
    /// <summary>
    /// Plugin name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the plugin is currently connected.
    /// </summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// Whether the plugin is excluded from auto-connect.
    /// </summary>
    public bool IsExcluded { get; init; }

    /// <summary>
    /// Transport type.
    /// </summary>
    public McpTransportType Transport { get; init; }

    /// <summary>
    /// Command (for stdio transport).
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// URL (for http transport).
    /// </summary>
    public string? Url { get; init; }
}

/// <summary>
/// Information about a discovered tool.
/// </summary>
public record DiscoveredTool
{
    /// <summary>
    /// Plugin that provides this tool.
    /// </summary>
    public required string PluginName { get; init; }

    /// <summary>
    /// Tool name.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Tool description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The actual AITool instance (if discovered).
    /// </summary>
    public AITool? Tool { get; init; }
}
