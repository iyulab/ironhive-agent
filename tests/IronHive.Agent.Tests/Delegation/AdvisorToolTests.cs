using Ironbees.Core;
using IronHive.Agent.Delegation;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Delegation;

public class AdvisorToolTests
{
    private static string AdvisorTranscript(MockChatClient advisor) =>
        advisor.ReceivedMessages.Single()[^1].Text ?? string.Empty;

    /// <summary>
    /// Through a real <see cref="FunctionInvokingChatClient"/>: the working model calls the advisor with no arguments,
    /// the advisor model receives the conversation (and no tools), and its advice is the tool result the working
    /// model reads next.
    /// </summary>
    [Fact]
    public async Task UnderFunctionInvokingChatClient_TheAdvisorSeesTheConversation_AndItsAdviceReachesTheWorker()
    {
        var advisor = new MockChatClient().EnqueueResponse("Check the empty-input case before finishing.",
            new UsageDetails { InputTokenCount = 40, OutputTokenCount = 10 });
        var tracker = new UsageTracker();
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions { UsageTracker = tracker });

        var worker = new MockChatClient()
            .EnqueueToolCallResponse("advisor", "{}", "Let me check my plan.")
            .EnqueueResponse("Done, with the empty-input case handled.");
        using var client = worker.AsBuilder().UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "You fix bugs."), new ChatMessage(ChatRole.User, "Fix the parser bug.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.EndsWith("Done, with the empty-input case handled.", response.Text);

        var transcript = AdvisorTranscript(advisor);
        Assert.Contains("Fix the parser bug.", transcript);
        Assert.Contains("You fix bugs.", transcript);
        Assert.Contains("Let me check my plan.", transcript);
        Assert.Null(advisor.ReceivedOptions.Single()!.Tools);

        var advice = worker.ReceivedMessages[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.Contains("empty-input case", advice.Result?.ToString());
        Assert.Equal(50, tracker.GetSessionUsage().TotalTokens);
    }

    /// <summary>
    /// Through the Ironbees adapter's own loop, which is not FunctionInvokingChatClient: the advisor still sees the
    /// conversation, so it works for a named agent too.
    /// </summary>
    [Fact]
    public async Task UnderTheIronbeesAdapterLoop_TheAdvisorSeesTheConversation()
    {
        var advisor = new MockChatClient().EnqueueResponse("Looks right.");
        var tool = AdvisorTool.Create(advisor);
        var worker = new MockChatClient()
            .EnqueueToolCallResponse("advisor", "{}")
            .EnqueueResponse("finished");
        var adapter = new ChatClientFrameworkAdapter(_ => worker, () => [tool]);
        var agent = await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "worker", Description = "d", Version = "1.0.0", SystemPrompt = "You work.",
            Model = new ModelConfig { Deployment = "m" },
        }, TestContext.Current.CancellationToken);

        var text = await adapter.RunAsync(agent, "Summarise the report.", TestContext.Current.CancellationToken);

        Assert.Equal("finished", text);
        Assert.Contains("Summarise the report.", AdvisorTranscript(advisor));
    }

    [Fact]
    public async Task CalledOutsideAnyToolLoop_WithoutAConversation_FailsAndSaysHowToFixIt()
    {
        var tool = AdvisorTool.Create(new MockChatClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("AdvisorOptions.Conversation", ex.Message);
    }

    [Fact]
    public async Task AnExplicitConversation_IsUsedWhereverTheToolIsCalled()
    {
        var advisor = new MockChatClient().EnqueueResponse("ok");
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions
        {
            Conversation = () => [new ChatMessage(ChatRole.User, "explicit history")],
        });

        await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("explicit history", AdvisorTranscript(advisor));
    }

    [Fact]
    public async Task PastMaxCalls_TheAdvisorIsNotConsulted_AndTheWorkerIsToldToProceed()
    {
        var advisor = new MockChatClient().EnqueueResponse("first advice").EnqueueResponse("never");
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions
        {
            MaxCalls = 1,
            Conversation = () => [new ChatMessage(ChatRole.User, "q")],
        });

        await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        var second = await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("already been consulted 1 times", second?.ToString());
        Assert.Single(advisor.ReceivedMessages);
    }

    [Fact]
    public async Task AtTheSessionUsageLimit_TheAdvisorIsNotConsulted()
    {
        var advisor = new MockChatClient().EnqueueResponse("never");
        var limiter = new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = 10, StopOnLimit = true });
        limiter.RecordTokenUsage(10, 0m);
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions
        {
            UsageLimiter = limiter,
            Conversation = () => [new ChatMessage(ChatRole.User, "q")],
        });

        var result = await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Contains("usage limit", result?.ToString());
        Assert.Empty(advisor.ReceivedMessages);
    }

    [Fact]
    public async Task TheAdvisorIsNeverGivenTools_EvenWhenItsOptionsCarryThem()
    {
        var advisor = new MockChatClient().EnqueueResponse("ok");
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions
        {
            ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => 1, "noop")], MaxOutputTokens = 300 },
            Conversation = () => [new ChatMessage(ChatRole.User, "q")],
        });

        await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        var sent = advisor.ReceivedOptions.Single()!;
        Assert.Null(sent.Tools);
        Assert.Equal(300, sent.MaxOutputTokens);
    }

    [Fact]
    public async Task TheTranscript_CutsLongToolResults_AndLeavesReasoningOut()
    {
        var conversation = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("private thoughts"), new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 50))]),
        };

        var advisor = new MockChatClient().EnqueueResponse("ok");
        var tool = AdvisorTool.Create(advisor, new AdvisorOptions { MaxToolResultChars = 10, Conversation = () => conversation });
        await tool.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);
        var transcript = AdvisorTranscript(advisor);

        Assert.Contains("-> calls read(", transcript);
        Assert.Contains("a.txt", transcript);
        Assert.Contains("40 more characters cut", transcript);
        Assert.DoesNotContain("private thoughts", transcript);
    }
}
