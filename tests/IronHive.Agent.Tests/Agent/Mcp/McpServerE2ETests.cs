using System.Diagnostics.CodeAnalysis;
using IronHive.Agent.Mcp;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// End-to-end tests for MCP server integration.
/// These tests require Node.js and npm to be installed.
/// Tests are conditionally executed based on environment.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "MCP")]
[SuppressMessage("IDisposableAnalyzers.Correctness", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposed via IAsyncLifetime.DisposeAsync")]
public class McpServerE2ETests : IAsyncLifetime
{
    private McpPluginManager? _manager;
    private bool _canRunTests;
    private const string EverythingServerName = "everything";

    public async Task InitializeAsync()
    {
        // Check if Node.js is available
        _canRunTests = await IsNodeAvailableAsync();
        if (_canRunTests)
        {
            _manager = new McpPluginManager();
        }
    }

    public async Task DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task ConnectToEverythingServer_Succeeds()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();

        await _manager!.ConnectAsync(EverythingServerName, config);

        Assert.Contains(EverythingServerName, _manager.ConnectedPlugins);
    }

    [SkippableFact]
    public async Task ListTools_ReturnsExpectedTools()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        var tools = await _manager.GetToolsAsync(EverythingServerName);

        // Everything server exposes several demo tools
        Assert.NotEmpty(tools);
        // AITool list should have items
        Assert.True(tools.Count > 0, "Should have at least one tool");
    }

    [SkippableFact]
    public async Task CallEchoTool_ReturnsExpectedResult()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        var result = await _manager.CallToolAsync(
            EverythingServerName,
            "echo",
            new Dictionary<string, object?> { ["message"] = "Hello, MCP!" });

        Assert.False(result.IsError, $"Tool call failed: {result.Content}");
        Assert.Contains("Hello, MCP!", result.Content);
    }

    [SkippableFact]
    public async Task CallAddTool_ReturnsCorrectSum()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        var result = await _manager.CallToolAsync(
            EverythingServerName,
            "add",
            new Dictionary<string, object?>
            {
                ["a"] = 5,
                ["b"] = 3
            });

        Assert.False(result.IsError, $"Tool call failed: {result.Content}");
        // The result should contain 8 (5 + 3)
        Assert.Contains("8", result.Content);
    }

    [SkippableFact]
    public async Task DisconnectServer_RemovesFromConnectedPlugins()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        Assert.Contains(EverythingServerName, _manager.ConnectedPlugins);

        await _manager.DisconnectAsync(EverythingServerName);

        Assert.DoesNotContain(EverythingServerName, _manager.ConnectedPlugins);
    }

    [SkippableFact]
    public async Task PluginConnectedEvent_IsFired()
    {
        SkipIfNotAvailable();

        var eventFired = false;
        _manager!.PluginConnected += (_, args) =>
        {
            if (args.PluginName == EverythingServerName)
            {
                eventFired = true;
            }
        };

        var config = CreateEverythingServerConfig();
        await _manager.ConnectAsync(EverythingServerName, config);

        Assert.True(eventFired, "PluginConnected event should have been fired");
    }

    [SkippableFact]
    public async Task PluginDisconnectedEvent_IsFired()
    {
        SkipIfNotAvailable();

        var eventFired = false;
        _manager!.PluginDisconnected += (_, args) =>
        {
            if (args.PluginName == EverythingServerName)
            {
                eventFired = true;
            }
        };

        var config = CreateEverythingServerConfig();
        await _manager.ConnectAsync(EverythingServerName, config);
        await _manager.DisconnectAsync(EverythingServerName);

        Assert.True(eventFired, "PluginDisconnected event should have been fired");
    }

    [SkippableFact]
    public async Task GetAllTools_AggregatesFromMultiplePlugins()
    {
        SkipIfNotAvailable();

        // Connect the same server twice with different names
        var config1 = CreateEverythingServerConfig();
        var config2 = CreateEverythingServerConfig();

        await _manager!.ConnectAsync("server1", config1);
        await _manager.ConnectAsync("server2", config2);

        var allTools = await _manager.GetToolsAsync();
        var server1Tools = await _manager.GetToolsAsync("server1");
        var server2Tools = await _manager.GetToolsAsync("server2");

        // All tools should be at least the sum of both servers' tools
        Assert.True(allTools.Count >= server1Tools.Count + server2Tools.Count);
    }

    [SkippableFact]
    public async Task DisconnectAll_RemovesAllPlugins()
    {
        SkipIfNotAvailable();

        var config1 = CreateEverythingServerConfig();
        var config2 = CreateEverythingServerConfig();

        await _manager!.ConnectAsync("server1", config1);
        await _manager.ConnectAsync("server2", config2);

        Assert.Equal(2, _manager.ConnectedPlugins.Count);

        await _manager.DisconnectAllAsync();

        Assert.Empty(_manager.ConnectedPlugins);
    }

    [SkippableFact]
    public async Task DuplicateConnect_ThrowsInvalidOperationException()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.ConnectAsync(EverythingServerName, config));
    }

    [SkippableFact]
    public async Task CallTool_WithInvalidTool_ReturnsError()
    {
        SkipIfNotAvailable();

        var config = CreateEverythingServerConfig();
        await _manager!.ConnectAsync(EverythingServerName, config);

        var result = await _manager.CallToolAsync(
            EverythingServerName,
            "nonexistent_tool_12345",
            null);

        // The MCP server should return an error for unknown tools
        Assert.True(result.IsError);
    }

    private void SkipIfNotAvailable()
    {
        Skip.If(!_canRunTests, "Node.js is not available");
    }

    private static McpPluginConfig CreateEverythingServerConfig()
    {
        return new McpPluginConfig
        {
            Transport = McpTransportType.Stdio,
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"],
            TimeoutMs = 30000
        };
    }

    private static async Task<bool> IsNodeAvailableAsync()
    {
        try
        {
            // First check if Node.js is available
            using var nodeProcess = new System.Diagnostics.Process();
            nodeProcess.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            nodeProcess.Start();
            await nodeProcess.WaitForExitAsync();
            if (nodeProcess.ExitCode != 0)
            {
                return false;
            }

            // Check if MCP server-everything package can be resolved
            // This will download on first run, which may take time
            // For CI, pre-install or skip if not available
            var mcpServerCheck = Environment.GetEnvironmentVariable("IRONHIVE_MCP_E2E_ENABLED");
            return mcpServerCheck?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }
}
