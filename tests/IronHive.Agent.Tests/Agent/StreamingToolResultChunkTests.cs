using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A streamed turn reports each tool call's outcome on its own chunk the moment the result arrives —
/// between the call's <see cref="AgentResponseChunk.ToolCallDelta"/> and the final <see cref="AgentResponseChunk.Turn"/> —
/// and that record is the same one the turn record carries for the call.
/// </summary>
public class StreamingToolResultChunkTests
{
    private static async Task<List<AgentResponseChunk>> CollectAsync(IAgentLoop loop, string prompt)
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in loop.RunStreamingAsync(prompt, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static void AssertResultChunkBetweenCallAndTurn(List<AgentResponseChunk> chunks)
    {
        var callIndex = chunks.FindIndex(c => c.ToolCallDelta is not null);
        var resultIndex = chunks.FindIndex(c => c.ToolResult is not null);
        var turnIndex = chunks.FindIndex(c => c.Turn is not null);

        callIndex.Should().BeGreaterThanOrEqualTo(0);
        resultIndex.Should().BeGreaterThan(callIndex, "the outcome follows the call it answers");
        turnIndex.Should().BeGreaterThan(resultIndex, "the outcome is reported before the turn closes, not only inside it");

        var result = chunks[resultIndex].ToolResult!;
        result.CallId.Should().Be(chunks[callIndex].ToolCallDelta!.Id);
        result.ToolName.Should().Be("read_file");
        result.Result.Should().Be("file contents");
        result.Success.Should().BeTrue();
        chunks[turnIndex].Turn!.ToolCalls.Should().ContainSingle().Which.Should().Be(result,
            "the per-call chunk and the turn record are built by one rule");
    }

    [Fact]
    public async Task AgentLoop_YieldsTheToolResult_AsItArrives()
    {
        var client = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", """{"path":"a.txt"}""", "file contents");

        var chunks = await CollectAsync(new AgentLoop(client), "read it");

        chunks.Count(c => c.ToolResult is not null).Should().Be(1);
        AssertResultChunkBetweenCallAndTurn(chunks);
    }

    [Fact]
    public async Task ThinkingAgentLoop_YieldsTheToolResult_AsItArrives()
    {
        var client = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", """{"path":"a.txt"}""", "file contents");
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(
                Arg.Any<ThinkingContext>(),
                Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)]), TurnMetrics.Empty, null)));
        await using var loop = new ThinkingAgentLoop(client, manager);

        var chunks = await CollectAsync(loop, "read it");

        chunks.Count(c => c.ToolResult is not null).Should().Be(1);
        AssertResultChunkBetweenCallAndTurn(chunks);
    }

    [Fact]
    public async Task ATextOnlyTurn_YieldsNoToolResult()
    {
        // Positive control for the assertion above: no call, no result chunk.
        var client = new MockChatClient().EnqueueResponse("hello");

        var chunks = await CollectAsync(new AgentLoop(client), "hi");

        chunks.Should().NotContain(c => c.ToolResult != null);
        chunks.Should().ContainSingle(c => c.Turn != null);
    }
}
