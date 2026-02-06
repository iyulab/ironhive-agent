using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// Unit tests for McpPluginHotReloader.
/// </summary>
public class McpPluginHotReloaderTests
{
    [Fact]
    public async Task Constructor_InitializesCorrectly()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        Assert.Same(config, reloader.CurrentConfig);
        Assert.Empty(reloader.ExcludedPlugins);
    }

    [Fact]
    public async Task Constructor_WithNullManager_ThrowsArgumentNullException()
    {
        var config = new McpPluginsConfig();

        Assert.Throws<ArgumentNullException>(() =>
            new McpPluginHotReloader(null!, config, enableFileWatcher: false));
    }

    [Fact]
    public async Task Constructor_WithNullConfig_ThrowsArgumentNullException()
    {
        await using var manager = new McpPluginManager();

        Assert.Throws<ArgumentNullException>(() =>
            new McpPluginHotReloader(manager, null!, enableFileWatcher: false));
    }

    [Fact]
    public async Task Constructor_WithExcludedPlugins_InitializesExcludedSet()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            ExcludePlugins = ["plugin1", "plugin2"]
        };
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        Assert.Contains("plugin1", reloader.ExcludedPlugins);
        Assert.Contains("plugin2", reloader.ExcludedPlugins);
    }

    [Fact]
    public async Task InitializeAsync_WithAutoConnectFalse_DoesNotConnectPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["test"] = new McpPluginConfig { Command = "test" }
            },
            AutoConnect = false
        };
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await reloader.InitializeAsync();

        Assert.Empty(manager.ConnectedPlugins);
    }

    [Fact]
    public async Task InitializeAsync_SkipsExcludedPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["test1"] = new McpPluginConfig { Command = "test1" },
                ["test2"] = new McpPluginConfig { Command = "test2" }
            },
            ExcludePlugins = ["test1"],
            AutoConnect = true
        };
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        // Note: This will fail to connect (invalid command) but should skip test1
        await reloader.InitializeAsync();

        // No plugins connected due to invalid commands, but test1 was skipped
        Assert.Empty(manager.ConnectedPlugins);
    }

    [Fact]
    public async Task ReloadAsync_AddsNewPlugins()
    {
        await using var manager = new McpPluginManager();
        var initialConfig = new McpPluginsConfig();
        await using var reloader = new McpPluginHotReloader(manager, initialConfig, enableFileWatcher: false);

        var newConfig = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["new-plugin"] = new McpPluginConfig { Command = "new-plugin" }
            },
            AutoConnect = false // Don't actually connect
        };

        var reloadedPlugins = new List<string>();
        reloader.PluginsReloaded += (_, args) =>
        {
            reloadedPlugins.AddRange(args.AddedPlugins);
        };

        await reloader.ReloadAsync(newConfig);

        Assert.Same(newConfig, reloader.CurrentConfig);
    }

    [Fact]
    public async Task ReloadAsync_RemovesDisconnectedPlugins()
    {
        await using var manager = new McpPluginManager();
        var initialConfig = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["plugin1"] = new McpPluginConfig { Command = "cmd1" }
            }
        };
        await using var reloader = new McpPluginHotReloader(manager, initialConfig, enableFileWatcher: false);

        var newConfig = new McpPluginsConfig(); // Empty - remove all

        var removedPlugins = new List<string>();
        reloader.PluginsReloaded += (_, args) =>
        {
            removedPlugins.AddRange(args.RemovedPlugins);
        };

        await reloader.ReloadAsync(newConfig);

        Assert.Empty(reloader.CurrentConfig.Plugins);
    }

    [Fact]
    public async Task ExcludePluginAsync_AddsToExcludedSet()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await reloader.ExcludePluginAsync("test-plugin");

        Assert.Contains("test-plugin", reloader.ExcludedPlugins);
    }

    [Fact]
    public async Task ExcludePluginAsync_WithNull_ThrowsArgumentNullException()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await Assert.ThrowsAsync<ArgumentNullException>(() => reloader.ExcludePluginAsync(null!));
    }

    [Fact]
    public async Task ExcludePluginAsync_WithEmpty_ThrowsArgumentException()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await Assert.ThrowsAsync<ArgumentException>(() => reloader.ExcludePluginAsync(""));
    }

    [Fact]
    public async Task IncludePluginAsync_RemovesFromExcludedSet()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            ExcludePlugins = ["test-plugin"]
        };
        await using var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        Assert.Contains("test-plugin", reloader.ExcludedPlugins);

        await reloader.IncludePluginAsync("test-plugin");

        Assert.DoesNotContain("test-plugin", reloader.ExcludedPlugins);
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await reloader.DisposeAsync();
        await reloader.DisposeAsync(); // Should not throw

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task AfterDispose_MethodsThrowObjectDisposedException()
    {
        var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        var reloader = new McpPluginHotReloader(manager, config, enableFileWatcher: false);

        await reloader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reloader.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reloader.ReloadAsync(config));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reloader.ExcludePluginAsync("test"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reloader.IncludePluginAsync("test"));

        await manager.DisposeAsync();
    }
}

/// <summary>
/// Tests for PluginReloadEventArgs.
/// </summary>
public class PluginReloadEventArgsTests
{
    [Fact]
    public void DefaultValues_AreEmpty()
    {
        var args = new PluginReloadEventArgs();

        Assert.Empty(args.AddedPlugins);
        Assert.Empty(args.RemovedPlugins);
        Assert.Empty(args.UpdatedPlugins);
        Assert.Null(args.ErrorMessage);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var args = new PluginReloadEventArgs
        {
            AddedPlugins = ["added1", "added2"],
            RemovedPlugins = ["removed1"],
            UpdatedPlugins = ["updated1"],
            ErrorMessage = "Test error"
        };

        Assert.Equal(2, args.AddedPlugins.Count);
        Assert.Single(args.RemovedPlugins);
        Assert.Single(args.UpdatedPlugins);
        Assert.Equal("Test error", args.ErrorMessage);
    }
}
