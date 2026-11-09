using System.IO.Pipelines;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// A tool call that waits for a person can be released after <see cref="IMcpElicitationHandler.DeferAfter"/>: it ends as
/// <em>awaiting</em> with the call's exact arguments and the server's own state, and the turn goes on. Off by default (a handler
/// that does not set it waits as it always did). The server is a real MCP server over in-memory pipes, shaped like an approval
/// proxy: stateless, the approval carried in the state.
/// </summary>
public sealed class McpDeferredElicitationTests : IAsyncDisposable
{
    private const string State = "approval-state-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverStop = new();
    private Task? _server;
    private int _toolCalls;
    private string? _lastArgumentTheServerGot;

    /// <summary>A person who has not answered: the wait ends only when the token is cancelled, or when <see cref="Answer"/> is called.</summary>
    private sealed class SlowPerson(TimeSpan? deferAfter) : IMcpElicitationHandler
    {
        private readonly TaskCompletionSource<ElicitResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? DeferAfter { get; } = deferAfter;

        public int Asked;
        public volatile bool TokenWasCancelled;

        public void Answer(string action) => _answer.TrySetResult(new ElicitResult { Action = action });

        public async ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            using var registration = cancellationToken.Register(() => TokenWasCancelled = true);
            try
            {
                return await _answer.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TokenWasCancelled = true;
                throw;
            }
        }
    }

    private void StartServer()
    {
        var tool = McpServerTool.Create(
            (string? note, RequestContext<CallToolRequestParams> context) =>
            {
                Interlocked.Increment(ref _toolCalls);
                _lastArgumentTheServerGot = note;
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
                            Url = "https://approvals.example.invalid/asks/apr_1",
                        }),
                    },
                    State);
            },
            new McpServerToolCreateOptions { Name = "send_payment" });

        var server = McpServer.Create(
            new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [tool] });
        _server = server.RunAsync(_serverStop.Token);
    }

    private async Task<McpPluginManager> Connect(McpPluginManager manager)
    {
        StartServer();
        await manager.ConnectCoreAsync(
            "bank",
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpPluginConfig(),
            Ct);
        return manager;
    }

    [Fact]
    public async Task ACallThatWaitsLongerThanDeferAfter_EndsAsAwaiting_WithItsExactArgumentsAndTheServersState()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(150));
        await using var manager = await Connect(new McpPluginManager(person));
        McpAwaitingInput? raised = null;
        manager.CallAwaitingInput += (_, e) => raised = e.Awaiting;

        var result = await manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        result.IsError.Should().BeFalse("an awaiting call is not a failure");
        result.AwaitingInput.Should().NotBeNull("the call was released, not answered");
        var awaiting = result.AwaitingInput!;
        awaiting.PluginName.Should().Be("bank");
        awaiting.ToolName.Should().Be("send_payment");
        awaiting.Arguments.Should().ContainKey("note").WhoseValue.Should().Be("rent", "the call stays bound to its exact arguments");
        awaiting.RequestState.Should().Be(State);
        awaiting.Requests.Should().ContainKey("approval").WhoseValue.ElicitationId.Should().Be("apr_1");
        result.Content.Should().Contain("awaiting_input");
        raised.Should().BeEquivalentTo(awaiting, "the host that listens is handed the same entry");
        person.TokenWasCancelled.Should().BeTrue("the wait on the question was ended, so it is no longer pending in the handler");
        _toolCalls.Should().Be(1, "the call was released, not retried with an answer nobody gave");
        _lastArgumentTheServerGot.Should().Be("rent");
    }

    [Fact]
    public async Task AnAnswerBeforeDeferAfter_CompletesTheCallAsAlways()
    {
        var person = new SlowPerson(TimeSpan.FromSeconds(30));
        await using var manager = await Connect(new McpPluginManager(person));
        var call = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        await WaitUntil(() => person.Asked == 1);
        person.Answer("accept");
        var result = await call;

        result.AwaitingInput.Should().BeNull();
        result.Content.Should().Contain("sent");
        _toolCalls.Should().Be(2, "the SDK retried the call with the server's state once the person answered");
    }

    [Fact]
    public async Task WithoutDeferAfter_TheCallWaitsAsItAlwaysDid()
    {
        var person = new SlowPerson(deferAfter: null);
        await using var manager = await Connect(new McpPluginManager(person));
        var call = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        await WaitUntil(() => person.Asked == 1);
        await Task.Delay(400, Ct);
        call.IsCompleted.Should().BeFalse("no DeferAfter: the call is still waiting for the person");
        person.Answer("accept");
        var result = await call;

        result.AwaitingInput.Should().BeNull();
        result.Content.Should().Contain("sent");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("condition was not met in 10 s");
            }

            await Task.Delay(20, Ct);
        }
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
