using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Mcp;

public class McpPluginsConfigTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var config = new McpPluginsConfig();

        Assert.Empty(config.Plugins);
        Assert.Equal(30000, config.DefaultTimeoutMs);
        Assert.True(config.AutoConnect);
        Assert.Empty(config.ExcludePlugins);
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var plugins = new Dictionary<string, McpPluginConfig>
        {
            ["filesystem"] = new McpPluginConfig { Command = "npx", Arguments = ["-y", "@modelcontextprotocol/server-filesystem"] },
            ["github"] = new McpPluginConfig { Command = "npx", Arguments = ["-y", "@modelcontextprotocol/server-github"] }
        };

        var config = new McpPluginsConfig
        {
            Plugins = plugins,
            DefaultTimeoutMs = 60000,
            AutoConnect = false,
            ExcludePlugins = ["github"]
        };

        Assert.Equal(2, config.Plugins.Count);
        Assert.Equal(60000, config.DefaultTimeoutMs);
        Assert.False(config.AutoConnect);
        Assert.Single(config.ExcludePlugins);
        Assert.Equal("github", config.ExcludePlugins[0]);
    }
}
