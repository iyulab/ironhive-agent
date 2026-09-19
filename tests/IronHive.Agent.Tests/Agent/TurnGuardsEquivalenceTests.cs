using IndexThinking.Agents;
using IndexThinking.Extensions;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Exceptions;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// The per-turn safeguards — usage-limit stop, usage recording, one retry of a transient failure — must
/// behave the same on both loops. Until they were shared, only <see cref="AgentLoop"/> had them, and the
/// thinking loop (the one hosts prefer) silently ran past a configured limit.
/// </summary>
public class TurnGuardsEquivalenceTests
{
    public static TheoryData<string> Loops => new() { "agent", "thinking" };

    private static IAgentLoop Build(
        string kind, IChatClient client, IUsageLimiter? limiter = null, IErrorRecoveryService? recovery = null)
    {
        if (kind == "agent")
        {
            return new AgentLoop(client, errorRecovery: recovery, usageLimiter: limiter);
        }

        // The real turn manager, so both loops reach the same client through their own path.
        var services = new ServiceCollection();
        services.AddIndexThinkingAgents();
        services.AddIndexThinkingInMemoryStorage();
        var turnManager = services.BuildServiceProvider().GetRequiredService<IThinkingTurnManager>();
        return new ThinkingAgentLoop(client, turnManager, errorRecovery: recovery, usageLimiter: limiter);
    }

    private static async Task DrainAsync(IAgentLoop loop, string prompt)
    {
        await foreach (var _ in loop.RunStreamingAsync(prompt, TestContext.Current.CancellationToken))
        {
        }
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Buffered_LimitAlreadyReached_RefusesTheTurnWithoutCallingTheModel(string kind)
    {
        var client = new MockChatClient().EnqueueResponse("never");
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 10, StopOnLimit = true });
        limiter.RecordTokenUsage(10, 0m);
        var loop = Build(kind, client, limiter);

        await Assert.ThrowsAsync<UsageLimitExceededException>(
            () => loop.RunAsync("prompt", TestContext.Current.CancellationToken));
        Assert.Empty(client.ReceivedMessages);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Streaming_LimitAlreadyReached_RefusesTheTurnWithoutCallingTheModel(string kind)
    {
        var client = new MockChatClient().EnqueueResponse("never");
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 10, StopOnLimit = true });
        limiter.RecordTokenUsage(10, 0m);
        var loop = Build(kind, client, limiter);

        await Assert.ThrowsAsync<UsageLimitExceededException>(() => DrainAsync(loop, "prompt"));
        Assert.Empty(client.ReceivedMessages);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Buffered_TurnUsageIsFedIntoTheLimiter_SoTheNextTurnStops(string kind)
    {
        var client = new MockChatClient()
            .EnqueueResponse("first", new UsageDetails { InputTokenCount = 6, OutputTokenCount = 4 })
            .EnqueueResponse("never");
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 8, StopOnLimit = true });
        var loop = Build(kind, client, limiter);

        await loop.RunAsync("first", TestContext.Current.CancellationToken);
        Assert.Equal(10, limiter.GetCurrentUsage().Tokens);

        await Assert.ThrowsAsync<UsageLimitExceededException>(
            () => loop.RunAsync("second", TestContext.Current.CancellationToken));
        Assert.Single(client.ReceivedMessages);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Streaming_TurnUsageIsFedIntoTheLimiter(string kind)
    {
        var client = new MockChatClient()
            .EnqueueResponse("streamed", new UsageDetails { InputTokenCount = 6, OutputTokenCount = 4 });
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 1000, StopOnLimit = true });
        var loop = Build(kind, client, limiter);

        await DrainAsync(loop, "prompt");

        Assert.Equal(10, limiter.GetCurrentUsage().Tokens);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Buffered_TransientFailure_IsRetriedOnceAndTheTurnSucceeds(string kind)
    {
        var client = new FailFirstCallClient(
            new MockChatClient().EnqueueResponse("recovered"),
            new HttpRequestException("connection reset"));
        var recovery = new ErrorRecoveryService(new ErrorRecoveryConfig { DefaultRetryDelay = TimeSpan.Zero });
        var loop = Build(kind, client, recovery: recovery);

        var response = await loop.RunAsync("prompt", TestContext.Current.CancellationToken);

        Assert.Equal("recovered", response.Content);
        Assert.Equal(2, client.Calls);
        Assert.Single(recovery.GetSessionErrors());
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Buffered_TransientFailure_WithoutRecovery_Surfaces(string kind)
    {
        var client = new FailFirstCallClient(
            new MockChatClient().EnqueueResponse("never"),
            new HttpRequestException("connection reset"));
        var loop = Build(kind, client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => loop.RunAsync("prompt", TestContext.Current.CancellationToken));
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task Streaming_UsageFromEveryModelCallInTheTurn_IsSummed(string kind)
    {
        // Under function invocation one turn makes several model calls, each reporting its own usage. The turn's usage
        // is their sum; keeping only the last reported just the final round-trip.
        var client = new FixedStreamClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 5 })]),
            new ChatResponseUpdate(ChatRole.Assistant, "answer"),
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 120, OutputTokenCount = 7 })]));
        var loop = Build(kind, client);

        AgentResponseChunk? last = null;
        await foreach (var chunk in loop.RunStreamingAsync("prompt", TestContext.Current.CancellationToken))
        {
            last = chunk;
        }

        Assert.Equal(220, last!.Usage!.InputTokens);
        Assert.Equal(12, last.Usage.OutputTokens);
    }

    /// <summary>Streams a fixed list of updates verbatim.</summary>
    private sealed class FixedStreamClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                yield return update;
                await Task.Yield();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Fails the first buffered call with the given exception, then delegates.</summary>
    private sealed class FailFirstCallClient(IChatClient inner, Exception failure) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Calls == 1
                ? Task.FromException<ChatResponse>(failure)
                : inner.GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }
}
