using System.IO.Pipelines;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A server that needs a person before a tool call can finish answers <c>tools/call</c> with <c>input_required</c> — a
/// URL-mode elicitation and an opaque <c>requestState</c> (multi round-trip requests). With an
/// <see cref="IMcpElicitationHandler"/> the person is asked and the same call is sent again with that state; without one
/// the call fails, as before. The server here is a real MCP server over in-memory pipes, shaped like an approval proxy:
/// stateless, the approval carried only in the state it gets back.
/// </summary>
public sealed class McpElicitationTests : IAsyncDisposable
{
    private const string State = "opaque-state-1";
    private const string ApprovalUrl = "https://approvals.example.invalid/asks/apr_1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverStop = new();
    private Task? _server;

    /// <summary>Records what the person was shown and answers with <paramref name="action"/>.</summary>
    private sealed class Person(string action) : IMcpElicitationHandler
    {
        public List<(string Plugin, ElicitRequestParams Request)> Asked { get; } = [];

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            Asked.Add((pluginName, request));
            return ValueTask.FromResult(new ElicitResult { Action = action });
        }
    }

    private void StartServer()
    {
        var tool = McpServerTool.Create(
            (RequestContext<CallToolRequestParams> context) =>
            {
                if (context.Params?.RequestState == State)
                {
                    return "sent";
                }

                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["approval"] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "url",
                            Message = "This action needs approval before it runs.",
                            ElicitationId = "apr_1",
                            Url = ApprovalUrl,
                        }),
                    },
                    State);
            },
            new McpServerToolCreateOptions { Name = "send_payment" });

        var options = new McpServerOptions { ToolCollection = [tool] };
        var server = McpServer.Create(
            new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()), options);
        _server = server.RunAsync(_serverStop.Token);
    }

    private async Task<McpPluginManager> Connect(McpPluginManager manager)
    {
        StartServer();
        await manager.ConnectCoreAsync(
            "approvals",
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpPluginConfig(),
            Ct);
        return manager;
    }

    [Fact]
    public async Task InputRequired_AsksThePersonWithTheUrl_AndResendsTheCallWithTheState()
    {
        var person = new Person("accept");
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("approvals", "send_payment", new Dictionary<string, object?>(), Ct);

        result.IsError.Should().BeFalse(result.Content);
        result.Content.Should().Contain("sent", "the retry carried the server's requestState");
        person.Asked.Should().ContainSingle();
        person.Asked[0].Plugin.Should().Be("approvals");
        person.Asked[0].Request.Url.Should().Be(ApprovalUrl);
        person.Asked[0].Request.Mode.Should().Be("url");
    }

    [Fact]
    public async Task WithoutAHandler_TheCallStillFails()
    {
        await using var manager = await Connect(new McpPluginManager());

        var result = await manager.CallToolAsync("approvals", "send_payment", new Dictionary<string, object?>(), Ct);

        (result.IsError || result.Content?.Contains("sent", StringComparison.Ordinal) != true)
            .Should().BeTrue("nobody can answer the server's request — the positive control for the fact above");
    }

    [Fact]
    public async Task AddIronHiveAgent_BuildsTheManagerWithARegisteredHandler()
    {
        var person = new Person("accept");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IMcpElicitationHandler>(services, person);
        IronHive.Agent.Extensions.AgentServiceCollectionExtensions.AddIronHiveAgent(services);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var manager = (McpPluginManager)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IMcpPluginManager>(provider);

        await Connect(manager);
        var result = await manager.CallToolAsync("approvals", "send_payment", new Dictionary<string, object?>(), Ct);

        result.Content.Should().Contain("sent");
        person.Asked.Should().ContainSingle();
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
