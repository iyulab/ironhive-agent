using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// A call to a tool the request did not offer goes through the tool-invocation pipeline, so the loop guards count it:
/// a model stuck on an invented or withdrawn name is stopped like one that keeps failing an offered tool. Each fact
/// drives a real function-invoking client through an <see cref="AgentLoop"/>.
/// </summary>
public class UnknownToolCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NotFound = "Error: Requested function \"get_file_info\" not found.";

    private sealed class Files
    {
        public int Runs;

        public string ReadFile(string path)
        {
            Runs++;
            return $"content of {path}";
        }
    }

    private static (AgentLoop Loop, Files Files) Build(Action<MockChatClient> script, ToolInvocationPipeline? pipeline = null)
    {
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder()
            .UseToolInvocationPipeline(pipeline ?? ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions()))
            .Build();
        var files = new Files();
        var tools = new List<AITool> { AIFunctionFactory.Create(files.ReadFile, "read_file") };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), files);
    }

    private static IEnumerable<FunctionResultContent> Results(AgentLoop loop) =>
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>());

    private static MockChatClient Unknown(MockChatClient mock, int times)
    {
        for (var i = 0; i < times; i++)
        {
            mock.EnqueueToolCallResponse("get_file_info", """{"path":"a.txt"}""");
        }

        return mock;
    }

    [Fact]
    public async Task The_same_unknown_call_again_and_again_ends_the_request_at_the_repeated_error_limit()
    {
        var (loop, _) = Build(m => Unknown(m, 12).EnqueueResponse("never requested"));

        var response = await loop.RunAsync("inspect a.txt", Ct);

        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        var results = Results(loop).ToList();
        results.Should().HaveCount(3);
        results.Take(2).Should().OnlyContain(r => r.Result is ToolCallRefusal && ((ToolCallRefusal)r.Result!).Kind == ToolCallRefusalKind.UnknownTool);
        results.Last().Result.Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
    }

    [Fact]
    public async Task Below_the_limit_the_model_reads_the_same_not_found_text_as_before()
    {
        var (loop, _) = Build(m => Unknown(m, 1).EnqueueResponse("done"));

        var response = await loop.RunAsync("inspect a.txt", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
        Results(loop).Should().ContainSingle().Which.Result!.ToString().Should().Be(NotFound);
    }

    [Fact]
    public async Task A_mistyped_name_then_the_right_one_runs_the_right_one_without_a_refusal()
    {
        var (loop, files) = Build(m => Unknown(m, 1)
            .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
            .EnqueueResponse("done"));

        var response = await loop.RunAsync("read a.txt", Ct);

        response.StopReason.Should().Be(TurnStopReason.Completed);
        files.Runs.Should().Be(1);
        Results(loop).Select(r => r.Result).OfType<ToolCallRefusal>().Should().OnlyContain(r => r.Kind == ToolCallRefusalKind.UnknownTool);
    }

    [Fact]
    public async Task An_offered_and_an_unknown_call_in_one_response_run_the_offered_one()
    {
        var (loop, files) = Build(m => m
            .EnqueueToolCallsResponse(("read_file", """{"path":"a.txt"}"""), ("get_file_info", """{"path":"a.txt"}"""))
            .EnqueueResponse("done"));

        await loop.RunAsync("read a.txt", Ct);

        files.Runs.Should().Be(1);
        Results(loop).Select(r => r.Result?.ToString()).Should().BeEquivalentTo(["content of a.txt", NotFound]);
    }

    [Fact]
    public async Task The_approval_gate_is_not_asked_about_an_unknown_call()
    {
        var approval = Substitute.For<IHumanApprovalService>();
        var config = PermissionConfig.CreateDefault();
        config.DefaultAction = PermissionAction.Ask;
        var pipeline = new ToolInvocationPipeline(
            [new ApprovalGateMiddleware(new ToolCallPolicy(config), approval), new RepeatedErrorGuardMiddleware()]);
        var (loop, _) = Build(m => Unknown(m, 1).EnqueueResponse("done"), pipeline);

        await loop.RunAsync("inspect a.txt", Ct);

        approval.ReceivedCalls().Should().BeEmpty();
        Results(loop).Should().ContainSingle().Which.Result!.ToString().Should().Be(NotFound);
    }

    [Fact]
    public async Task The_streamed_turn_stops_the_same_way()
    {
        var (loop, _) = Build(m => Unknown(m, 12).EnqueueResponse("never requested"));

        var refusals = new List<ToolCallRefusalKind?>();
        await foreach (var chunk in loop.RunStreamingAsync("inspect a.txt", Ct))
        {
            if (chunk.ToolResult is { } result)
            {
                refusals.Add(result.RefusalKind);
            }
        }

        refusals.Should().Equal(ToolCallRefusalKind.UnknownTool, ToolCallRefusalKind.UnknownTool, ToolCallRefusalKind.RepeatedError);
    }

    [Fact]
    public async Task A_host_s_own_additional_tool_is_not_treated_as_unknown()
    {
        var hostRuns = 0;
        var mock = new MockChatClient()
            .EnqueueToolCallResponse("host_tool", "{}")
            .EnqueueResponse("done");
        var client = mock.AsBuilder()
            .UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(), c =>
                c.AdditionalTools = [AIFunctionFactory.Create(() => { hostRuns++; return "ran"; }, "host_tool")])
            .Build();
        var loop = new AgentLoop(client, new AgentOptions());

        await loop.RunAsync("go", Ct);

        hostRuns.Should().Be(1);
        Results(loop).Should().ContainSingle().Which.Result!.ToString().Should().Be("ran");
    }
}
