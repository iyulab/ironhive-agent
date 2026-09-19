using Ironbees.Core;
using IronHive.Agent.Delegation;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IronHive.Agent.Tests.Delegation;

public class DelegationToolsTests
{
    private static IAgent Definition(string name, string description = "Looks things up.")
    {
        var agent = Substitute.For<IAgent>();
        agent.Name.Returns(name);
        agent.Description.Returns(description);
        return agent;
    }

    private static IAgentOrchestrator Orchestrator(string name = "research", string description = "Looks things up.")
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var definition = Definition(name, description);
        orchestrator.GetAgent(name).Returns(definition);
        return orchestrator;
    }

    private static async Task<string> InvokeAsync(AIFunction tool, string task, string? context = null)
    {
        var args = new AIFunctionArguments { ["task"] = task };
        if (context is not null)
        {
            args["context"] = context;
        }

        var result = await tool.InvokeAsync(args, TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task ADelegation_RunsTheNamedAgent_WithTheOverridesAndTheTaskPlusContext()
    {
        var orchestrator = Orchestrator();
        ProcessOptions? sent = null;
        string? input = null;
        orchestrator.ProcessStructuredAsync(
                Arg.Do<string>(i => input = i), Arg.Do<ProcessOptions>(o => sent = o), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult { Text = "found it" });

        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent
        {
            AgentName = "research", Model = "fast-model", ThinkingEffort = ThinkingEffort.Low, MaxTokens = 500, MaxToolTurns = 4,
        });

        var output = await InvokeAsync(tool, "find the config file", "the repo is a .NET solution");

        Assert.Equal("found it", output);
        Assert.Equal("research", sent!.AgentName);
        Assert.Equal("fast-model", sent.ModelOverride);
        Assert.Equal(ThinkingEffort.Low, sent.ThinkingEffort);
        Assert.Equal(500, sent.MaxTokens);
        Assert.Equal(4, sent.MaxToolTurns);
        Assert.Contains("find the config file", input);
        Assert.Contains("the repo is a .NET solution", input);
    }

    [Fact]
    public void TheToolIsNamedAndDescribedFromTheAgentDefinition()
    {
        var tool = DelegationTools.Create(Orchestrator("code.review", "Reviews a diff."), new DelegatedAgent { AgentName = "code.review" });

        Assert.Equal("code_review", tool.Name);
        Assert.Equal("Reviews a diff.", tool.Description);
    }

    [Fact]
    public void AnAgentThatIsNotLoaded_FailsAtCreation_WithItsName()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "ghost" }));

        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void AnAgentWithNoDescription_FailsAtCreation_BecauseTheModelChoosesFromIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DelegationTools.Create(Orchestrator(description: ""), new DelegatedAgent { AgentName = "research" }));

        Assert.Contains("description", ex.Message);
    }

    [Fact]
    public async Task TheDelegatedRunsUsage_IsRecorded_AndCountsAgainstTheParentsLimit()
    {
        var orchestrator = Orchestrator();
        orchestrator.ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult { Text = "ok", Usage = new UsageDetails { InputTokenCount = 30, OutputTokenCount = 20 } });
        var tracker = new UsageTracker();
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 40, StopOnLimit = true });
        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research" },
            new DelegationOptions { UsageTracker = tracker, UsageLimiter = limiter });

        await InvokeAsync(tool, "first");

        Assert.Equal(50, tracker.GetSessionUsage().TotalTokens);
        Assert.Equal(50, limiter.GetCurrentUsage().Tokens);

        var refused = await InvokeAsync(tool, "second");
        Assert.Contains("usage limit", refused);
        await orchestrator.Received(1).ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARunThatHitItsTurnLimit_IsReportedAsPartial()
    {
        var orchestrator = Orchestrator();
        orchestrator.ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult { Text = "halfway", TurnsUsed = 3, TurnLimitReached = true });
        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research" });

        var output = await InvokeAsync(tool, "go");

        Assert.StartsWith("halfway", output);
        Assert.Contains("tool-turn limit (3 turns)", output);
        Assert.Contains("partial", output);
    }

    [Fact]
    public async Task AFailedRun_ComesBackAsAResultTheModelCanRead()
    {
        var orchestrator = Orchestrator();
        orchestrator.ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research" });

        var output = await InvokeAsync(tool, "go");

        Assert.Contains("failed", output);
        Assert.Contains("provider unavailable", output);
    }

    [Fact]
    public async Task CancellingTheParent_CancelsTheDelegation()
    {
        var orchestrator = Orchestrator();
        orchestrator.ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>()).ContinueWith<AgentRunResult>(
                t => throw new OperationCanceledException(ci.Arg<CancellationToken>()), TaskScheduler.Default));
        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research" });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var pending = tool.InvokeAsync(new AIFunctionArguments { ["task"] = "go" }, cts.Token).AsTask();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TheToolsOfOneSet_ShareTheConcurrencyLimit()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var a = Definition("a");
        var b = Definition("b");
        orchestrator.GetAgent("a").Returns(a);
        orchestrator.GetAgent("b").Returns(b);
        var release = new TaskCompletionSource<AgentRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        orchestrator.ProcessStructuredAsync(Arg.Any<string>(), Arg.Any<ProcessOptions>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                peak = Math.Max(peak, Interlocked.Increment(ref running));
                var result = await release.Task;
                Interlocked.Decrement(ref running);
                return result;
            });
        var tools = DelegationTools.Create(orchestrator,
            [new DelegatedAgent { AgentName = "a" }, new DelegatedAgent { AgentName = "b" }],
            new DelegationOptions { MaxConcurrent = 1 });

        var first = InvokeAsync(tools[0], "one");
        var second = InvokeAsync(tools[1], "two");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, running);

        release.SetResult(new AgentRunResult { Text = "ok" });
        await Task.WhenAll(first, second);
        Assert.Equal(1, peak);
    }

    /// <summary>
    /// End to end through a real orchestrator and adapter: an agent whose tools include a delegation to itself is
    /// refused at the depth limit, and the refusal reaches the model as the tool result.
    /// </summary>
    [Fact]
    public async Task NestedDelegation_PastTheDepthLimit_IsRefused_AndTheModelSeesWhy()
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse("research", """{"task":"dig deeper"}""")
            .EnqueueResponse("done without going deeper");
        IReadOnlyList<AITool> pool = [];
        var adapter = new ChatClientFrameworkAdapter(_ => client, () => pool.ToList());
        var registry = new AgentRegistry();
        registry.Register("research", await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "research",
            Description = "Looks things up.",
            Version = "1.0.0",
            SystemPrompt = "You research.",
            Model = new ModelConfig { Deployment = "m" },
        }, TestContext.Current.CancellationToken));
        var orchestrator = new AgentOrchestrator(
            Substitute.For<IAgentLoader>(), registry, adapter, Substitute.For<IAgentSelector>());

        var tool = DelegationTools.Create(orchestrator, new DelegatedAgent { AgentName = "research" },
            new DelegationOptions { MaxDepth = 1 });
        pool = [tool];

        var output = await InvokeAsync(tool, "look it up");

        Assert.Equal("done without going deeper", output);
        var toolResult = client.ReceivedMessages[1]
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single();
        Assert.Contains("refused", toolResult.Result?.ToString());
        Assert.Contains("limit 1", toolResult.Result?.ToString());
    }
}
