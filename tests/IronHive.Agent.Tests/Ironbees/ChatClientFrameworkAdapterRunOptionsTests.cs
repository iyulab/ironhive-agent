using Ironbees.Core;
using Ironbees.Core.Streaming;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Ironbees;

/// <summary>
/// The structured surface of <see cref="ChatClientFrameworkAdapter"/>. Until it existed the adapter took the
/// interface's default, which refuses every per-request option — so a sub-agent asking for a token cap, a turn
/// limit or a model's reasoning level got <see cref="NotSupportedException"/> on this adapter.
/// </summary>
public class ChatClientFrameworkAdapterRunOptionsTests
{
    /// <summary>What this adapter applies per request.</summary>
    private static readonly HashSet<string> Honoured =
    [
        nameof(AgentRunOptions.MaxTokens),
        nameof(AgentRunOptions.MaxToolTurns),
        nameof(AgentRunOptions.ThinkingEffort),
        nameof(AgentRunOptions.Tools),
    ];

    /// <summary>What it refuses: it has no suggestion pass.</summary>
    private static readonly HashSet<string> Refused = [nameof(AgentRunOptions.Suggestions)];

    private static AgentConfig Config(List<string>? tools = null) => new()
    {
        Name = "worker",
        Description = "test",
        Version = "1.0.0",
        SystemPrompt = "You are a test assistant.",
        Model = new ModelConfig { Deployment = "test-model", MaxTokens = 1000 },
        Tools = tools,
    };

    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => $"{name}-result", name);

    [Fact]
    public void EveryRunOption_IsEitherHonouredOrRefused()
    {
        var properties = typeof(AgentRunOptions).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Empty(Honoured.Intersect(Refused));
        Assert.True(properties.SetEquals(Honoured.Union(Refused)),
            "AgentRunOptions gained or lost a property. Decide what ChatClientFrameworkAdapter does with it and record it here. " +
            $"Unclassified: [{string.Join(", ", properties.Except(Honoured.Union(Refused)))}]; " +
            $"stale: [{string.Join(", ", Honoured.Union(Refused).Except(properties))}].");
    }

    [Fact]
    public async Task MaxToolTurns_StopsTheLoop_AndTheResultSaysSo()
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse("lookup", "{}", "thinking about it")
            .EnqueueToolCallResponse("lookup", "{}", "still looking")
            .EnqueueResponse("never reached");
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => [Tool("lookup")]);
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);

        var result = await adapter.RunStructuredAsync(
            agent, "go", options: new AgentRunOptions { MaxToolTurns = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.TurnLimitReached);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Equal(2, client.ReceivedMessages.Count);
    }

    [Fact]
    public async Task AFinishedRun_ReportsTurnsAndSummedUsage()
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse("lookup", "{}")
            .EnqueueResponse("done", new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2 });
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => [Tool("lookup")]);
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);

        var result = await adapter.RunStructuredAsync(agent, "go", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("done", result.Text);
        Assert.False(result.TurnLimitReached);
        Assert.Equal(2, result.TurnsUsed);
        Assert.Equal(5, result.Usage!.InputTokenCount);
        Assert.Equal(2, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task MaxTokens_AndThinkingEffort_ReachTheModelCall()
    {
        var client = new MockChatClient().EnqueueResponse("ok");
        var adapter = new ChatClientFrameworkAdapter(client);
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);

        await adapter.RunStructuredAsync(
            agent, "go",
            options: new AgentRunOptions { MaxTokens = 64, ThinkingEffort = ThinkingEffort.High },
            cancellationToken: TestContext.Current.CancellationToken);

        var sent = client.ReceivedOptions.Single()!;
        Assert.Equal(64, sent.MaxOutputTokens);
        Assert.Equal(ReasoningEffort.High, sent.Reasoning!.Effort);
    }

    [Fact]
    public async Task AnAgentThatNamesItsTools_GetsExactlyThose()
    {
        var client = new MockChatClient().EnqueueResponse("ok");
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => [Tool("read"), Tool("write"), Tool("delete")]);
        var agent = await adapter.CreateAgentAsync(Config(tools: ["read"]), TestContext.Current.CancellationToken);

        await adapter.RunStructuredAsync(agent, "go", cancellationToken: TestContext.Current.CancellationToken);

        var offered = client.ReceivedOptions.Single()!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(["read"], offered);
    }

    [Fact]
    public async Task AnAgentThatNamesAToolThePoolLacks_FailsWithTheName()
    {
        var adapter = new ChatClientFrameworkAdapter(_ => new MockChatClient(), () => [Tool("read")]);
        var agent = await adapter.CreateAgentAsync(Config(tools: ["read", "teleport"]), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.RunStructuredAsync(agent, "go", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("teleport", ex.Message);
    }

    [Fact]
    public async Task PerRequestTools_ReplaceTheAgentsTools()
    {
        var client = new MockChatClient().EnqueueResponse("ok");
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => [Tool("read")]);
        var agent = await adapter.CreateAgentAsync(Config(tools: ["read"]), TestContext.Current.CancellationToken);

        await adapter.RunStructuredAsync(
            agent, "go", options: new AgentRunOptions { Tools = [Tool("scoped")] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["scoped"], client.ReceivedOptions.Single()!.Tools!.Select(t => t.Name).ToList());
    }

    [Fact]
    public async Task Suggestions_AreRefused_FromBothHalves_AtCallTime()
    {
        var client = new MockChatClient().EnqueueResponse("never");
        var adapter = new ChatClientFrameworkAdapter(client);
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);
        var options = new AgentRunOptions { Suggestions = new SuggestionRequest() };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.RunStructuredAsync(agent, "go", null, options, TestContext.Current.CancellationToken));
        Assert.Throws<NotSupportedException>(
            () => adapter.StreamStructuredAsync(agent, "go", null, options, TestContext.Current.CancellationToken));
        Assert.Empty(client.ReceivedMessages);
    }

    [Fact]
    public async Task Streaming_AtTheTurnLimit_EndsWithAnUnsuccessfulCompletion()
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse("lookup", "{}", "partial")
            .EnqueueResponse("never reached");
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => [Tool("lookup")]);
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in adapter.StreamStructuredAsync(
            agent, "go", options: new AgentRunOptions { MaxToolTurns = 1 }, cancellationToken: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var completion = Assert.IsType<CompletionChunk>(chunks[^1]);
        Assert.False(completion.Success);
        Assert.Equal("tool_turn_limit", completion.FinishReason);
    }

    [Fact]
    public async Task MaxToolTurnsBelowOne_IsRejected()
    {
        var adapter = new ChatClientFrameworkAdapter(new MockChatClient());
        var agent = await adapter.CreateAgentAsync(Config(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.RunStructuredAsync(
            agent, "go", options: new AgentRunOptions { MaxToolTurns = 0 }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
