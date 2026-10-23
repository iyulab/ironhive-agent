using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A turn's model request runs its tool rounds inside it, so sending it again after a failure replays the tools. The loop
/// retries a failed request only when no tool ran during it; a failure after a tool ran surfaces.
/// </summary>
public sealed class TurnRetryReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers each model call from a script: a tool call, a failure, or text.</summary>
    private sealed class Scripted(params Func<ChatResponse>[] script) : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var step = script[Math.Min(_calls, script.Length - 1)];
            _calls++;
            return Task.FromResult(step());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatResponse CallStep() =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), "take_next")]))
        {
            FinishReason = ChatFinishReason.ToolCalls
        };

    private static ChatResponse Fail() => throw new HttpRequestException("connection reset");

    private static ChatResponse Done() => new(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop };

    private static (AgentLoop Loop, Func<int> Taken) Build(IChatClient model, bool withPipeline)
    {
        var taken = 0;
        var tool = AIFunctionFactory.Create(() => $"item {Interlocked.Increment(ref taken)}", "take_next");
        var builder = new ChatClientBuilder(model);
        var client = withPipeline
            ? builder.UseToolInvocationPipeline(new ToolInvocationPipeline([])).Build()
            : builder.UseFunctionInvocation().Build();
        var loop = new AgentLoop(client, new AgentOptions { Tools = [tool] },
            errorRecovery: new ErrorRecoveryService(new ErrorRecoveryConfig { DefaultRetryDelay = TimeSpan.Zero }));
        return (loop, () => taken);
    }

    [Fact]
    public async Task AFailureAfterAToolRan_Surfaces_AndTheToolIsNotRunAgain()
    {
        var model = new Scripted(CallStep, Fail, CallStep, Done);
        var (loop, taken) = Build(model, withPipeline: true);

        var act = () => loop.RunAsync("take one item", Ct);

        await Assert.ThrowsAsync<HttpRequestException>(act);
        Assert.Equal(1, taken());
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AFailureBeforeAnyToolRan_IsRetried_AndTheTurnCompletes()
    {
        var model = new Scripted(Fail, CallStep, Done);
        var (loop, taken) = Build(model, withPipeline: true);

        var response = await loop.RunAsync("take one item", Ct);

        Assert.Equal("done", response.Content);
        Assert.Equal(1, taken());
    }

    [Fact]
    public async Task WithoutAToolPipeline_ARequestOfferingTools_IsNotRetried()
    {
        // Whether a tool ran cannot be told without the pipeline, so the request is not sent a second time.
        var model = new Scripted(Fail, CallStep, Done);
        var (loop, taken) = Build(model, withPipeline: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => loop.RunAsync("take one item", Ct));
        Assert.Equal(0, taken());
    }
}
