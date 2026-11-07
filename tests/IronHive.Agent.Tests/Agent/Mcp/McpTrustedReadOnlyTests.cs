using System.IO.Pipelines;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A server's <c>readOnlyHint</c> is its own claim about a tool. It reaches the permission decision only from a server the
/// host marked <see cref="McpPluginConfig.TrustReadOnlyHints"/>, and only where no rule spoke and the default would ask - so an
/// unattended run does not refuse a tool a trusted server declared read-only, and nothing else about the policy moves. The
/// server is a real MCP server over in-memory pipes, so the hint travels the way it does in a run.
/// </summary>
public sealed class McpTrustedReadOnlyTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverStop = new();
    private Task? _server;

    private async Task<McpPluginManager> ConnectAsync(string name, bool trust)
    {
        var manager = new McpPluginManager();
        var lookup = McpServerTool.Create(() => "found", new McpServerToolCreateOptions { Name = "lookup", ReadOnly = true });
        var write = McpServerTool.Create(() => "written", new McpServerToolCreateOptions { Name = "write" });
        var undeclared = McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "undeclared", ReadOnly = false });
        var server = McpServer.Create(
            new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [lookup, write, undeclared] });
        _server = server.RunAsync(_serverStop.Token);
        await manager.ConnectCoreAsync(
            name,
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpPluginConfig { TrustReadOnlyHints = trust },
            Ct);
        await manager.GetToolsAsync(Ct);
        return manager;
    }

    private static ToolCallPolicy PolicyOver(McpPluginManager manager, PermissionConfig? config = null) =>
        new(new PermissionEvaluator(config ?? new PermissionConfig()), manager.IsMcpTool, manager.IsTrustedReadOnly);

    [Fact]
    public async Task ATrustedServersReadOnlyTool_IsTrustedReadOnly_AndOthersAreNot()
    {
        await using var manager = await ConnectAsync("docs", trust: true);

        manager.IsTrustedReadOnly("lookup").Should().BeTrue();
        manager.IsTrustedReadOnly("write").Should().BeFalse("the server declared nothing about it");
        manager.IsTrustedReadOnly("undeclared").Should().BeFalse("the server said readOnlyHint false");
        manager.IsTrustedReadOnly("not-a-tool").Should().BeFalse();
    }

    [Fact]
    public async Task AnUntrustedServersHint_IsIgnored()
    {
        await using var manager = await ConnectAsync("docs", trust: false);

        manager.IsMcpTool("lookup").Should().BeTrue();
        manager.IsTrustedReadOnly("lookup").Should().BeFalse("trusting a server's claim about itself is the host's decision, off by default");
    }

    [Fact]
    public async Task TheDefaultOfAManager_TrustsNothing()
    {
        IMcpPluginManager viaTheInterfaceDefault = new NotImplementingIt();

        viaTheInterfaceDefault.IsTrustedReadOnly("lookup").Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheDecision_AllowsATrustedReadOnlyTool_WithoutARule_AndStillAsksAboutTheRest()
    {
        await using var manager = await ConnectAsync("docs", trust: true);
        var policy = PolicyOver(manager);

        policy.Evaluate("lookup", null).Verdict.Should().Be(PermissionAction.Allow);
        policy.Evaluate("write", null).Verdict.Should().Be(PermissionAction.Ask);
        policy.Evaluate("undeclared", null).Verdict.Should().Be(PermissionAction.Ask);
    }

    [Fact]
    public async Task TheDecision_ForAnUntrustedServer_IsWhatItAlwaysWas()
    {
        await using var manager = await ConnectAsync("docs", trust: false);
        var policy = PolicyOver(manager);

        policy.Evaluate("lookup", null).Verdict.Should().Be(PermissionAction.Ask);
        new ToolCallPolicy(new PermissionEvaluator(new PermissionConfig()), manager.IsMcpTool)
            .Evaluate("lookup", null).Verdict.Should().Be(PermissionAction.Ask);
    }

    [Theory]
    [InlineData(PermissionAction.Deny)]
    [InlineData(PermissionAction.Ask)]
    public async Task ARuleOnTheTool_StillWins_OverATrustedHint(PermissionAction ruleAction)
    {
        await using var manager = await ConnectAsync("docs", trust: true);
        var config = new PermissionConfig { McpTools = [new PermissionRule { Pattern = "lookup", Action = ruleAction }] };

        PolicyOver(manager, config).Evaluate("lookup", null).Verdict.Should().Be(ruleAction);
    }

    [Fact]
    public async Task ADefaultOfDeny_IsNotLoosenedByAHint()
    {
        await using var manager = await ConnectAsync("docs", trust: true);
        var config = new PermissionConfig { DefaultAction = PermissionAction.Deny };

        PolicyOver(manager, config).Evaluate("lookup", null).Verdict.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public async Task TheTrustOption_ReadsFromTheYamlPluginFile()
    {
        var config = McpPluginsConfigLoader.Parse(
            """{"mcpServers":{"docs":{"command":"docs-server","trustReadOnlyHints":true},"remote":{"url":"https://example.test/mcp","trustReadOnlyHints":true},"other":{"command":"other-server"}}}""");

        config.Plugins["docs"].TrustReadOnlyHints.Should().BeTrue();
        config.Plugins["remote"].TrustReadOnlyHints.Should().BeTrue();
        config.Plugins["other"].TrustReadOnlyHints.Should().BeFalse();
        await Task.CompletedTask;
    }

    private sealed class NotImplementingIt : IMcpPluginManager
    {
        public IReadOnlyCollection<string> ConnectedPlugins => [];
        public IReadOnlyDictionary<string, McpPluginPending> PendingPlugins => new Dictionary<string, McpPluginPending>();
        public Task ConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ReconnectPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ReconnectAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Microsoft.Extensions.AI.AITool>> GetToolsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Microsoft.Extensions.AI.AITool>> GetToolsAsync(string pluginName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool IsMcpTool(string toolName) => true;
        public Task<McpToolResult> CallToolAsync(string pluginName, string toolName, IDictionary<string, object?>? arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsHealthyAsync(string pluginName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public event EventHandler<McpPluginEventArgs>? PluginConnected { add { } remove { } }
        public event EventHandler<McpPluginEventArgs>? PluginDisconnected { add { } remove { } }
        public event EventHandler<McpPluginEventArgs>? PluginConnectionFailed { add { } remove { } }
        public event EventHandler<McpPluginEventArgs>? ToolsChanged { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _serverStop.CancelAsync();
        if (_server is not null)
        {
            try
            {
                await _server;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _serverStop.Dispose();
    }
}
