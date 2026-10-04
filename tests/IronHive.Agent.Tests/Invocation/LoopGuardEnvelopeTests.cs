using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// The loop guards' acceptance envelope in one place: the call sequences they must stop and the ones they must let
/// through, each with where it was seen. The guards have been tuned from both sides — consumer loops that ran forever
/// (tighten) and benchmark tasks cut short while making progress (loosen) — and a change made from one side's evidence
/// can silently undo the other's. A guard change adds its row here in the same commit.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>Sequence</term><description>Expected · seen in</description></listheader>
///   <item><term>Same target, alternating an optional argument</term><description>stopped (repeated call) · consumer: an image-description tool called on one photo 8× in a row</description></item>
///   <item><term>Identical call, identical result, past the refusals</term><description>request ends · consumer agent loops</description></item>
///   <item><term>Same failure thrown again and again</term><description>request ends (repeated error) · consumer: a failing tool called until the step budget</description></item>
///   <item><term>Read-only re-reads in rotation, same content</term><description>request ends (repeated result) · in-turn masking thrash</description></item>
///   <item><term>Same write, same content, back to back</term><description>stopped (repeated call) · benchmark overfull-hbox</description></item>
///   <item><term>Edit, re-run a failing check, edit, re-run…</term><description>runs · benchmark prove-plus-comm (fixed in 0.47.0)</description></item>
///   <item><term>Identical status call whose answer moves</term><description>runs · polling a job/boot that advances</description></item>
///   <item><term>Identical status call, same answer every time</term><description>stopped (repeated call) · the model is told to use the result</description></item>
///   <item><term>A tool whose answer differs every call</term><description>runs until the step budget · accepted cost of the line above</description></item>
/// </list>
/// </remarks>
public class LoopGuardEnvelopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        private readonly Queue<string> _status = new();
        private int _clock;
        public int Runs;

        public World(params string[] statuses)
        {
            foreach (var s in statuses)
            {
                _status.Enqueue(s);
            }
        }

        public string DescribeImage(string path, string question)
        {
            Runs++;
            return $"{path}: a photo ({question.Length})";
        }

        public string Status()
        {
            Runs++;
            return _status.Count > 1 ? _status.Dequeue() : _status.Peek();
        }

        public string Clock()
        {
            Runs++;
            return $"t={++_clock}";
        }

        public string Write(string path, string content)
        {
            Runs++;
            return "ok";
        }

        public string Edit(string part)
        {
            Runs++;
            return "edited";
        }

        public string Check()
        {
            Runs++;
            return "FAILED: 1 goal remaining";
        }

        public string Read(string part)
        {
            Runs++;
            return $"content of {part}";
        }

        public string Flaky(string input)
        {
            Runs++;
            throw new InvalidOperationException("backend unavailable");
        }
    }

    private static (AgentLoop Loop, MockChatClient Mock) Build(World world, Action<MockChatClient> script)
    {
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions())).Build();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(world.DescribeImage, "describe_image").WithTargetArguments(["path"]).WithReadOnly(true),
            AIFunctionFactory.Create(world.Status, "status").WithReadOnly(true),
            AIFunctionFactory.Create(world.Clock, "clock").WithReadOnly(true),
            AIFunctionFactory.Create(world.Write, "write_file").WithReadOnly(false),
            AIFunctionFactory.Create(world.Edit, "edit").WithReadOnly(false),
            AIFunctionFactory.Create(world.Check, "run_check").WithReadOnly(false),
            AIFunctionFactory.Create(world.Read, "read_part").WithReadOnly(true),
            AIFunctionFactory.Create(world.Flaky, "flaky"),
        };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), mock);
    }

    private static MockChatClient Calls(MockChatClient mock, params (string Tool, string Args)[] calls)
    {
        foreach (var (tool, args) in calls)
        {
            mock.EnqueueToolCallResponse(tool, args);
        }

        return mock;
    }

    private static IEnumerable<ToolCallRefusal> Refusals(AgentLoop loop) =>
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.Result).OfType<ToolCallRefusal>();

    private static (string, string) Repeat(string tool, string args) => (tool, args);

    // ---- must stop ----

    [Fact]
    public async Task Same_target_with_an_alternating_optional_argument_is_stopped()
    {
        var world = new World();
        var (loop, _) = Build(world, m => Calls(m,
            ("describe_image", """{"path":"a.jpg","question":"what is it"}"""),
            ("describe_image", """{"path":"a.jpg","question":"describe it in detail"}"""),
            ("describe_image", """{"path":"a.jpg","question":"what is it"}"""),
            ("describe_image", """{"path":"a.jpg","question":"describe it in detail"}""")).EnqueueResponse("done"));

        await loop.RunAsync("describe a.jpg", Ct);

        world.Runs.Should().Be(3);
        Refusals(loop).Should().ContainSingle().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
    }

    [Fact]
    public async Task Identical_call_and_result_past_the_refusals_ends_the_request()
    {
        var world = new World("booting");
        var args = "{}";
        var (loop, _) = Build(world, m => Calls(m,
            Repeat("status", args), Repeat("status", args), Repeat("status", args),
            Repeat("status", args), Repeat("status", args)).EnqueueResponse("never requested"));

        var response = await loop.RunAsync("wait for the boot", Ct);

        world.Runs.Should().Be(3);
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        Refusals(loop).Should().OnlyContain(r => r.Kind == ToolCallRefusalKind.RepeatedCall);
    }

    [Fact]
    public async Task The_same_failure_thrown_again_and_again_ends_the_request()
    {
        var world = new World();
        var (loop, _) = Build(world, m => Calls(m,
            ("flaky", """{"input":"a"}"""), ("flaky", """{"input":"b"}"""), ("flaky", """{"input":"c"}"""),
            ("flaky", """{"input":"d"}""")).EnqueueResponse("never requested"));

        var response = await loop.RunAsync("use the backend", Ct);

        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        world.Runs.Should().Be(3);
    }

    [Fact]
    public async Task Read_only_re_reads_in_rotation_end_the_request()
    {
        var world = new World();
        var (loop, _) = Build(world, m => Calls(m,
            ("read_part", """{"part":"1"}"""), ("read_part", """{"part":"2"}"""), ("read_part", """{"part":"1"}"""),
            ("read_part", """{"part":"2"}"""), ("read_part", """{"part":"1"}""")).EnqueueResponse("never requested"));

        var response = await loop.RunAsync("read every part", Ct);

        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        Refusals(loop).Last().Kind.Should().Be(ToolCallRefusalKind.RepeatedResult);
    }

    [Fact]
    public async Task The_same_write_back_to_back_is_stopped()
    {
        var world = new World();
        var write = """{"path":"input.tex","content":"same text"}""";
        var (loop, _) = Build(world, m => Calls(m,
            Repeat("write_file", write), Repeat("write_file", write), Repeat("write_file", write), Repeat("write_file", write))
            .EnqueueResponse("done"));

        await loop.RunAsync("fix the file", Ct);

        world.Runs.Should().Be(3);
        Refusals(loop).Should().ContainSingle().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
    }

    // ---- must run ----

    [Fact]
    public async Task Edit_then_rerun_a_failing_check_runs_to_completion()
    {
        var world = new World();
        var (loop, _) = Build(world, m => Calls(m,
            ("run_check", "{}"), ("edit", """{"part":"proof"}"""),
            ("run_check", "{}"), ("edit", """{"part":"proof"}"""),
            ("run_check", "{}"), ("edit", """{"part":"proof"}"""),
            ("run_check", "{}")).EnqueueResponse("done"));

        var response = await loop.RunAsync("prove it", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
        world.Runs.Should().Be(7);
        Refusals(loop).Should().BeEmpty();
    }

    [Fact]
    public async Task An_identical_status_call_whose_answer_moves_runs()
    {
        var world = new World("downloading 10%", "downloading 40%", "downloading 70%", "downloading 90%", "done");
        var (loop, _) = Build(world, m => Calls(m,
            Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"))
            .EnqueueResponse("downloaded"));

        var response = await loop.RunAsync("wait for the download", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
        world.Runs.Should().Be(5);
        Refusals(loop).Should().BeEmpty();
    }

    [Fact]
    public async Task An_identical_status_call_with_the_same_answer_is_stopped_after_the_threshold()
    {
        var world = new World("downloading 10%", "booting", "booting", "booting", "booting");
        var (loop, _) = Build(world, m => Calls(m,
            Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"), Repeat("status", "{}"))
            .EnqueueResponse("done"));

        await loop.RunAsync("wait for the boot", Ct);

        // 10% then three identical "booting" answers: the fifth call is the first refused — the moving answer did not count.
        world.Runs.Should().Be(4);
        Refusals(loop).Should().ContainSingle().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
    }

    [Fact]
    public async Task A_tool_whose_answer_differs_every_call_runs_until_the_step_budget()
    {
        // The accepted cost of counting only unchanged answers: nothing here stops it but the loop's own step budget.
        var world = new World();
        var (loop, _) = Build(world, m => Calls(m,
            Repeat("clock", "{}"), Repeat("clock", "{}"), Repeat("clock", "{}"), Repeat("clock", "{}"), Repeat("clock", "{}"),
            Repeat("clock", "{}")).EnqueueResponse("done"));

        var response = await loop.RunAsync("what time is it", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
        world.Runs.Should().Be(6);
    }
}
