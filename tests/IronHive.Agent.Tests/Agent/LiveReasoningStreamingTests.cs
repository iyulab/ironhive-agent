using System.Runtime.CompilerServices;
using AwesomeAssertions;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// The plain <see cref="AgentLoop"/> streams a thinking model's reasoning as <see cref="AgentResponseChunk.ThinkingDelta"/>
/// while it arrives — the same bridge <see cref="ThinkingAgentLoop"/> has — so a host can show that the model is
/// thinking rather than an empty pane. The reasoning never enters the response text.
/// </summary>
public class LiveReasoningStreamingTests
{
    [Fact]
    public async Task RunStreamingAsync_ReasoningContent_StreamsAsThinkingDelta_AndStaysOutOfTheText()
    {
        var loop = new AgentLoop(new FixedStreamClient(
            new ChatResponseUpdate { Contents = [new TextReasoningContent("The user wants ")] },
            new ChatResponseUpdate { Contents = [new TextReasoningContent("a greeting.")] },
            new ChatResponseUpdate { Contents = [new TextContent("Hello!")] }));

        var thinking = new List<string>();
        var text = new List<string>();
        TurnRecord? turn = null;
        await foreach (var chunk in loop.RunStreamingAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
        {
            if (chunk.ThinkingDelta is { } t)
            {
                thinking.Add(t);
            }

            if (chunk.TextDelta is { } x)
            {
                text.Add(x);
            }

            turn ??= chunk.Turn;
        }

        thinking.Should().Equal("The user wants ", "a greeting.");
        text.Should().Equal("Hello!");
        turn.Should().NotBeNull();
        turn!.Content.Should().Be("Hello!");
    }

    [Fact]
    public async Task RunStreamingAsync_NoReasoning_YieldsNoThinkingDelta()
    {
        var loop = new AgentLoop(new FixedStreamClient(new ChatResponseUpdate { Contents = [new TextContent("Hello!")] }));

        await foreach (var chunk in loop.RunStreamingAsync("hi", cancellationToken: TestContext.Current.CancellationToken))
        {
            chunk.ThinkingDelta.Should().BeNull();
        }
    }

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
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
