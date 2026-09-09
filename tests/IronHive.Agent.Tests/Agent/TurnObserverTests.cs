using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// The post-turn seam: what a turn did, handed to whoever wants to check it against what the turn
/// said.
///
/// <para>
/// The loop already correlated the model's text with the tools it called — it builds the turn's
/// history message from exactly those two things — and then dropped the correlation. A consumer
/// wanting to ask "does this answer match what actually happened?" could only watch the streamed
/// output and separately instrument its own tools, rebuilding outside the loop the state the loop
/// held. The streaming path was the real gap: <c>RunAsync</c> already returned
/// <c>AgentResponse.ToolCalls</c>, while <c>RunStreamingAsync</c> yielded tool calls one delta at a
/// time and never a consolidated record.
/// </para>
/// <para>
/// The seam observes and appends; it never edits. By the time a streamed turn is complete every text
/// delta has already been yielded, so an amend-in-flight capability would work on one entry point and
/// silently not fire on the other.
/// </para>
/// </summary>
public class TurnObserverTests
{
    private sealed class RecordingObserver(string? addendum = null) : ITurnObserver
    {
        public List<TurnRecord> Seen { get; } = [];

        public ValueTask<string?> OnTurnCompletedAsync(TurnRecord turn, CancellationToken cancellationToken = default)
        {
            Seen.Add(turn);
            return ValueTask.FromResult(addendum);
        }
    }

    private static IThinkingTurnManager BuildTurnManager(ChatResponse response)
    {
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(
                Arg.Any<ThinkingContext>(),
                Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(response, TurnMetrics.Empty, null)));
        return manager;
    }

    private static async Task<List<AgentResponseChunk>> CollectAsync(IAgentLoop loop, string prompt = "q")
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in loop.RunStreamingAsync(prompt, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    // ---------------------------------------------------------------- the streaming record (ask 3)

    [Fact]
    public async Task Streaming_ClosesWithTheTurnsConsolidatedToolRecord()
    {
        var client = new MockChatClient()
            .EnqueueToolCallResponse("write_file", """{"path":"a.txt"}""", "Saved it.");
        var loop = new AgentLoop(client);

        var chunks = await CollectAsync(loop);

        var final = chunks[^1];
        final.Turn.Should().NotBeNull("the stream must end with a chunk carrying the turn record");
        final.Turn!.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be("write_file",
                "a consumer checking narration against tool activity should not have to reassemble " +
                "this from the individual ToolCallDelta chunks");
        final.Turn.Content.Should().Be("Saved it.");
    }

    [Fact]
    public async Task Streaming_EmitsTheFinalChunkEvenWhenTheProviderReportedNoUsage()
    {
        var client = new MockChatClient().EnqueueResponse("hello");
        var loop = new AgentLoop(client);

        var chunks = await CollectAsync(loop);

        chunks[^1].Turn.Should().NotBeNull(
            "the record must not be conditional on usage being present -- a consumer that reads it " +
            "would then work against one provider and silently not against another");
        chunks[^1].Usage.Should().BeNull();
    }

    [Fact]
    public async Task Streaming_ReportsAnExecutedCallsOutcomeWhenSomethingActuallyRanIt()
    {
        var client = new MockChatClient()
            .EnqueueResolvedToolCallResponse("send_message", "{}", "delivered");
        var loop = new AgentLoop(client);

        var chunks = await CollectAsync(loop);

        var call = chunks[^1].Turn!.ToolCalls.Should().ContainSingle().Subject;
        call.Success.Should().BeTrue();
        call.Result.Should().Be("delivered");
    }

    [Fact]
    public async Task Streaming_LeavesTheOutcomeUnknownWhenNothingInvokedTheCall()
    {
        var client = new MockChatClient().EnqueueToolCallResponse("send_message", "{}");
        var loop = new AgentLoop(client);

        var chunks = await CollectAsync(loop);

        chunks[^1].Turn!.ToolCalls.Should().ContainSingle()
            .Which.Success.Should().BeNull(
                "the loop extracts the call the model requested, it does not run it -- claiming " +
                "success here is the kind of confident wrong answer this whole seam exists to catch");
    }

    // ---------------------------------------------------------------- the observer (ask 2)

    [Fact]
    public async Task AnObserverSeesTheTurnsTextAndItsToolRecord_Streaming()
    {
        var observer = new RecordingObserver();
        var client = new MockChatClient()
            .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""", "I read it.");
        var loop = new AgentLoop(client, turnObservers: [observer]);

        await CollectAsync(loop);

        var turn = observer.Seen.Should().ContainSingle().Subject;
        turn.Content.Should().Be("I read it.");
        turn.ToolCalls.Should().ContainSingle().Which.ToolName.Should().Be("read_file");
    }

    [Fact]
    public async Task AnObserverSeesTheTurnsTextAndItsToolRecord_NonStreaming()
    {
        var observer = new RecordingObserver();
        var client = new MockChatClient()
            .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""", "I read it.");
        var loop = new AgentLoop(client, turnObservers: [observer]);

        await loop.RunAsync("q", TestContext.Current.CancellationToken);

        var turn = observer.Seen.Should().ContainSingle().Subject;
        turn.Content.Should().Be("I read it.");
        turn.ToolCalls.Should().ContainSingle().Which.ToolName.Should().Be("read_file");
    }

    [Fact]
    public async Task AnAddendumReachesTheConsumerOnTheFinalChunk()
    {
        var client = new MockChatClient().EnqueueResponse("Task registered.");
        var loop = new AgentLoop(client, turnObservers: [new RecordingObserver("(no tool was called)")]);

        var chunks = await CollectAsync(loop);

        chunks[^1].TextDelta.Should().Be("(no tool was called)",
            "a consumer relaying a stream cannot inject a correction into it -- the loop can, which " +
            "is the half of the amend ask that survives streaming");
        chunks[^1].Turn!.Content.Should().Be("Task registered.",
            "the record keeps the model's own words; the addendum is not folded into it");
    }

    [Fact]
    public async Task AnAddendumIsAppendedToTheResponseAndStaysSeparatelyReadable()
    {
        var client = new MockChatClient().EnqueueResponse("Task registered.");
        var loop = new AgentLoop(client, turnObservers: [new RecordingObserver("(no tool was called)")]);

        var response = await loop.RunAsync("q", TestContext.Current.CancellationToken);

        response.Content.Should().Be("Task registered.\n\n(no tool was called)",
            "a consumer that only renders Content must still see the correction");
        response.Addendum.Should().Be("(no tool was called)",
            "and one that cares must be able to tell it apart from what the model said");
    }

    [Fact]
    public async Task AnAddendumIsNotWrittenIntoHistory()
    {
        var client = new MockChatClient().EnqueueResponse("Task registered.");
        IAgentLoop loop = new AgentLoop(client, turnObservers: [new RecordingObserver("(no tool was called)")]);

        await loop.RunAsync("q", TestContext.Current.CancellationToken);
        var history = await loop.GetHistoryAsync(TestContext.Current.CancellationToken);

        history[^1].Text.Should().NotContain("no tool was called",
            "the model never said it; feeding a fabricated assistant utterance back into the next " +
            "turn's context would be worse than the claim it corrects");
    }

    [Fact]
    public async Task SeveralObserversAreJoinedInRegistrationOrder()
    {
        var client = new MockChatClient().EnqueueResponse("done");
        var loop = new AgentLoop(client, turnObservers:
            [new RecordingObserver("first"), new RecordingObserver(null), new RecordingObserver("second")]);

        var response = await loop.RunAsync("q", TestContext.Current.CancellationToken);

        response.Addendum.Should().Be("first\n\nsecond",
            "an observer that only observes contributes nothing to the output");
    }

    [Fact]
    public async Task WithNoObservers_NothingIsAppended()
    {
        var client = new MockChatClient().EnqueueResponse("done");
        var loop = new AgentLoop(client);

        var response = await loop.RunAsync("q", TestContext.Current.CancellationToken);

        response.Content.Should().Be("done");
        response.Addendum.Should().BeNull();
    }

    // ---------------------------------------------------------------- the peer implementation

    [Fact]
    public async Task ThinkingAgentLoop_HasTheSameSeam_NonStreaming()
    {
        var observer = new RecordingObserver("(checked)");
        var manager = BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "Task registered.")]));

        await using var loop = new ThinkingAgentLoop(
            new MockChatClient(), manager, turnObservers: [observer]);

        var result = await loop.RunAsync("q", TestContext.Current.CancellationToken);

        observer.Seen.Should().ContainSingle().Which.Content.Should().Be("Task registered.");
        result.Addendum.Should().Be("(checked)",
            "ThinkingAgentLoop is a peer implementation of IAgentLoop, not a wrapper -- a seam wired " +
            "into only one of them is a check that stops firing when a consumer switches loops");
    }

    [Fact]
    public async Task ThinkingAgentLoop_HasTheSameSeam_Streaming()
    {
        var observer = new RecordingObserver();
        var client = new MockChatClient().EnqueueToolCallResponse("read_file", "{}", "I read it.");
        var manager = BuildTurnManager(
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "I read it.")]));

        await using var loop = new ThinkingAgentLoop(client, manager, turnObservers: [observer]);

        var chunks = await CollectAsync(loop);

        chunks[^1].Turn.Should().NotBeNull();
        chunks[^1].Turn!.ToolCalls.Should().ContainSingle().Which.ToolName.Should().Be("read_file");
        observer.Seen.Should().ContainSingle().Which.Content.Should().Be("I read it.");
    }
}
