using IndexThinking.Agents;
using IndexThinking.Extensions;
using IronHive.Agent.Exceptions;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A usage limit is enforced on every model call of a turn — each tool round inside function invocation included — when
/// the pipeline carries a <see cref="UsageLimitChatClient"/>. Without it the loop checks once per turn, so a turn of many
/// tool rounds could run far past the limit.
/// </summary>
public class UsageLimitPerModelCallTests
{
    public static TheoryData<string> Loops => new() { "agent", "thinking" };

    private static readonly AITool Lookup = AIFunctionFactory.Create(() => "looked up", "lookup");

    private static IAgentLoop Build(string kind, IChatClient client, IUsageLimiter limiter)
    {
        var options = new AgentOptions { Tools = [Lookup] };
        if (kind == "agent")
        {
            return new AgentLoop(client, options, usageLimiter: limiter);
        }

        var services = new ServiceCollection();
        services.AddIndexThinkingAgents();
        services.AddIndexThinkingInMemoryStorage();
        var turnManager = services.BuildServiceProvider().GetRequiredService<IThinkingTurnManager>();
        return new ThinkingAgentLoop(client, turnManager, options, usageLimiter: limiter);
    }

    /// <summary>Function invocation outside, the usage limit inside it — the shape a host's decorator builds.</summary>
    private static IChatClient Pipeline(MockChatClient model) =>
        new ChatClientBuilder(model).UseFunctionInvocation().UseUsageLimit().Build();

    private static MockChatClient ToolRoundsOf600Tokens() => new MockChatClient()
        .EnqueueToolCallResponse("lookup", "{}", usage: new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 })
        .EnqueueToolCallResponse("lookup", "{}", usage: new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 })
        .EnqueueToolCallResponse("lookup", "{}", usage: new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 })
        .EnqueueResponse("done", new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 });

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task ATurnOfToolRounds_StopsAtTheModelCallAfterTheLimitIsReached(string kind)
    {
        var model = ToolRoundsOf600Tokens();
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 1000, StopOnLimit = true });
        var loop = Build(kind, Pipeline(model), limiter);

        await Assert.ThrowsAsync<UsageLimitExceededException>(() => loop.RunAsync("go", TestContext.Current.CancellationToken));

        // Round 1 (600) and round 2 (1,200 — over the limit) ran; round 3 was refused before reaching the model.
        Assert.Equal(2, model.ReceivedMessages.Count);
        Assert.Equal(1200, limiter.GetCurrentUsage().Tokens);
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task UsageIsCountedOncePerModelCall_NotAgainForTheTurn(string kind)
    {
        var model = ToolRoundsOf600Tokens();
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 100_000, StopOnLimit = true });
        var loop = Build(kind, Pipeline(model), limiter);

        await loop.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(4, model.ReceivedMessages.Count);
        Assert.Equal(2400, limiter.GetCurrentUsage().Tokens);
    }

    [Fact]
    public async Task WithoutTheClient_TheLoopStillChecksPerTurn_SoTheWholeTurnRunsPastTheLimit()
    {
        // The negative control: the same turn without UseUsageLimit is only stopped on the next turn.
        var model = ToolRoundsOf600Tokens();
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 1000, StopOnLimit = true });
        var loop = Build("agent", new ChatClientBuilder(model).UseFunctionInvocation().Build(), limiter);

        await loop.RunAsync("go", TestContext.Current.CancellationToken);

        Assert.Equal(4, model.ReceivedMessages.Count);
        Assert.True(limiter.GetCurrentUsage().Tokens >= 600, "the turn's usage is still recorded at the end of the turn");
    }

    [Fact]
    public void Bind_ASecondLoopsLimiter_IsRefused()
    {
        var client = new UsageLimitChatClient(new MockChatClient());
        var first = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 10 });
        client.Bind(first);
        client.Bind(first);

        Assert.Throws<InvalidOperationException>(() => client.Bind(new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 10 })));
    }
}
