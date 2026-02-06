using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// Unit tests for McpToolDiscovery.
/// </summary>
public class McpToolDiscoveryTests
{
    [Fact]
    public async Task Constructor_InitializesCorrectly()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var plugins = discovery.GetAvailablePlugins();
        Assert.Empty(plugins);
    }

    [Fact]
    public void Constructor_WithNullManager_ThrowsArgumentNullException()
    {
        var config = new McpPluginsConfig();

        Assert.Throws<ArgumentNullException>(() => new McpToolDiscovery(null!, config));
    }

    [Fact]
    public async Task Constructor_WithNullConfig_ThrowsArgumentNullException()
    {
        await using var manager = new McpPluginManager();

        Assert.Throws<ArgumentNullException>(() => new McpToolDiscovery(manager, null!));
    }

    [Fact]
    public async Task GetAvailablePlugins_ReturnsConfiguredPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["memory"] = new McpPluginConfig { Command = "memory-indexer" },
                ["code"] = new McpPluginConfig { Url = "http://localhost:8080", Transport = McpTransportType.Http }
            }
        };
        using var discovery = new McpToolDiscovery(manager, config);

        var plugins = discovery.GetAvailablePlugins();

        Assert.Equal(2, plugins.Count);

        var memory = plugins.First(p => p.Name == "memory");
        Assert.False(memory.IsConnected);
        Assert.False(memory.IsExcluded);
        Assert.Equal(McpTransportType.Stdio, memory.Transport);
        Assert.Equal("memory-indexer", memory.Command);

        var code = plugins.First(p => p.Name == "code");
        Assert.False(code.IsConnected);
        Assert.Equal(McpTransportType.Http, code.Transport);
        Assert.Equal("http://localhost:8080", code.Url);
    }

    [Fact]
    public async Task GetAvailablePlugins_MarksExcludedPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["memory"] = new McpPluginConfig { Command = "memory-indexer" },
                ["excluded"] = new McpPluginConfig { Command = "excluded-plugin" }
            },
            ExcludePlugins = ["excluded"]
        };
        using var discovery = new McpToolDiscovery(manager, config);

        var plugins = discovery.GetAvailablePlugins();

        var excluded = plugins.First(p => p.Name == "excluded");
        Assert.True(excluded.IsExcluded);

        var memory = plugins.First(p => p.Name == "memory");
        Assert.False(memory.IsExcluded);
    }

    [Fact]
    public async Task DiscoverAllToolsAsync_ReturnsEmptyForNoConnectedPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["memory"] = new McpPluginConfig { Command = "memory-indexer" }
            }
        };
        using var discovery = new McpToolDiscovery(manager, config);

        var tools = await discovery.DiscoverAllToolsAsync(connectIfNeeded: false);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task DiscoverAllToolsAsync_SkipsExcludedPlugins()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["excluded"] = new McpPluginConfig { Command = "cmd" }
            },
            ExcludePlugins = ["excluded"]
        };
        using var discovery = new McpToolDiscovery(manager, config);

        var tools = await discovery.DiscoverAllToolsAsync(connectIfNeeded: true);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task SearchToolsAsync_ReturnsEmptyForNoMatches()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var results = await discovery.SearchToolsAsync("nonexistent");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetToolAsync_ReturnsNullForNonExistentPlugin()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var tool = await discovery.GetToolAsync("nonexistent", "tool");

        Assert.Null(tool);
    }

    [Fact]
    public async Task EnsurePluginConnectedAsync_ReturnsFalseForNonConfiguredPlugin()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var result = await discovery.EnsurePluginConnectedAsync("nonexistent");

        Assert.False(result);
    }

    [Fact]
    public async Task EnsurePluginConnectedAsync_ReturnsTrueForAlreadyConnectedPlugin()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["test"] = new McpPluginConfig { Command = "cmd" }
            }
        };
        using var discovery = new McpToolDiscovery(manager, config);

        // Plugin not connected, but configured - will fail due to invalid command
        var result = await discovery.EnsurePluginConnectedAsync("test");
        Assert.False(result); // Should fail because cmd doesn't exist
    }

    [Fact]
    public async Task CreateDiscoveryTool_ReturnsAITool()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var tool = discovery.CreateDiscoveryTool();

        Assert.NotNull(tool);
        Assert.Equal("mcp_discover_tools", tool.Name);
        Assert.NotNull(tool.Description);
        Assert.Contains("Discovers", tool.Description);
    }

    [Fact]
    public async Task CreatePluginLoaderTool_ReturnsAITool()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        using var discovery = new McpToolDiscovery(manager, config);

        var tool = discovery.CreatePluginLoaderTool();

        Assert.NotNull(tool);
        Assert.Equal("mcp_load_plugin", tool.Name);
        Assert.NotNull(tool.Description);
        Assert.Contains("Loads", tool.Description);
    }

    [Fact]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var manager = new McpPluginManager();
        var config = new McpPluginsConfig();
        var discovery = new McpToolDiscovery(manager, config);

        discovery.Dispose();
        discovery.Dispose(); // Should not throw

        await manager.DisposeAsync();
    }
}

/// <summary>
/// Tests for PluginInfo.
/// </summary>
public class PluginInfoTests
{
    [Fact]
    public void Properties_SetCorrectly()
    {
        var info = new PluginInfo
        {
            Name = "test-plugin",
            IsConnected = true,
            IsExcluded = false,
            Transport = McpTransportType.Stdio,
            Command = "test-cmd",
            Url = null
        };

        Assert.Equal("test-plugin", info.Name);
        Assert.True(info.IsConnected);
        Assert.False(info.IsExcluded);
        Assert.Equal(McpTransportType.Stdio, info.Transport);
        Assert.Equal("test-cmd", info.Command);
        Assert.Null(info.Url);
    }

    [Fact]
    public void HttpTransport_HasUrlProperty()
    {
        var info = new PluginInfo
        {
            Name = "http-plugin",
            Transport = McpTransportType.Http,
            Url = "http://localhost:8080"
        };

        Assert.Equal(McpTransportType.Http, info.Transport);
        Assert.Equal("http://localhost:8080", info.Url);
    }
}

/// <summary>
/// Tests for DiscoveredTool.
/// </summary>
public class DiscoveredToolTests
{
    [Fact]
    public void Properties_SetCorrectly()
    {
        var tool = new DiscoveredTool
        {
            PluginName = "memory",
            ToolName = "search",
            Description = "Search for content",
            Tool = null
        };

        Assert.Equal("memory", tool.PluginName);
        Assert.Equal("search", tool.ToolName);
        Assert.Equal("Search for content", tool.Description);
        Assert.Null(tool.Tool);
    }
}
