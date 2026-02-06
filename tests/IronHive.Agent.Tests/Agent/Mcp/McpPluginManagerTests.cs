using IronHive.Agent.Mcp;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// Unit tests for McpPluginManager.
/// Note: Most tests require actual MCP servers, so we test
/// edge cases and state management here.
/// </summary>
public class McpPluginManagerTests
{
    [Fact]
    public async Task Constructor_InitializesEmptyState()
    {
        await using var manager = new McpPluginManager();

        Assert.Empty(manager.ConnectedPlugins);
    }

    [Fact]
    public async Task ConnectAsync_WithNullName_ThrowsArgumentNullException()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginConfig { Command = "test" };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => manager.ConnectAsync(null!, config));
    }

    [Fact]
    public async Task ConnectAsync_WithEmptyName_ThrowsArgumentException()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginConfig { Command = "test" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.ConnectAsync("", config));
    }

    [Fact]
    public async Task ConnectAsync_WithNullConfig_ThrowsArgumentNullException()
    {
        await using var manager = new McpPluginManager();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => manager.ConnectAsync("test", null!));
    }

    [Fact]
    public async Task ConnectAsync_WithInvalidCommand_ThrowsException()
    {
        await using var manager = new McpPluginManager();
        var config = new McpPluginConfig
        {
            Transport = McpTransportType.Stdio,
            Command = "nonexistent_command_that_does_not_exist_12345"
        };

        // Should throw when trying to start the process
        await Assert.ThrowsAnyAsync<Exception>(
            () => manager.ConnectAsync("test", config));
    }

    [Fact]
    public async Task DisconnectAsync_NonExistentPlugin_DoesNotThrow()
    {
        await using var manager = new McpPluginManager();

        // Should not throw
        await manager.DisconnectAsync("nonexistent");

        Assert.Empty(manager.ConnectedPlugins);
    }

    [Fact]
    public async Task DisconnectAllAsync_EmptyManager_DoesNotThrow()
    {
        await using var manager = new McpPluginManager();

        await manager.DisconnectAllAsync();

        Assert.Empty(manager.ConnectedPlugins);
    }

    [Fact]
    public async Task GetToolsAsync_EmptyManager_ReturnsEmptyList()
    {
        await using var manager = new McpPluginManager();

        var tools = await manager.GetToolsAsync();

        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetToolsAsync_NonExistentPlugin_ThrowsInvalidOperationException()
    {
        await using var manager = new McpPluginManager();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetToolsAsync("nonexistent"));
    }

    [Fact]
    public async Task CallToolAsync_NonExistentPlugin_ReturnsError()
    {
        await using var manager = new McpPluginManager();

        var result = await manager.CallToolAsync("nonexistent", "tool", null);

        Assert.True(result.IsError);
        Assert.Contains("not connected", result.Content);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllClients()
    {
        var manager = new McpPluginManager();

        await manager.DisposeAsync();

        // After disposal, operations should throw
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.GetToolsAsync());
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var manager = new McpPluginManager();

        await manager.DisposeAsync();
        await manager.DisposeAsync(); // Should not throw
    }
}

/// <summary>
/// Tests for McpPluginConfig.
/// </summary>
public class McpPluginConfigTests
{
    [Fact]
    public void DefaultConfig_HasCorrectDefaults()
    {
        var config = new McpPluginConfig();

        Assert.Equal(McpTransportType.Stdio, config.Transport);
        Assert.Null(config.Command);
        Assert.Null(config.Arguments);
        Assert.Null(config.Environment);
        Assert.Null(config.WorkingDirectory);
        Assert.Null(config.Url);
        Assert.True(config.AutoReconnect);
        Assert.Equal(30000, config.TimeoutMs);
    }

    [Fact]
    public void Config_WithCommand_SetsCorrectly()
    {
        var config = new McpPluginConfig
        {
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"]
        };

        Assert.Equal("npx", config.Command);
        Assert.Equal(2, config.Arguments!.Count);
    }
}

/// <summary>
/// Tests for McpToolResult.
/// </summary>
public class McpToolResultTests
{
    [Fact]
    public void Success_CreatesSuccessfulResult()
    {
        var result = McpToolResult.Success("output");

        Assert.Equal("output", result.Content);
        Assert.False(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void Success_WithStructuredContent_SetsCorrectly()
    {
        var structured = new { value = 42 };
        var result = McpToolResult.Success("output", structured);

        Assert.Equal("output", result.Content);
        Assert.False(result.IsError);
        Assert.Same(structured, result.StructuredContent);
    }

    [Fact]
    public void Error_CreatesErrorResult()
    {
        var result = McpToolResult.Error("something went wrong");

        Assert.Equal("something went wrong", result.Content);
        Assert.True(result.IsError);
    }
}

/// <summary>
/// Tests for McpPluginsConfig.
/// </summary>
public class McpPluginsConfigTests
{
    [Fact]
    public void DefaultConfig_HasCorrectDefaults()
    {
        var config = new McpPluginsConfig();

        Assert.Empty(config.Plugins);
        Assert.Equal(30000, config.DefaultTimeoutMs);
        Assert.True(config.AutoConnect);
        Assert.Empty(config.ExcludePlugins);
    }

    [Fact]
    public void Config_WithPlugins_SetsCorrectly()
    {
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["memory"] = new McpPluginConfig { Command = "memory-indexer" },
                ["code"] = new McpPluginConfig { Command = "code-beaker" }
            }
        };

        Assert.Equal(2, config.Plugins.Count);
        Assert.Contains("memory", config.Plugins.Keys);
        Assert.Contains("code", config.Plugins.Keys);
    }
}

/// <summary>
/// Tests for McpPluginEventArgs.
/// </summary>
public class McpPluginEventArgsTests
{
    [Fact]
    public void EventArgs_SetsProperties()
    {
        var args = new McpPluginEventArgs
        {
            PluginName = "test-plugin",
            ErrorMessage = "connection failed"
        };

        Assert.Equal("test-plugin", args.PluginName);
        Assert.Equal("connection failed", args.ErrorMessage);
    }
}

/// <summary>
/// Tests for McpPluginsConfigLoader.
/// </summary>
public class McpPluginsConfigLoaderTests
{
    [Fact]
    public void LoadFromDefault_NoFiles_ReturnsDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var config = McpPluginsConfigLoader.LoadFromDefault(tempDir);

            Assert.Empty(config.Plugins);
            Assert.True(config.AutoConnect);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void LoadFromFile_NonExistentFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(
            () => McpPluginsConfigLoader.LoadFromFile("/nonexistent/path.json"));
    }

    [Fact]
    public void LoadFromFile_UnsupportedExtension_ThrowsNotSupportedException()
    {
        var tempFile = Path.GetTempFileName() + ".txt";
        File.WriteAllText(tempFile, "test");

        try
        {
            Assert.Throws<NotSupportedException>(
                () => McpPluginsConfigLoader.LoadFromFile(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadFromFile_ValidJson_LoadsCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        File.Move(tempFile, tempFile + ".json");
        tempFile += ".json";

        var json = """
        {
            "plugins": {
                "memory": {
                    "command": "memory-indexer",
                    "arguments": ["--port", "8080"]
                },
                "code": {
                    "command": "code-beaker",
                    "autoReconnect": false,
                    "timeoutMs": 60000
                }
            },
            "defaultTimeoutMs": 45000,
            "autoConnect": true,
            "excludePlugins": ["disabled"]
        }
        """;
        File.WriteAllText(tempFile, json);

        try
        {
            var config = McpPluginsConfigLoader.LoadFromFile(tempFile);

            Assert.Equal(2, config.Plugins.Count);
            Assert.True(config.Plugins.ContainsKey("memory"));
            Assert.True(config.Plugins.ContainsKey("code"));

            var memory = config.Plugins["memory"];
            Assert.Equal("memory-indexer", memory.Command);
            Assert.Equal(2, memory.Arguments!.Count);
            Assert.Equal("--port", memory.Arguments[0]);

            var code = config.Plugins["code"];
            Assert.Equal("code-beaker", code.Command);
            Assert.False(code.AutoReconnect);
            Assert.Equal(60000, code.TimeoutMs);

            Assert.Equal(45000, config.DefaultTimeoutMs);
            Assert.True(config.AutoConnect);
            Assert.Single(config.ExcludePlugins);
            Assert.Equal("disabled", config.ExcludePlugins[0]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void LoadFromFile_ValidYaml_LoadsCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        File.Move(tempFile, tempFile + ".yaml");
        tempFile += ".yaml";

        var yaml = """
        plugins:
          memory:
            command: memory-indexer
            arguments:
              - --port
              - "8080"
          code:
            command: code-beaker
            autoReconnect: false
        defaultTimeoutMs: 45000
        autoConnect: true
        """;
        File.WriteAllText(tempFile, yaml);

        try
        {
            var config = McpPluginsConfigLoader.LoadFromFile(tempFile);

            Assert.Equal(2, config.Plugins.Count);
            Assert.True(config.Plugins.ContainsKey("memory"));
            Assert.Equal("memory-indexer", config.Plugins["memory"].Command);
            Assert.Equal(45000, config.DefaultTimeoutMs);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void DefaultPaths_ContainsExpectedPaths()
    {
        Assert.Contains(".ironhive/plugins.yaml", McpPluginsConfigLoader.DefaultPaths);
        Assert.Contains(".ironhive/plugins.json", McpPluginsConfigLoader.DefaultPaths);
        Assert.Contains("plugins.yaml", McpPluginsConfigLoader.DefaultPaths);
        Assert.Contains("plugins.json", McpPluginsConfigLoader.DefaultPaths);
    }
}
