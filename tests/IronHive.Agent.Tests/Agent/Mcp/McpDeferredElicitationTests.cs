using System.IO.Pipelines;
using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;
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
    private string? _lastActionTheServerGot;

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

    /// <summary>A person who answers in the last moment: honouring the cancelled token, they hand in an accept instead of giving up.</summary>
    private sealed class LastMomentPerson(TimeSpan deferAfter) : IMcpElicitationHandler
    {
        public TimeSpan? DeferAfter { get; } = deferAfter;

        public async ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new ElicitResult { Action = "accept" };
        }
    }

    /// <summary>A handler that ignores its token: the wait ends only when <see cref="Answer"/> is called.</summary>
    private sealed class DeafPerson(TimeSpan deferAfter) : IMcpElicitationHandler
    {
        private readonly TaskCompletionSource<ElicitResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? DeferAfter { get; } = deferAfter;

        public void Answer() => _answer.TrySetResult(new ElicitResult { Action = "accept" });

        public ValueTask<ElicitResult> ElicitAsync(string pluginName, ElicitRequestParams request, CancellationToken cancellationToken) =>
            new(_answer.Task);
    }

    private sealed class CountingGuard : IMcpToolCallGuard
    {
        public int Calls;
        public int Results;

        public ValueTask<McpToolCallVerdict> CheckCallAsync(McpToolCallInspection call, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(McpToolCallVerdict.Allow());
        }

        public ValueTask<ToolResultVerdict> CheckResultAsync(McpToolCallInspection call, string result, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Results);
            return ValueTask.FromResult(ToolResultVerdict.Allow());
        }
    }

    private sealed class OneCallModel(string callId, string toolName, IDictionary<string, object?> arguments) : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_turn++ == 0
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
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
                    _lastActionTheServerGot = context.Params.InputResponses?["approval"]
                        .Deserialize(InputResponse.ElicitResultJsonTypeInfo)?.Action;
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
        raised.Should().BeSameAs(awaiting, "the host that listens is handed the entry the result carries");
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

    private static ElicitRequestParams ApprovalRequest() => new()
    {
        Mode = "url",
        Message = "This action needs approval before it runs.",
        ElicitationId = "apr_1",
        Url = "https://approvals.example.invalid/asks/apr_1",
    };

    [Fact]
    public async Task TheAnswer_SendsTheReleasedCallAgain_WithItsExactArgumentsAndTheStoredState()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        var released = await manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);
        released.AwaitingInput.Should().NotBeNull();

        var result = await manager.ReissueAsync(
            released.AwaitingInput!, new Dictionary<string, ElicitResult> { ["approval"] = new() { Action = "accept" } }, Ct);

        result.AwaitingInput.Should().BeNull();
        result.IsError.Should().BeFalse(result.Content);
        result.Content.Should().Contain("sent");
        _toolCalls.Should().Be(2, "once released, once sent again with the answer - never retried in between");
        _lastArgumentTheServerGot.Should().Be("rent", "the call is not re-planned: the arguments are the stored ones");
        _lastActionTheServerGot.Should().Be("accept", "the person's answer reached the server as the answer to its request");
    }

    [Fact]
    public async Task AnEntryThatNoManagerHasSeen_CanBeSentAgain_AsAfterARestart()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        var entry = new McpAwaitingInput(
            "bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, State,
            new Dictionary<string, ElicitRequestParams> { ["approval"] = ApprovalRequest() });

        var result = await manager.ReissueAsync(
            entry, new Dictionary<string, ElicitResult> { ["approval"] = new() { Action = "decline" } }, Ct);

        result.Content.Should().Contain("sent", "the server judges the answer; this one only takes the state and the arguments");
        _lastActionTheServerGot.Should().Be("decline");
    }

    [Fact]
    public async Task AStateTheServerNoLongerHonours_IsItsVerdict_AndTheCallWaitsForThePersonAgain()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        var stale = new McpAwaitingInput(
            "bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, "expired-state",
            new Dictionary<string, ElicitRequestParams> { ["approval"] = ApprovalRequest() });

        var result = await manager.ReissueAsync(
            stale, new Dictionary<string, ElicitResult> { ["approval"] = new() { Action = "accept" } }, Ct);

        result.AwaitingInput.Should().NotBeNull("the server asked again with a fresh state; nobody answered yet");
        result.AwaitingInput!.RequestState.Should().Be(State, "the new entry carries the server's new state, not the stale one");
    }

    [Fact]
    public async Task TwoCallsInFlight_AreNotReleased_BecauseTheirStateCouldBePutOnTheWrongCall()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(150));
        await using var manager = await Connect(new McpPluginManager(person));
        var first = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "a" }, Ct);
        var second = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "b" }, Ct);

        await WaitUntil(() => person.Asked == 2);
        await Task.Delay(500, Ct);
        first.IsCompleted.Should().BeFalse("two identical questions are in flight: neither call can be named, so neither is released");
        second.IsCompleted.Should().BeFalse();
        person.Answer("accept");
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.AwaitingInput == null && r.Content!.Contains("sent"));
    }

    [Fact]
    public async Task ACallerWhoCancels_IsNotTakenForADeadline()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, cancel.Token);

        await WaitUntil(() => person.Asked == 1);
        await cancel.CancelAsync();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>("the caller cancelled: that is not a call waiting for a person");
    }

    [Fact]
    public async Task AnAnswerInTheLastMoment_CompletesTheCall_InsteadOfBeingThrownAway()
    {
        await using var manager = await Connect(new McpPluginManager(new LastMomentPerson(TimeSpan.FromMilliseconds(100))));

        var result = await manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        result.AwaitingInput.Should().BeNull("the handler answered when its wait was ended: that answer is the answer");
        result.Content.Should().Contain("sent");
    }

    [Fact]
    public async Task AHandlerThatIgnoresItsToken_DoesNotHoldTheCallForever()
    {
        var person = new DeafPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));

        var result = await manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        result.AwaitingInput.Should().NotBeNull("after a short grace the call is released although the handler never returned");
        person.Answer();
    }

    [Fact]
    public async Task ACallThatStartsWhilePersonThinks_KeepsTheWait()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(400));
        await using var manager = await Connect(new McpPluginManager(person));
        var first = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "a" }, Ct);
        await WaitUntil(() => person.Asked == 1);
        var second = manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "b" }, Ct);

        await WaitUntil(() => person.Asked == 2);
        await Task.Delay(700, Ct);
        first.IsCompleted.Should().BeFalse("a second call started before the first one's deadline: its state could land on the wrong call");
        person.Answer("accept");
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.AwaitingInput == null);
    }

    [Fact]
    public async Task TheGuard_ChecksTheReleasedCallAndItsReissue_AndTheInterfaceReachesBoth()
    {
        var guard = new CountingGuard();
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person, guard: guard));
        IMcpPluginManager iface = manager;
        var raised = new List<McpAwaitingInput>();
        iface.CallAwaitingInput += (_, e) => raised.Add(e.Awaiting);

        var released = await iface.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);
        var result = await iface.ReissueAsync(
            released.AwaitingInput!, new Dictionary<string, ElicitResult> { ["approval"] = new() { Action = "accept" } }, Ct);

        raised.Should().ContainSingle().Which.Should().BeSameAs(released.AwaitingInput);
        result.Content.Should().Contain("sent");
        guard.Calls.Should().Be(2, "the request is checked before the call and before its re-issue");
        guard.Results.Should().Be(1, "the released call had no result to check; the re-issue's result is checked");
    }

    [Fact]
    public async Task ANullArgument_SurvivesTheReissue()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        var entry = new McpAwaitingInput(
            "bank", "send_payment", new Dictionary<string, object?> { ["note"] = null }, State,
            new Dictionary<string, ElicitRequestParams> { ["approval"] = ApprovalRequest() });

        var result = await manager.ReissueAsync(
            entry, new Dictionary<string, ElicitResult> { ["approval"] = new() { Action = "accept" } }, Ct);

        result.IsError.Should().BeFalse(result.Content);
        result.Content.Should().Contain("sent");
        _lastArgumentTheServerGot.Should().BeNull();
    }

    [Fact]
    public async Task TheModelsOwnCall_ReadsAwaitingInput_AndTheHostGetsTheEntryWithTheCallId()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        var raised = new List<McpAwaitingInput>();
        manager.CallAwaitingInput += (_, e) => raised.Add(e.Awaiting);
        var listed = (await manager.GetToolsAsync(Ct)).OfType<AIFunction>().Single(t => t.Name == "send_payment");
        using var chat = new FunctionInvokingChatClient(new OneCallModel("call_42", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }));

        var response = await chat.GetResponseAsync("pay it", new ChatOptions { Tools = [listed] }, Ct);

        var entry = raised.Should().ContainSingle().Subject;
        entry.CallId.Should().Be("call_42");
        entry.ToolName.Should().Be("send_payment");
        entry.Arguments.Should().ContainKey("note");
        entry.RequestState.Should().Be(State);
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()
            .Should().Contain("awaiting_input", "the model reads that the call waits for the person");
    }

    [Fact]
    public async Task ASubscriberThatThrows_DoesNotLoseTheEntry()
    {
        var person = new SlowPerson(TimeSpan.FromMilliseconds(100));
        await using var manager = await Connect(new McpPluginManager(person));
        manager.CallAwaitingInput += (_, _) => throw new InvalidOperationException("subscriber fault");

        var result = await manager.CallToolAsync("bank", "send_payment", new Dictionary<string, object?> { ["note"] = "rent" }, Ct);

        result.AwaitingInput.Should().NotBeNull("the entry is in the result whatever a listener does");
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
