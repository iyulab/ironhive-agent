using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// Target arguments (<see cref="ToolInvocationHints"/>): a tool that declares which arguments name what a call acts on is
/// guarded per target, whatever its other arguments. Driven through a real function-invoking client over a scripted model.
/// </summary>
public class ToolTargetArgumentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolInvocationOptions Defaults = new()
    {
        MaxRepeatedCalls = 3,
        MaxRefusedRepeats = 2,
        MaxRepeatedResults = 3,
    };

    private const string Bare = """{"path":"photos/a.jpg"}""";
    private const string Asked = """{"path":"photos/a.jpg","question":"What event is this photo for?"}""";

    // A measured loop: a small model, stuck on one photo, alternated an optional question between calls. Every call
    // succeeded and every answer was worded differently (the describer samples), so no run of identical calls ever
    // reached the limit and no result ever repeated.
    private static readonly string[] AlternatingTrace = [Bare, Asked, Asked, Asked, Bare, Asked, Bare, Bare];

    private sealed class Describer
    {
        public int Calls;

        public string Describe(string path, string? question = null) => $"answer {++Calls} about {path}";

        public string Constant(string path, string? question = null)
        {
            Calls++;
            return $"a beach at sunset ({path})";
        }

        public int Notes;

        public string Note(string text) => $"noted {++Notes}";
    }

    private static (AgentLoop Loop, MockChatClient Mock) Build(AITool describe, Describer describer, IEnumerable<(string Tool, string Args)> calls)
    {
        var mock = new MockChatClient();
        foreach (var (tool, args) in calls)
        {
            mock.EnqueueToolCallResponse(tool, args);
        }

        mock.EnqueueResponse("done");
        var client = mock.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(Defaults)).Build();
        var tools = new List<AITool> { describe, AIFunctionFactory.Create(describer.Note, "note") };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), mock);
    }

    private static AIFunction DescribeTool(Describer describer) => AIFunctionFactory.Create(describer.Describe, "describe_image");

    [Fact]
    public async Task A_model_alternating_an_optional_argument_on_one_target_is_refused_on_the_fourth_call_and_ended_on_the_second_refusal()
    {
        var describer = new Describer();
        var (loop, mock) = Build(
            DescribeTool(describer).WithTargetArguments(["path"]), describer,
            AlternatingTrace.Select(args => ("describe_image", args)));

        var response = await loop.RunAsync("write the record for this photo", Ct);

        describer.Calls.Should().Be(3, "three successful calls on photos/a.jpg, then the fourth is refused");
        mock.ReceivedMessages.Should().HaveCount(5, "three runs, one refusal the model may correct, then the second refusal ends it");
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);

        var results = loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        var refusal = results[3].Result.Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
        refusal.Message.Should().Contain("on the same path").And.Contain("a different path").And.NotContain("change the arguments");
        results[^1].Result.Should().BeOfType<ToolCallRefusal>()
            .Which.Message.Should().Contain("on the same path").And.Contain("stopped as stuck");
    }

    // Positive control for the fact above: without the declaration the same trace runs to the end, which is the loop
    // the declaration exists to stop.
    [Fact]
    public async Task Without_declared_targets_the_same_trace_is_never_refused()
    {
        var describer = new Describer();
        var (loop, _) = Build(DescribeTool(describer), describer, AlternatingTrace.Select(args => ("describe_image", args)));

        var response = await loop.RunAsync("write the record for this photo", Ct);

        describer.Calls.Should().Be(AlternatingTrace.Length);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task A_different_target_is_a_different_call()
    {
        var describer = new Describer();
        var (loop, _) = Build(
            DescribeTool(describer).WithTargetArguments(["path"]), describer,
            [
                ("describe_image", Bare), ("describe_image", Asked), ("describe_image", Bare),
                ("describe_image", """{"path":"photos/b.jpg","question":"What event is this photo for?"}"""),
            ]);

        var response = await loop.RunAsync("describe both photos", Ct);

        describer.Calls.Should().Be(4);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    // The repeated-result guard shares the same notion of "the same call": on separate visits, the same target that
    // returns the same result counts, whatever the other arguments were.
    [Fact]
    public async Task Separate_visits_to_one_target_that_return_the_same_result_end_the_request()
    {
        var describer = new Describer();
        var describe = AIFunctionFactory.Create(describer.Constant, "describe_image").WithTargetArguments(["path"]);
        var (loop, _) = Build(describe, describer,
        [
            ("describe_image", Bare), ("note", """{"text":"x"}"""),
            ("describe_image", Asked), ("note", """{"text":"y"}"""),
            ("describe_image", """{"path":"photos/a.jpg","question":"Who is in it?"}"""),
        ]);

        var response = await loop.RunAsync("write the record for this photo", Ct);

        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Last().Result
            .Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedResult);
    }

    [Fact]
    public void Declaring_an_argument_the_tool_does_not_have_is_rejected()
    {
        var describe = DescribeTool(new Describer());

        var act = () => describe.WithTargetArguments(["file"]);

        act.Should().Throw<ArgumentException>().WithMessage("*'describe_image' has no argument named 'file'*declared: path, question*");
        FluentActions.Invoking(() => describe.WithTargetArguments([])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task The_declared_tool_invokes_and_describes_itself_as_before()
    {
        var describer = new Describer();
        var original = DescribeTool(describer);

        var declared = (AIFunction)original.WithTargetArguments(["path", "path"]).WithTargetArguments(["path"]);

        declared.Name.Should().Be(original.Name);
        declared.JsonSchema.GetRawText().Should().Be(original.JsonSchema.GetRawText());
        ToolInvocationHints.GetTargetArguments(declared).Should().Equal("path");
        (await declared.InvokeAsync(new() { ["path"] = "photos/a.jpg" }, Ct))!.ToString().Should().Contain("answer 1");
    }

    private static ModelContextProtocol.Client.McpClientTool McpTool(System.Text.Json.Nodes.JsonObject meta) =>
        new(NSubstitute.Substitute.For<ModelContextProtocol.Client.McpClient>(),
            new ModelContextProtocol.Protocol.Tool
            {
                Name = "describe_image",
                InputSchema = JsonSerializer.Deserialize<JsonElement>(
                    """{"type":"object","properties":{"path":{"type":"string"},"question":{"type":"string"}}}"""),
                Meta = meta,
            });

    [Fact]
    public void Target_arguments_an_MCP_server_declares_in_meta_reach_the_guards()
    {
        var tool = McpPluginManager.WithDeclaredHints(McpTool(new() { [ToolInvocationHints.TargetArgumentsKey] = "path" }));

        ToolInvocationHints.GetTargetArguments(tool).Should().Equal("path");
        tool.Name.Should().Be("describe_image");
    }

    [Fact]
    public void A_target_name_the_MCP_tool_s_schema_does_not_declare_is_dropped()
    {
        var original = McpTool(new() { [ToolInvocationHints.TargetArgumentsKey] = new System.Text.Json.Nodes.JsonArray("file", "path") });
        ToolInvocationHints.GetTargetArguments(McpPluginManager.WithDeclaredHints(original)).Should().Equal("path");

        var unknownOnly = McpTool(new() { [ToolInvocationHints.TargetArgumentsKey] = "file" });
        McpPluginManager.WithDeclaredHints(unknownOnly).Should().BeSameAs(unknownOnly);
    }
}
