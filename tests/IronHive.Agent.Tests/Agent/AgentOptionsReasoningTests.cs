using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="AgentOptions.Reasoning"/> is a loop default: it reaches every model call of both loops, a per-turn
/// override replaces it, and the configured object is never handed to a call.
/// </summary>
public class AgentOptionsReasoningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentOptions LowReasoning() => new() { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };

    [Fact]
    public async Task AgentLoop_sends_the_configured_reasoning_with_every_call()
    {
        var mock = new MockChatClient().EnqueueResponse("one").EnqueueResponse("two");
        var options = LowReasoning();
        var loop = new AgentLoop(mock, options);

        await loop.RunAsync("first", Ct);
        await loop.RunAsync("second", Ct);

        mock.ReceivedOptions.Should().HaveCount(2).And.AllSatisfy(o => o!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low));
        mock.ReceivedOptions.Should().AllSatisfy(o => o!.Reasoning.Should().NotBeSameAs(options.Reasoning, "a call gets a copy"));
    }

    [Fact]
    public async Task A_per_turn_override_replaces_it_for_that_turn_only()
    {
        var mock = new MockChatClient().EnqueueResponse("one").EnqueueResponse("two");
        var loop = new AgentLoop(mock, LowReasoning());

        await loop.RunAsync("first", new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } }, Ct);
        await loop.RunAsync("second", Ct);

        mock.ReceivedOptions[0]!.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
        mock.ReceivedOptions[1]!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
    }

    [Fact]
    public async Task Unset_sends_no_reasoning()
    {
        var mock = new MockChatClient().EnqueueResponse("one");

        await new AgentLoop(mock, new AgentOptions()).RunAsync("first", Ct);

        mock.ReceivedOptions.Should().ContainSingle().Which!.Reasoning.Should().BeNull();
    }

    [Fact]
    public async Task ThinkingAgentLoop_sends_the_configured_reasoning_too()
    {
        var mock = new MockChatClient().EnqueueResponse("answer");
        var turnManager = Substitute.For<IThinkingTurnManager>();
        turnManager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(async call =>
            {
                var send = call.Arg<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>();
                var response = await send([new ChatMessage(ChatRole.User, "q")], Ct);
                return TurnResult.Success(response, TurnMetrics.Empty, null);
            });
        await using var loop = new ThinkingAgentLoop(mock, turnManager, LowReasoning());

        await loop.RunAsync("question", Ct);

        mock.ReceivedOptions.Should().ContainSingle().Which!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
    }
}
