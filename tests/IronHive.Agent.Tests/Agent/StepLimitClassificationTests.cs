using AwesomeAssertions;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// At <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/> the middleware invokes the last round's calls
/// and asks the model once more <b>without tools</b>; the turn then ends on text with no call left unanswered. The loop
/// must still report <see cref="TurnStopReason.StepLimit"/> — a host that reads the stop reason (a CLI exit code, a
/// benchmark harness) would otherwise take an agent that ran out of steps for one that finished.
/// </summary>
public sealed class StepLimitClassificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Calls the tool on every request that offers tools, for <paramref name="toolRounds"/> rounds; text otherwise.</summary>
    private sealed class ToolHappyModel(int toolRounds) : IChatClient
    {
        private int _rounds;

        public List<int> ToolCountsSeen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var offered = options?.Tools?.Count ?? 0;
            ToolCountsSeen.Add(offered);
            if (offered > 0 && _rounds < toolRounds)
            {
                _rounds++;
                var call = new FunctionCallContent($"call-{_rounds}", "write_note", new Dictionary<string, object?> { ["text"] = $"n{_rounds}" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])) { FinishReason = ChatFinishReason.ToolCalls });
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly ChatOptions WithTool = new() { Tools = [AIFunctionFactory.Create((string text) => "noted " + text, "write_note")] };

    private static (AgentLoop Loop, ToolHappyModel Model) Loop(int toolRounds, int cap)
    {
        var model = new ToolHappyModel(toolRounds);
        var client = new ChatClientBuilder(model).UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = cap).Build();
        return (new AgentLoop(client), model);
    }

    [Fact]
    public async Task ATurnCutAtTheIterationCap_IsAStepLimit_NotCompleted()
    {
        var (loop, model) = Loop(toolRounds: 10, cap: 2);

        var response = await loop.RunAsync("take notes", WithTool, Ct);

        model.ToolCountsSeen.Should().EndWith(0, "the middleware's final request at the cap offers no tools - the premise of this fact");
        response.ToolCalls.Should().HaveCount(2);
        response.StopReason.Should().Be(TurnStopReason.StepLimit);
    }

    [Fact]
    public async Task ATurnThatFinishesUnderTheCap_IsCompleted()
    {
        var (loop, model) = Loop(toolRounds: 1, cap: 5);

        var response = await loop.RunAsync("take a note", WithTool, Ct);

        model.ToolCountsSeen.Should().OnlyContain(n => n > 0, "the model answered while tools were still offered");
        response.ToolCalls.Should().HaveCount(1);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task TheStreamingTurn_ClassifiesTheCapTheSameWay()
    {
        var (loop, _) = Loop(toolRounds: 10, cap: 2);

        AgentResponseChunk? last = null;
        await foreach (var chunk in loop.RunStreamingAsync("take notes", WithTool, Ct))
        {
            last = chunk;
        }

        last!.Turn!.StopReason.Should().Be(TurnStopReason.StepLimit);
    }
}
