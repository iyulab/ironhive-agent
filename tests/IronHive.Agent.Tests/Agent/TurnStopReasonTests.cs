using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// How a turn ended, read from what actually happened inside a real function-invoking client — and the wall-clock limit
/// that ends a turn by throwing.
/// </summary>
public class TurnStopReasonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentLoop Loop(IChatClient model, IList<AITool> tools, Action<FunctionInvokingChatClient>? configure = null, TimeSpan? maxTurn = null) =>
        new(model.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 2 }), configure).Build(),
            new AgentOptions { Tools = tools, MaxTurnDuration = maxTurn });

    private static AIFunction Lookup() => AIFunctionFactory.Create((string query) => $"found {query}", "Lookup");

    private static AIFunction Broken() => AIFunctionFactory.Create(string (string query) => throw new IOException("disk offline"), "Broken");

    [Fact]
    public async Task An_answer_after_tool_calls_is_completed()
    {
        var mock = new MockChatClient().EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done");

        var response = await Loop(mock, [Lookup()]).RunAsync("go", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task An_answer_cut_at_the_output_limit_is_reported()
    {
        var model = new FixedResponseClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial ans")) { FinishReason = ChatFinishReason.Length });

        var response = await Loop(model, []).RunAsync("go", Ct);

        response.StopReason.Should().Be(TurnStopReason.OutputLimit);
    }

    [Fact]
    public async Task A_turn_a_guard_ended_is_tool_terminated_streamed_or_not()
    {
        MockChatClient Script() => new MockChatClient()
            .EnqueueToolCallResponse("Broken", """{"query":"q"}""")
            .EnqueueToolCallResponse("Broken", """{"query":"q"}""")
            .EnqueueResponse("never requested");

        var response = await Loop(Script(), [Broken()]).RunAsync("go", Ct);
        AgentResponseChunk? last = null;
        await foreach (var chunk in Loop(Script(), [Broken()]).RunStreamingAsync("go", Ct))
        {
            last = chunk;
        }

        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        last!.Turn!.StopReason.Should().Be(TurnStopReason.ToolTerminated);
    }

    [Fact]
    public async Task Calls_to_tools_the_host_runs_are_awaiting_host_tools()
    {
        var declaration = AIFunctionFactory.CreateDeclaration("approve_payment", "Asks a human", AIJsonUtilities.CreateJsonSchema(typeof(string)));
        var mock = new MockChatClient().EnqueueToolCallResponse("approve_payment", """{"value":"x"}""");

        var response = await Loop(mock, [declaration]).RunAsync("go", Ct);

        response.StopReason.Should().Be(TurnStopReason.AwaitingHostTools);
    }

    [Fact]
    public async Task Tool_calls_left_when_the_iteration_limit_is_reached_are_a_step_limit()
    {
        var mock = new MockChatClient()
            .EnqueueToolCallResponse("Lookup", """{"query":"a"}""")
            .EnqueueToolCallResponse("Lookup", """{"query":"b"}""")
            .EnqueueToolCallResponse("Lookup", """{"query":"c"}""")
            .EnqueueResponse("late answer");

        var response = await Loop(mock, [Lookup()], c => c.MaximumIterationsPerRequest = 1).RunAsync("go", Ct);

        response.StopReason.Should().Be(TurnStopReason.StepLimit, $"content was '{response.Content}', calls {response.ToolCalls.Count}");
    }

    [Fact]
    public async Task A_turn_past_MaxTurnDuration_throws_TimeoutException()
    {
        var loop = Loop(new SlowClient(TimeSpan.FromSeconds(30)), [], maxTurn: TimeSpan.FromMilliseconds(150));

        var act = () => loop.RunAsync("go", Ct);

        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("*MaxTurnDuration*");
    }

    [Fact]
    public async Task A_streamed_turn_past_MaxTurnDuration_throws_TimeoutException()
    {
        var loop = Loop(new SlowClient(TimeSpan.FromSeconds(30)), [], maxTurn: TimeSpan.FromMilliseconds(150));

        var act = async () =>
        {
            await foreach (var _ in loop.RunStreamingAsync("go", Ct))
            {
            }
        };

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(100));
        var loop = Loop(new SlowClient(TimeSpan.FromSeconds(30)), [], maxTurn: TimeSpan.FromSeconds(20));

        var act = () => loop.RunAsync("go", caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FixedResponseClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            response.ToChatResponseUpdates().ToAsyncEnumerable();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SlowClient(TimeSpan delay) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "too late"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
            await Task.Delay(delay, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "too late");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
