using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// What a streamed turn leaves behind for the next turn to be sent with.
///
/// <para>
/// <c>RunAsync</c> keeps history by <c>_history.AddRange(response.Messages)</c> — when function
/// invocation is in play that includes the Tool-role result messages, in order. The streaming twin
/// rebuilt one assistant message from the deltas instead, appending every <c>FunctionCallContent</c>
/// and discarding the <c>FunctionResultContent</c> that followed each one. The next request would
/// then carry an assistant message announcing tool calls with no result answering them, which several
/// providers reject outright — and a provider that does not reject it is handed a conversation where
/// the tools appear never to have returned.
/// </para>
/// <para>
/// This is the same shape as the two defects the seam work already turned up: the two entry points
/// did the same job by two independently written rules, so they could disagree, and did.
/// </para>
/// </summary>
public class StreamingHistoryFidelityTests
{
    private static IThinkingTurnManager BuildTurnManager(ChatResponse response)
    {
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(
                Arg.Any<ThinkingContext>(),
                Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(response, TurnMetrics.Empty, null)));
        return manager;
    }

    private static async Task DrainAsync(IAgentLoop loop, string prompt)
    {
        await foreach (var _ in loop.RunStreamingAsync(prompt, TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task AStreamedToolTurn_KeepsTheToolResultInHistory()
    {
        var client = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", """{"path":"a.txt"}""", "file contents");
        IAgentLoop loop = new AgentLoop(client);

        await DrainAsync(loop, "read it");
        var history = await loop.GetHistoryAsync(TestContext.Current.CancellationToken);

        var calls = history.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        var results = history.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();

        calls.Should().ContainSingle();
        results.Should().ContainSingle(
            "an assistant message announcing a tool call with nothing answering it is a conversation " +
            "several providers reject, and one none of them can interpret correctly");
        results[0].CallId.Should().Be(calls[0].CallId);
    }

    [Fact]
    public async Task AStreamedToolTurn_KeepsHistoryInTheSameShapeAsTheNonStreamedTwin()
    {
        var streamed = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", "{}", "file contents");
        var direct = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", "{}", "file contents");

        IAgentLoop streamingLoop = new AgentLoop(streamed);
        IAgentLoop directLoop = new AgentLoop(direct);

        await DrainAsync(streamingLoop, "read it");
        await directLoop.RunAsync("read it", TestContext.Current.CancellationToken);

        var streamedRoles = (await streamingLoop.GetHistoryAsync(TestContext.Current.CancellationToken))
            .Select(m => m.Role.Value).ToList();
        var directRoles = (await directLoop.GetHistoryAsync(TestContext.Current.CancellationToken))
            .Select(m => m.Role.Value).ToList();

        streamedRoles.Should().Equal(directRoles,
            "the two entry points must leave the next turn the same conversation -- a consumer " +
            "switching to streaming should not silently change what the model is sent next");
    }

    [Fact]
    public async Task ThinkingAgentLoop_StreamedToolTurn_AlsoKeepsTheToolResult()
    {
        var client = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", "{}", "file contents");
        await using var loop = new ThinkingAgentLoop(
            client, BuildTurnManager(new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)])));

        await DrainAsync(loop, "read it");
        var history = await ((IAgentLoop)loop).GetHistoryAsync(TestContext.Current.CancellationToken);

        history.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Should().ContainSingle(
            "the peer implementation rebuilt history the same way, so it lost the same thing");
    }
}
