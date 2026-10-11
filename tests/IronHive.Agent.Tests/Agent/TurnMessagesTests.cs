using System.Runtime.CompilerServices;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using IndexThinking.Agents;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="TurnRecord.Messages"/> / <see cref="AgentResponse.Messages"/>: what a turn added to the conversation, so a
/// consumer that keeps the conversation itself appends it instead of diffing the loop's history.
/// </summary>
public class TurnMessagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_turn_started_from_text_adds_the_user_message_then_the_answer()
    {
        var loop = new AgentLoop(new ScriptedModel("hello back"));

        var response = await loop.RunAsync("hello", Ct);

        Assert.Collection(response.Messages,
            m => Assert.Equal((ChatRole.User, "hello"), (m.Role, m.Text)),
            m => Assert.Equal((ChatRole.Assistant, "hello back"), (m.Role, m.Text)));
        // The very messages the loop keeps, not copies.
        Assert.Same(loop.History[^2], response.Messages[0]);
        Assert.Same(loop.History[^1], response.Messages[1]);
    }

    [Fact]
    public async Task A_streamed_turn_started_from_a_picture_carries_that_message_on_the_turn_record()
    {
        var loop = new AgentLoop(new ScriptedModel("a cat"));
        var question = new ChatMessage(ChatRole.User, [new TextContent("what is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        var turn = await LastTurnAsync(loop.RunStreamingAsync(question, Ct));

        Assert.Same(question, turn.Messages[0]);
        Assert.Equal((ChatRole.Assistant, "a cat"), (turn.Messages[1].Role, turn.Messages[1].Text));
        Assert.Equal(2, turn.Messages.Count);
    }

    [Fact]
    public async Task A_tool_round_adds_the_call_the_result_and_the_answer_in_history_order()
    {
        var clock = AIFunctionFactory.Create(() => "12:00", "clock", "Tells the time.");
        var loop = new AgentLoop(
            new FunctionInvokingChatClient(new ScriptedModel(new FunctionCallContent("c1", "clock"), "It is noon.")),
            new AgentOptions { Tools = [clock] });

        var turn = await LastTurnAsync(loop.RunStreamingAsync("what time is it?", Ct));

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], turn.Messages.Select(m => m.Role));
        Assert.Equal(loop.History.TakeLast(turn.Messages.Count).Select(Describe), turn.Messages.Select(Describe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_continuation_after_host_results_adds_only_what_the_model_produced(bool streaming)
    {
        var loop = new AgentLoop(new ScriptedModel("Lunch is bibimbap."));
        loop.InitializeHistory([
            new ChatMessage(ChatRole.User, "lunch?"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read_page")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "Lunch: bibimbap")]),
        ]);

        var added = streaming
            ? (await LastTurnAsync(loop.ContinueStreamingAsync(Ct))).Messages
            : (await loop.ContinueAsync(Ct)).Messages;

        var only = Assert.Single(added);
        Assert.Equal((ChatRole.Assistant, "Lunch is bibimbap."), (only.Role, only.Text));
    }

    [Fact]
    public async Task An_injected_goal_reminder_is_not_part_of_the_turn_and_a_stateless_round_trip_matches_the_loop()
    {
        // The reminder is a second user-role message the loop puts into its own history mid-turn: the case a consumer's
        // «after the last user message» diff cuts in half.
        var reminders = new GoalReminderOptions { Enabled = true, MinMessagesBeforeReminder = 1 };
        var model = new ScriptedModel("first answer", "second answer");
        var kept = new AgentLoop(model, contextManager: new ContextManager(new ContextTokenCounter("gpt-4o", 8000), goalReminderOptions: reminders));

        var record = new List<ChatMessage>();
        record.AddRange((await kept.RunAsync("Plan the migration", Ct)).Messages);
        var second = await kept.RunAsync("and the rollback?", Ct);
        record.AddRange(second.Messages);

        Assert.Contains(kept.History, ContextManager.IsInjected);
        Assert.DoesNotContain(second.Messages, ContextManager.IsInjected);
        Assert.Equal((ChatRole.User, "and the rollback?"), (second.Messages[0].Role, second.Messages[0].Text));
        Assert.Equal((ChatRole.Assistant, "second answer"), (second.Messages[^1].Role, second.Messages[^1].Text));
        // What the consumer recorded is the conversation without the loop's own sending aids.
        Assert.Equal(kept.History.Where(m => !ContextManager.IsInjected(m)).Select(Describe), record.Select(Describe));
    }

    [Fact]
    public async Task ThinkingAgentLoop_reports_the_same_messages()
    {
        var loop = new ThinkingAgentLoop(new ScriptedModel("answer"), TurnManagerAnswering("answer"));

        var response = await loop.RunAsync("q", Ct);

        Assert.Equal([(ChatRole.User, "q"), (ChatRole.Assistant, "answer")], response.Messages.Select(m => (m.Role, m.Text)));
        Assert.Equal(loop.History.TakeLast(2).Select(Describe), response.Messages.Select(Describe));
    }

    [Fact]
    public async Task ThinkingAgentLoop_streaming_reports_the_same_messages()
    {
        var loop = new ThinkingAgentLoop(new ScriptedModel("answer"), TurnManagerAnswering("answer"));

        var turn = await LastTurnAsync(loop.RunStreamingAsync("q", Ct));

        Assert.Equal([(ChatRole.User, "q"), (ChatRole.Assistant, "answer")], turn.Messages.Select(m => (m.Role, m.Text)));
    }

    private static IThinkingTurnManager TurnManagerAnswering(string answer)
    {
        var turnManager = Substitute.For<IThinkingTurnManager>();
        turnManager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(new ChatResponse([new ChatMessage(ChatRole.Assistant, answer)]), TurnMetrics.Empty, null)));
        return turnManager;
    }

    private static async Task<TurnRecord> LastTurnAsync(IAsyncEnumerable<AgentResponseChunk> chunks)
    {
        TurnRecord? turn = null;
        await foreach (var chunk in chunks)
        {
            turn = chunk.Turn ?? turn;
        }

        return turn ?? throw new InvalidOperationException("The stream carried no turn record.");
    }

    private static string Describe(ChatMessage message)
        => $"{message.Role}:{string.Join("|", message.Contents.Select(c => c switch
        {
            TextContent t => t.Text,
            FunctionCallContent call => "call " + call.Name,
            FunctionResultContent result => "result " + result.Result,
            _ => c.GetType().Name,
        }))}";

    /// <summary>Answers with each scripted step in turn; a step is a text or a tool call.</summary>
    private sealed class ScriptedModel(params object[] steps) : IChatClient
    {
        private int _next;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Next()));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = Next();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, message.Contents);
        }

        private ChatMessage Next() => steps[Math.Min(_next++, steps.Length - 1)] switch
        {
            FunctionCallContent call => new ChatMessage(ChatRole.Assistant, [call]),
            var text => new ChatMessage(ChatRole.Assistant, (string)text),
        };

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
