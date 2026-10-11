using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

public class RepeatedResultGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Parts of a document the model reads one at a time; <see cref="Edit"/> changes a part.</summary>
    private sealed class Document
    {
        private readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal)
        {
            ["1"] = "part one: GV-01 measure alert thresholds every 4 weeks",
            ["2"] = "part two: GV-02 review access lists every quarter",
        };

        public int Reads;

        public string Read(string part)
        {
            Reads++;
            return _parts[part];
        }

        public string Edit(string part)
        {
            _parts[part] += " (revised)";
            return "ok";
        }
    }

    private static (AgentLoop Loop, MockChatClient Mock, Document Doc) Build(ToolInvocationOptions options, Action<MockChatClient> script)
    {
        var doc = new Document();
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(options)).Build();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(doc.Read, "read_part"),
            AIFunctionFactory.Create(doc.Edit, "edit_part"),
        };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), mock, doc);
    }

    private static MockChatClient Read(MockChatClient mock, string part) =>
        mock.EnqueueToolCallResponse("read_part", $$"""{"part":"{{part}}"}""");

    [Fact]
    public async Task Re_reading_in_rotation_ends_the_request_on_the_third_visit_with_the_cause()
    {
        // The thrash shape: each new read pushed the other out, so the model rotates 1 → 2 → 1 → 2 → 1.
        var (loop, mock, doc) = Build(new ToolInvocationOptions(), m =>
        {
            Read(Read(Read(Read(Read(m, "1"), "2"), "1"), "2"), "1").EnqueueResponse("never requested");
        });

        var response = await loop.RunAsync("extract every part", Ct);

        doc.Reads.Should().Be(5);
        mock.ReceivedMessages.Should().HaveCount(5); // the model is not asked again after the fifth read
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        var refusal = loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
            .Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.RepeatedResult);
        refusal.Message.Should().Contain("'read_part'").And.Contain("3 times").And.Contain("ObservationMaskingProtectedTokens");
    }

    [Fact]
    public async Task A_re_read_whose_result_changed_does_not_count()
    {
        var (loop, _, doc) = Build(new ToolInvocationOptions(), m =>
        {
            var script = Read(m, "1");
            script = Read(script.EnqueueToolCallResponse("edit_part", """{"part":"1"}"""), "1");
            script = Read(script.EnqueueToolCallResponse("edit_part", """{"part":"1"}"""), "1");
            script.EnqueueResponse("done");
        });

        var response = await loop.RunAsync("revise part one twice", Ct);

        doc.Reads.Should().Be(3);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task Consecutive_identical_calls_are_one_visit_and_stay_with_the_repeated_call_guard()
    {
        var (loop, mock, doc) = Build(new ToolInvocationOptions(), m =>
        {
            Read(Read(Read(Read(m, "1"), "1"), "1"), "1").EnqueueResponse("done");
        });

        var response = await loop.RunAsync("read part one", Ct);

        doc.Reads.Should().Be(3); // the fourth is the repeated-call guard's refusal
        var fourth = mock.ReceivedMessages[4].SelectMany(x => x.Contents.OfType<FunctionResultContent>()).Last();
        fourth.Result.Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task MaxRepeatedResults_0_turns_the_guard_off()
    {
        var (loop, _, doc) = Build(new ToolInvocationOptions { MaxRepeatedResults = 0 }, m =>
        {
            Read(Read(Read(Read(Read(m, "1"), "2"), "1"), "2"), "1").EnqueueResponse("done");
        });

        var response = await loop.RunAsync("extract every part", Ct);

        doc.Reads.Should().Be(5);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task Visits_from_an_earlier_request_do_not_count()
    {
        // One conversation: the first request visits part one twice (part two between); the second asks again and reads it
        // once. That read is the request's first visit, not the conversation's third — the same reads inside one request
        // end it (Re_reading_in_rotation_ends_the_request_on_the_third_visit_with_the_cause).
        var (loop, _, doc) = Build(new ToolInvocationOptions(), m =>
        {
            Read(Read(Read(Read(m, "1"), "2"), "1"), "2").EnqueueResponse("first answer");
            Read(m, "1").EnqueueResponse("second answer");
        });

        var first = await loop.RunAsync("read the parts", Ct);
        var second = await loop.RunAsync("read part one again", Ct);

        first.StopReason.Should().Be(TurnStopReason.Completed);
        second.StopReason.Should().Be(TurnStopReason.Completed);
        second.Content.Should().Be("second answer");
        doc.Reads.Should().Be(5);
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Should().NotContain(r => r.Result is ToolCallRefusal);
    }

    [Fact]
    public async Task A_streak_does_not_continue_across_requests_for_the_repeated_call_guard()
    {
        // The repeated-call guard reads the same request-scoped history: a request that ended on three identical reads does
        // not make the next request's first identical read the fourth of a streak.
        var (loop, _, doc) = Build(new ToolInvocationOptions(), m =>
        {
            Read(Read(Read(m, "1"), "1"), "1").EnqueueResponse("first answer"); // three in a row: at, not over, the threshold
            Read(m, "1").EnqueueResponse("second answer");
        });

        await loop.RunAsync("read part one", Ct);
        var second = await loop.RunAsync("read part one again", Ct);

        second.Content.Should().Be("second answer");
        doc.Reads.Should().Be(4);
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Should().NotContain(r => r.Result is ToolCallRefusal);
    }

    [Fact]
    public void A_goal_reminder_does_not_start_a_new_request()
    {
        // The goal reminder is a user-role message added mid-request; calls before it still belong to the request.
        var reminder = new ChatMessage(ChatRole.User, "reminder")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [IronHive.Agent.Context.ContextManager.InjectedBlockKey] = "goal_reminder" },
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "earlier request"),
            Call("a", "read_part"), Result("a", "x"),
            new(ChatRole.User, "this request"),
            Call("b", "read_part"), Result("b", "x"),
            reminder,
            Call("c", "read_part"), Result("c", "x"),
        };

        var completed = ToolCallHistory.Completed(messages, excludeCallId: null);

        completed.Select(c => c.Call.CallId).Should().Equal("b", "c");
    }

    private static ChatMessage Call(string id, string name) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, name, new Dictionary<string, object?> { ["part"] = "1" })]);

    private static ChatMessage Result(string id, string text) =>
        new(ChatRole.Tool, [new FunctionResultContent(id, text)]);
}
