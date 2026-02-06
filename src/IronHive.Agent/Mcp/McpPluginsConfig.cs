namespace IronHive.Agent.Mcp;

/// <summary>
/// Configuration for MCP plugins loaded from settings file.
/// Corresponds to .ironhive/plugins.yaml or plugins.json
/// </summary>
public record McpPluginsConfig
{
    /// <summary>
    /// List of plugin configurations.
    /// </summary>
    public IReadOnlyDictionary<string, McpPluginConfig> Plugins { get; init; }
        = new Dictionary<string, McpPluginConfig>();

    /// <summary>
    /// Default timeout for all plugins in milliseconds.
    /// </summary>
    public int DefaultTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Whether to auto-connect plugins on startup.
    /// </summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>
    /// Plugins to exclude from auto-connect.
    /// </summary>
    public IReadOnlyList<string> ExcludePlugins { get; init; } = [];
}

/// <summary>
/// Extension methods for loading MCP plugin configurations.
/// </summary>
public static class McpPluginsConfigExtensions
{
    /// <summary>
    /// Loads plugins from configuration into the manager.
    /// </summary>
    /// <param name="manager">Plugin manager</param>
    /// <param name="config">Plugins configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task LoadFromConfigAsync(
        this IMcpPluginManager manager,
        McpPluginsConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager, nameof(manager));
        ArgumentNullException.ThrowIfNull(config, nameof(config));

        if (!config.AutoConnect)
        {
            return;
        }

        var excludeSet = new HashSet<string>(config.ExcludePlugins, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, pluginConfig) in config.Plugins)
        {
            if (excludeSet.Contains(name))
            {
                continue;
            }

            // Apply default timeout if not specified
            var effectiveConfig = pluginConfig with
            {
                TimeoutMs = pluginConfig.TimeoutMs > 0 ? pluginConfig.TimeoutMs : config.DefaultTimeoutMs
            };

            try
            {
                await manager.ConnectAsync(name, effectiveConfig, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log but continue with other plugins
                System.Diagnostics.Debug.WriteLine($"Failed to connect plugin '{name}': {ex.Message}");
            }
        }
    }
}
