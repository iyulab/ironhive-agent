using System.IO.Pipelines;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A plugin whose server was not ready when the engine started must not stay unconnected for the engine's life:
/// the manager keeps it as pending, tries it again on a growing wait, and a plugin found lost is
/// connected again the same way. The servers here are real MCP servers over in-memory pipes; the clock is a test clock.
/// </summary>
public sealed class McpPluginReconnectTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CancellationTokenSource _serversStop = new();
    private readonly List<Task> _servers = [];
    private readonly TestClock _clock = new();

    /// <summary>A clock the test moves.</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A transport whose server is not there: connecting throws, as an HTTP server answering 401 does.</summary>
    private sealed class RefusedTransport(string reason) : IClientTransport
    {
        public string Name => "refused";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException(reason);
    }

    /// <summary>A real server with one tool, reachable through the returned transport.</summary>
    private StreamClientTransport StartServer(string toolName)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var tool = McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = toolName });
        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [tool] });
        _servers.Add(server.RunAsync(_serversStop.Token));
        return new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
    }

    /// <summary>A manager whose plugin "late" is refused for the first <paramref name="refusals"/> attempts, then served.</summary>
    private McpPluginManager ManagerWithALateServer(int refusals, out Func<int> attempts)
    {
        var attempted = 0;
        attempts = () => attempted;
        return new McpPluginManager(timeProvider: _clock)
        {
            TransportFactory = (_, _) => ++attempted <= refusals
                ? (IClientTransport)new RefusedTransport("401 Unauthorized")
                : StartServer("message.send"),
        };
    }

    private static McpPluginConfig Http(bool autoReconnect = true) => new()
    {
        Transport = McpTransportType.Http,
        Url = "https://tools.example.invalid/mcp",
        AutoReconnect = autoReconnect,
        TimeoutMs = 5000,
    };

    [Fact]
    public async Task AServerThatRefusesAtStart_IsPending_WithWhyAndWhen_AndTheCallerStillSeesTheFailure()
    {
        await using var manager = ManagerWithALateServer(refusals: 1, out _);
        var failures = new List<McpPluginEventArgs>();
        manager.PluginConnectionFailed += (_, e) => failures.Add(e);

        var act = () => manager.ConnectAsync("late", Http(), Ct);

        await act.Should().ThrowAsync<IOException>();
        manager.ConnectedPlugins.Should().BeEmpty();
        var pending = manager.PendingPlugins.Should().ContainKey("late").WhoseValue;
        pending.LastError.Should().Contain("401");
        pending.Attempts.Should().Be(1);
        pending.NextAttempt.Should().Be(_clock.Now + TimeSpan.FromSeconds(5));
        failures.Should().ContainSingle().Which.ErrorMessage.Should().Contain("401");
    }

    [Fact]
    public async Task ReconnectPending_WaitsForTheBackoff_ThenConnectsAndForgetsThePlugin()
    {
        await using var manager = ManagerWithALateServer(refusals: 1, out var attempts);
        var connected = new List<string>();
        manager.PluginConnected += (_, e) => connected.Add(e.PluginName);
        await Assert.ThrowsAsync<IOException>(() => manager.ConnectAsync("late", Http(), Ct));

        (await manager.ReconnectPendingAsync(Ct)).Should().BeEmpty("the first retry is due only after the 5 s wait");
        attempts().Should().Be(1, "nothing was tried before it was due");

        _clock.Advance(TimeSpan.FromSeconds(5));
        var now = await manager.ReconnectPendingAsync(Ct);

        now.Should().Equal("late");
        manager.PendingPlugins.Should().BeEmpty();
        manager.ConnectedPlugins.Should().Equal("late");
        connected.Should().Equal("late");
        var tools = await manager.GetToolsAsync("late", Ct);
        tools.Select(tool => tool.Name).Should().Equal("message.send");
    }

    [Fact]
    public async Task AFailedRetry_DoublesTheWait_UpToFiveMinutes()
    {
        await using var manager = ManagerWithALateServer(refusals: int.MaxValue, out _);
        await Assert.ThrowsAsync<IOException>(() => manager.ConnectAsync("late", Http(), Ct));

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 9; i++)
        {
            var next = manager.PendingPlugins["late"].NextAttempt;
            waits.Add(next - _clock.Now);
            _clock.Advance(next - _clock.Now);
            (await manager.ReconnectPendingAsync(Ct)).Should().BeEmpty();
        }

        waits.Select(wait => (int)wait.TotalSeconds).Should().Equal(5, 10, 20, 40, 80, 160, 300, 300, 300);
        manager.PendingPlugins["late"].Attempts.Should().Be(10);
    }

    [Fact]
    public async Task APluginWithAutoReconnectOff_IsNotRetried()
    {
        await using var manager = ManagerWithALateServer(refusals: 1, out _);
        var failures = 0;
        manager.PluginConnectionFailed += (_, _) => failures++;

        await Assert.ThrowsAsync<IOException>(() => manager.ConnectAsync("late", Http(autoReconnect: false), Ct));
        _clock.Advance(TimeSpan.FromHours(1));

        manager.PendingPlugins.Should().BeEmpty();
        (await manager.ReconnectPendingAsync(Ct)).Should().BeEmpty();
        manager.ConnectedPlugins.Should().BeEmpty();
        failures.Should().Be(1, "the failure is still reported");
    }

    [Fact]
    public async Task ADisconnectedPendingPlugin_IsForgotten()
    {
        await using var manager = ManagerWithALateServer(refusals: 1, out _);
        await Assert.ThrowsAsync<IOException>(() => manager.ConnectAsync("late", Http(), Ct));

        await manager.DisconnectAsync("late", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));

        manager.PendingPlugins.Should().BeEmpty();
        (await manager.ReconnectPendingAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AConfigurationNoTransportCanBeBuiltFrom_IsAnErrorNotAPendingServer()
    {
        await using var manager = new McpPluginManager(timeProvider: _clock);

        var act = () => manager.ConnectAsync("bad", new McpPluginConfig { Transport = McpTransportType.Http }, Ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Url*");
        manager.PendingPlugins.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnect_ReplacesAConnectedPlugin_WithANewConnection_OnTheSameConfiguration()
    {
        var served = 0;
        await using var manager = new McpPluginManager(timeProvider: _clock)
        {
            TransportFactory = (_, _) => StartServer($"tool-{++served}"),
        };
        await manager.ConnectAsync("p", Http(), Ct);
        var disconnected = new List<string>();
        manager.PluginDisconnected += (_, e) => disconnected.Add(e.PluginName);

        var ok = await manager.ReconnectAsync("p", Ct);

        ok.Should().BeTrue();
        disconnected.Should().Equal("p");
        manager.ConnectedPlugins.Should().Equal("p");
        (await manager.GetToolsAsync("p", Ct)).Select(tool => tool.Name).Should().Equal("tool-2");
    }

    [Fact]
    public async Task Reconnect_OfAPluginThatCannotBeReached_LeavesItPending_AndOfAnUnknownOneDoesNothing()
    {
        var served = 0;
        await using var manager = new McpPluginManager(timeProvider: _clock)
        {
            TransportFactory = (_, _) => ++served == 1 ? (IClientTransport)StartServer("only") : new RefusedTransport("down"),
        };
        await manager.ConnectAsync("p", Http(), Ct);

        (await manager.ReconnectAsync("p", Ct)).Should().BeFalse();
        (await manager.ReconnectAsync("nobody", Ct)).Should().BeFalse();

        manager.ConnectedPlugins.Should().BeEmpty();
        manager.PendingPlugins.Should().ContainKey("p").WhoseValue.LastError.Should().Contain("down");
    }

    [Fact]
    public async Task Reconnect_LeavesAPluginWithAutoReconnectOffAlone()
    {
        await using var manager = new McpPluginManager(timeProvider: _clock) { TransportFactory = (_, _) => StartServer("only") };
        await manager.ConnectAsync("p", Http(autoReconnect: false), Ct);

        (await manager.ReconnectAsync("p", Ct)).Should().BeFalse();

        manager.ConnectedPlugins.Should().Equal("p");
    }

    public async ValueTask DisposeAsync()
    {
        await _serversStop.CancelAsync();
        try
        {
            await Task.WhenAll(_servers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // A server that ends with its pipe closed ends in an exception; nothing to assert about it.
        }

        _serversStop.Dispose();
    }
}
