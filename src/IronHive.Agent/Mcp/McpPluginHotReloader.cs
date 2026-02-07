namespace IronHive.Agent.Mcp;

/// <summary>
/// Watches for configuration file changes and reloads plugins automatically.
/// Provides hot reload capability for MCP plugins.
/// </summary>
public class McpPluginHotReloader : IAsyncDisposable
{
    private readonly IMcpPluginManager _pluginManager;
    private readonly string _watchDirectory;
    private readonly FileSystemWatcher? _watcher;
    private readonly HashSet<string> _excludedPlugins;
    private McpPluginsConfig _currentConfig;
    private bool _disposed;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>
    /// Event raised when plugins are reloaded.
    /// </summary>
    public event EventHandler<PluginReloadEventArgs>? PluginsReloaded;

    /// <summary>
    /// Event raised when a reload error occurs.
    /// </summary>
    public event EventHandler<PluginReloadEventArgs>? ReloadError;

    /// <summary>
    /// Creates a new hot reloader instance.
    /// </summary>
    /// <param name="pluginManager">The plugin manager to manage</param>
    /// <param name="initialConfig">Initial configuration</param>
    /// <param name="watchDirectory">Directory to watch for config changes</param>
    /// <param name="enableFileWatcher">Whether to enable file watching (default: true)</param>
    public McpPluginHotReloader(
        IMcpPluginManager pluginManager,
        McpPluginsConfig initialConfig,
        string? watchDirectory = null,
        bool enableFileWatcher = true)
    {
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _currentConfig = initialConfig ?? throw new ArgumentNullException(nameof(initialConfig));
        _watchDirectory = watchDirectory ?? Directory.GetCurrentDirectory();
        _excludedPlugins = new HashSet<string>(initialConfig.ExcludePlugins);

        if (enableFileWatcher && Directory.Exists(_watchDirectory))
        {
            _watcher = CreateFileWatcher();
        }
    }

    /// <summary>
    /// Initializes connections to all configured plugins.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_currentConfig.AutoConnect)
        {
            return;
        }

        foreach (var (name, config) in _currentConfig.Plugins)
        {
            if (_excludedPlugins.Contains(name))
            {
                continue;
            }

            try
            {
                await _pluginManager.ConnectAsync(name, config, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log but don't fail — other plugins may still work
                ReloadError?.Invoke(this, new PluginReloadEventArgs
                {
                    ErrorMessage = $"Failed to connect plugin '{name}': {ex.Message}"
                });
            }
        }
    }

    /// <summary>
    /// Manually triggers a configuration reload.
    /// </summary>
    /// <param name="newConfig">New configuration to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ReloadAsync(McpPluginsConfig newConfig, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(newConfig, nameof(newConfig));

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var oldConfig = _currentConfig;
            _currentConfig = newConfig;
            _excludedPlugins.Clear();
            foreach (var excluded in newConfig.ExcludePlugins)
            {
                _excludedPlugins.Add(excluded);
            }

            var added = new List<string>();
            var removed = new List<string>();
            var updated = new List<string>();

            // Find plugins to disconnect (removed or excluded)
            foreach (var name in _pluginManager.ConnectedPlugins.ToList())
            {
                if (!newConfig.Plugins.ContainsKey(name) || _excludedPlugins.Contains(name))
                {
                    await _pluginManager.DisconnectAsync(name, cancellationToken);
                    removed.Add(name);
                }
            }

            // Find plugins to connect (added) or reconnect (config changed)
            foreach (var (name, config) in newConfig.Plugins)
            {
                if (_excludedPlugins.Contains(name))
                {
                    continue;
                }

                var isConnected = _pluginManager.ConnectedPlugins.Contains(name);
                var wasConfigured = oldConfig.Plugins.TryGetValue(name, out var oldPluginConfig);
                var configChanged = wasConfigured && !ConfigEquals(oldPluginConfig!, config);

                if (configChanged && isConnected)
                {
                    // Config changed - reconnect
                    await _pluginManager.DisconnectAsync(name, cancellationToken);
                    await _pluginManager.ConnectAsync(name, config, cancellationToken);
                    updated.Add(name);
                }
                else if (!isConnected && newConfig.AutoConnect)
                {
                    // New plugin - connect
                    try
                    {
                        await _pluginManager.ConnectAsync(name, config, cancellationToken);
                        added.Add(name);
                    }
                    catch (Exception ex)
                    {
                        ReloadError?.Invoke(this, new PluginReloadEventArgs
                        {
                            ErrorMessage = $"Failed to connect plugin '{name}': {ex.Message}"
                        });
                    }
                }
            }

            PluginsReloaded?.Invoke(this, new PluginReloadEventArgs
            {
                AddedPlugins = added,
                RemovedPlugins = removed,
                UpdatedPlugins = updated
            });
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Adds a plugin exclusion at runtime.
    /// </summary>
    /// <param name="pluginName">Plugin name to exclude</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ExcludePluginAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName, nameof(pluginName));

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (_excludedPlugins.Add(pluginName))
            {
                if (_pluginManager.ConnectedPlugins.Contains(pluginName))
                {
                    await _pluginManager.DisconnectAsync(pluginName, cancellationToken);
                }
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Removes a plugin exclusion at runtime.
    /// </summary>
    /// <param name="pluginName">Plugin name to include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task IncludePluginAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName, nameof(pluginName));

        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (_excludedPlugins.Remove(pluginName))
            {
                if (_currentConfig.Plugins.TryGetValue(pluginName, out var config) && _currentConfig.AutoConnect)
                {
                    await _pluginManager.ConnectAsync(pluginName, config, cancellationToken);
                }
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Gets the current excluded plugin names.
    /// </summary>
    public IReadOnlySet<string> ExcludedPlugins => _excludedPlugins;

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public McpPluginsConfig CurrentConfig => _currentConfig;

    private FileSystemWatcher CreateFileWatcher()
    {
        var watcher = new FileSystemWatcher(_watchDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        // Watch for plugin config files
        foreach (var path in McpPluginsConfigLoader.DefaultPaths)
        {
            var fileName = Path.GetFileName(path);
            watcher.Filters.Add(fileName);
        }

        // Also watch .ironhive directory if it exists
        var ironhiveDir = Path.Combine(_watchDirectory, ".ironhive");
        if (Directory.Exists(ironhiveDir))
        {
            watcher.IncludeSubdirectories = true;
        }

        watcher.Changed += OnConfigFileChanged;
        watcher.Created += OnConfigFileChanged;

        return watcher;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce and reload asynchronously
        _ = Task.Run(async () =>
        {
            // Small delay to allow file write to complete
            await Task.Delay(100, _disposeCts.Token);

            var newConfig = McpPluginsConfigLoader.LoadFromDefault(_watchDirectory);
            await ReloadAsync(newConfig, _disposeCts.Token);
        }, _disposeCts.Token).ContinueWith(t =>
        {
            if (t.Exception is { } ex && ex.InnerException is not OperationCanceledException)
            {
                ReloadError?.Invoke(this, new PluginReloadEventArgs
                {
                    ErrorMessage = ex.InnerException?.Message ?? ex.Message
                });
            }
        }, TaskScheduler.Default);
    }

    private static bool ConfigEquals(McpPluginConfig a, McpPluginConfig b)
    {
        return a.Transport == b.Transport &&
               a.Command == b.Command &&
               a.Url == b.Url &&
               a.WorkingDirectory == b.WorkingDirectory &&
               a.AutoReconnect == b.AutoReconnect &&
               a.TimeoutMs == b.TimeoutMs &&
               ArgumentsEqual(a.Arguments, b.Arguments) &&
               EnvironmentEqual(a.Environment, b.Environment);
    }

    private static bool ArgumentsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a == null && b == null)
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        return a.SequenceEqual(b);
    }

    private static bool EnvironmentEqual(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a == null && b == null)
        {
            return true;
        }

        if (a == null || b == null)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        return a.All(kvp => b.TryGetValue(kvp.Key, out var value) && value == kvp.Value);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel any pending file-change reload tasks
        await _disposeCts.CancelAsync();

        _watcher?.Dispose();

        // Disconnect all connected plugins
        foreach (var name in _pluginManager.ConnectedPlugins.ToList())
        {
            try
            {
                await _pluginManager.DisconnectAsync(name);
            }
            catch (Exception)
            {
                // Best effort cleanup during dispose
            }
        }

        _disposeCts.Dispose();
        _reloadLock.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Event args for plugin reload events.
/// </summary>
public class PluginReloadEventArgs : EventArgs
{
    /// <summary>
    /// Plugins that were newly connected.
    /// </summary>
    public IReadOnlyList<string> AddedPlugins { get; init; } = [];

    /// <summary>
    /// Plugins that were disconnected.
    /// </summary>
    public IReadOnlyList<string> RemovedPlugins { get; init; } = [];

    /// <summary>
    /// Plugins that were reconnected due to config changes.
    /// </summary>
    public IReadOnlyList<string> UpdatedPlugins { get; init; } = [];

    /// <summary>
    /// Error message if reload failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
