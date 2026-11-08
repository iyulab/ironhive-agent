using System.IO.Pipelines;
using AwesomeAssertions;
using IronHive.Agent.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// The SDK retries a <c>tools/call</c> that a server answers with <c>input_required</c> by itself and never shows its
/// <c>requestState</c> to the elicitation handler. <see cref="McpInputRequiredTap"/> reads the raw responses on their way to
/// the SDK so a host that wants to release such a call (to ask the person in a later turn) has the state to keep. The server
/// is a real MCP server over in-memory pipes, shaped like an approval proxy: stateless, the approval carried in the state.
/// </summary>
public sealed class McpInputRequiredTapTests : IAsyncDisposable
{
    private const string State = "opaque-state-1";
    private const string ApprovalUrl = "https://approvals.example.invalid/asks/apr_1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverStop = new();
    private Task? _server;
    private int _toolCalls;
    private string? _lastStateTheServerGot;

    private void StartServer()
    {
        var tool = McpServerTool.Create(
            (RequestContext<CallToolRequestParams> context) =>
            {
                Interlocked.Increment(ref _toolCalls);
                _lastStateTheServerGot = context.Params?.RequestState;
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

        var server = McpServer.Create(
            new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [tool] });
        _server = server.RunAsync(_serverStop.Token);
    }

    private McpInputRequiredTap Tap() =>
        new(new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()));

    /// <summary>Raised by the handler to release the call instead of waiting for the person.</summary>
    private sealed class Released(ElicitRequestParams request) : Exception("released")
    {
        public ElicitRequestParams Request { get; } = request;
    }

    private static McpClientOptions Options(Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler) => new()
    {
        Handlers = new McpClientHandlers { ElicitationHandler = handler },
        Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } },
    };

    [Fact]
    public async Task TheTapSeesTheStateAndTheRequest_AndTheCallStillCompletesThroughTheSdksOwnRetry()
    {
        StartServer();
        var tap = Tap();
        await using var client = await McpClient.CreateAsync(
            tap, Options((request, _) => ValueTask.FromResult(new ElicitResult { Action = "accept" })), cancellationToken: Ct);

        var result = await client.CallToolAsync("send_payment", cancellationToken: Ct);

        result.IsError.Should().NotBe(true);
        _lastStateTheServerGot.Should().Be(State, "the tap forwards every message unchanged, so the SDK's retry still works");
        var captured = tap.Captured.Should().ContainSingle().Subject;
        captured.RequestState.Should().Be(State);
        captured.InputRequests.Should().ContainKey("approval");
        captured.InputRequests["approval"].ElicitationParams!.ElicitationId.Should().Be("apr_1");
    }

    [Fact]
    public async Task AHandlerThatReleasesTheCall_EndsItAtOnce_AndTheStateIsFoundByTheRequestItWasHanded()
    {
        StartServer();
        var tap = Tap();
        ElicitRequestParams? handed = null;
        await using var client = await McpClient.CreateAsync(
            tap, Options((request, _) =>
            {
                handed = request;
                throw new Released(request!);
            }), cancellationToken: Ct);

        var act = async () => await client.CallToolAsync("send_payment", cancellationToken: Ct);

        await act.Should().ThrowExactlyAsync<Released>("the SDK lets the handler's exception through unwrapped");
        _toolCalls.Should().Be(1, "the call was released, not retried");
        handed.Should().NotBeNull();
        var found = tap.Find(handed!);
        found.Should().NotBeNull("the state recorded for this request can be matched from what the handler was given");
        found!.RequestState.Should().Be(State);
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
