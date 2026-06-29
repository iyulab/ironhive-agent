using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Minimal IChatClient that streams a fixed list of updates verbatim, so a test can inject
    /// provider-native <see cref="TextReasoningContent"/> and assert how the loop bridges it.
    /// </summary>
    private sealed class FixedStreamClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var u in updates)
            {
                yield return u;
                await Task.Yield();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<(List<string> thinking, List<string> text)> CollectStreamAsync(ThinkingAgentLoop loop)
    {
        var thinking = new List<string>();
        var text = new List<string>();
        await foreach (var chunk in loop.RunStreamingAsync("q"))
        {
            if (!string.IsNullOrEmpty(chunk.ThinkingDelta))
            {
                thinking.Add(chunk.ThinkingDelta);
            }
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                text.Add(chunk.TextDelta);
            }
        }
        return (thinking, text);
    }

    [Fact]
    public async Task RunAsync_ResponseWithTextReasoningContent_PopulatesThinkingContent()
    {
        // Non-streaming: provider returns reasoning as M.E.AI TextReasoningContent with no
        // AdditionalProperties thinking blob. ExtractThinkingContent must fall back to it.
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant,
            [new TextReasoningContent("non-stream reasoning"), new TextContent("answer")])]);
        var loop = new ThinkingAgentLoop(new MockChatClient(), BuildTurnManager(response));

        var result = await loop.RunAsync("q");

        Assert.NotNull(result.ThinkingContent);
        Assert.Equal("non-stream reasoning", result.ThinkingContent.Content);
        Assert.Equal("answer", result.Content);
    }

    [Fact]
    public async Task RunStreamingAsync_BridgesProviderNativeTextReasoningContentToThinkingDelta()
    {
        // Provider streams reasoning as the M.E.AI-standard TextReasoningContent (not <think> tags,
        // not AdditionalProperties). ThinkingChatClient passes it through; the loop must bridge it to
        // ThinkingDelta so consumers (Filer) stop hand-splitting. RED today: ExtractStreamingThinking
        // only reads AdditionalProperties.
        var client = new FixedStreamClient(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextReasoningContent("thinking aloud")] },
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("the answer")] });
        var loop = new ThinkingAgentLoop(client, BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "the answer")])));

        var (thinking, text) = await CollectStreamAsync(loop);

        Assert.Contains("thinking aloud", thinking);
        Assert.Contains("the answer", text);
    }

    [Fact]
    public async Task RunStreamingAsync_LiveReasoningPlusMatchingTurnEnd_EmitsThinkingOnce()
    {
        // Live reasoning streams as TextReasoningContent; the turn-end metadata blob repeats the same
        // content (the collected updates are raw). Prefer-live dedup must emit thinking exactly once.
        var client = new FixedStreamClient(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextReasoningContent("reasoned")] },
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("answer")] });
        var loop = new ThinkingAgentLoop(client, BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]),
            new IndexThinkingContent { Text = "reasoned" }));

        var (thinking, _) = await CollectStreamAsync(loop);

        Assert.Equal(["reasoned"], thinking);
    }

    [Fact]
    public async Task RunStreamingAsync_NoLiveReasoning_EmitsTurnEndMetadataThinking()
    {
        // No live TextReasoningContent in the stream — only the turn-end AdditionalProperties blob.
        // This is the pre-live-separation path and must still work (regression guard).
        var client = new FixedStreamClient(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("answer")] });
        var loop = new ThinkingAgentLoop(client, BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]),
            new IndexThinkingContent { Text = "post-hoc reasoning" }));

        var (thinking, _) = await CollectStreamAsync(loop);

        Assert.Contains("post-hoc reasoning", thinking);
    }

    [Fact]
    public async Task RunStreamingAsync_ContinuationAppendsReasoning_EmitsLiveThenOnlyTail()
    {
        // Pins the prefix-tail LOGIC: when the turn-end blob is an exact superset of the live reasoning,
        // emit the live delta once then only the appended tail — no duplication, no loss. NOTE: this uses
        // synthetic prefix-aligned values; in practice the live (separator) and metadata (ParseReasoning)
        // texts come from different parsers and may NOT align — see ComputeMetadataThinkingTail's known
        // limitation. This test guards the tail computation, not real-world parser agreement.
        var client = new FixedStreamClient(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextReasoningContent("first round. ")] },
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("answer")] });
        var loop = new ThinkingAgentLoop(client, BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]),
            new IndexThinkingContent { Text = "first round. second round." }));

        var (thinking, _) = await CollectStreamAsync(loop);

        Assert.Equal(["first round. ", "second round."], thinking);
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
