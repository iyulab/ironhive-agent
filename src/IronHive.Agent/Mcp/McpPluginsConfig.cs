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

    /// <summary>
    /// What the reader refused or ignored, per entry. A refused entry is absent from <see cref="Plugins"/>;
    /// a host shows these before saving a pasted document, or logs them at load.
    /// </summary>
    public IReadOnlyList<McpConfigIssue> Issues { get; init; } = [];
}

/// <summary>
/// One problem the reader found in a plugins document.
/// </summary>
/// <param name="Entry">The entry's name (its key under <c>plugins</c>, <c>mcpServers</c> or <c>servers</c>),
/// or <c>null</c> for a problem with the document itself.</param>
/// <param name="Kind">Whether the entry was left out, or only something in it was ignored.</param>
/// <param name="Message">What was wrong, naming the key.</param>
public sealed record McpConfigIssue(string? Entry, McpConfigIssueKind Kind, string Message);

/// <summary>
/// How an <see cref="McpConfigIssue"/> affected the result.
/// </summary>
public enum McpConfigIssueKind
{
    /// <summary>The entry is not in <see cref="McpPluginsConfig.Plugins"/>.</summary>
    Refused,

    /// <summary>The entry was read; a key or value in it was ignored.</summary>
    Ignored
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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Log but continue with other plugins
                System.Diagnostics.Trace.TraceWarning($"Failed to connect plugin '{name}': {ex.Message}");
            }
        }
    }
}
