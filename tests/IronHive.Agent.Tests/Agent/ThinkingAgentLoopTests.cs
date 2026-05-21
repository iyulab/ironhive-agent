using IndexThinking.Agents;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;
using IndexThinkingContent = IndexThinking.Core.ThinkingContent;

namespace IronHive.Agent.Tests.Agent;

public class ThinkingAgentLoopTests
{
    private static IThinkingTurnManager BuildTurnManager(ChatResponse response, IndexThinkingContent? thinking = null)
    {
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(response, TurnMetrics.Empty, thinking)));
        return manager;
    }

    [Fact]
    public async Task RunAsync_WithTextResponse_HasTextOutputIsTrue()
    {
        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello!")]);
        var loop = new ThinkingAgentLoop(new MockChatClient(), BuildTurnManager(chatResponse));

        var response = await loop.RunAsync("Hi");

        Assert.True(response.HasTextOutput);
        Assert.Equal("Hello!", response.Content);
    }

    [Fact]
    public async Task RunAsync_WithEmptyTextResponse_HasTextOutputIsFalse()
    {
        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, [])]);
        var loop = new ThinkingAgentLoop(new MockChatClient(), BuildTurnManager(chatResponse));

        var response = await loop.RunAsync("Think about this");

        Assert.False(response.HasTextOutput);
        Assert.Equal(string.Empty, response.Content);
    }

    [Fact]
    public async Task RunAsync_ThinkingOnlyTurn_HasTextOutputFalseAndThinkingContentPresent()
    {
        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, [])]);
        var thinking = new IndexThinkingContent { Text = "deep thought", TokenCount = 42 };
        var loop = new ThinkingAgentLoop(new MockChatClient(), BuildTurnManager(chatResponse, thinking));

        var response = await loop.RunAsync("Think");

        Assert.False(response.HasTextOutput);
        Assert.Equal(string.Empty, response.Content);
        Assert.NotNull(response.ThinkingContent);
        Assert.Equal("deep thought", response.ThinkingContent.Content);
    }

    [Fact]
    public async Task RunAsync_WithNormalResponse_HasTextOutputTrueAndThinkingContentPresent()
    {
        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Here is the answer.")]);
        var thinking = new IndexThinkingContent { Text = "I reasoned through it" };
        var loop = new ThinkingAgentLoop(new MockChatClient(), BuildTurnManager(chatResponse, thinking));

        var response = await loop.RunAsync("What is the answer?");

        Assert.True(response.HasTextOutput);
        Assert.Equal("Here is the answer.", response.Content);
        Assert.NotNull(response.ThinkingContent);
    }
}
