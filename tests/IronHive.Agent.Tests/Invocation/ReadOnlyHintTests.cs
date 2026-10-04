using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Context;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using IronHive.Agent.Tools;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// A tool declared not read-only is outside the repeated-result guard: running a check again after an edit and getting
/// the same output is the edit-test loop, not a re-read. Undeclared tools are guarded as before.
/// </summary>
public class ReadOnlyHintTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Workspace
    {
        public int Checks;

        public int Writes;

        public string Write(string path, string content) => $"wrote {path} ({++Writes})";

        // The checker's verdict does not change: the proof still fails after every edit.
        public string Check(string path)
        {
            Checks++;
            return $"Error: {path}: the proof is incomplete";
        }
    }

    // The measured shape: write a new attempt, run the same check, read the same failure — four times.
    private static IEnumerable<(string Tool, string Args)> EditTestLoop()
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            yield return ("write", $$"""{"path":"proof.v","content":"attempt {{attempt}}"}""");
            yield return ("check", """{"path":"proof.v"}""");
        }
    }

    private static (AgentLoop Loop, Workspace Workspace) Build(bool declareCheck)
    {
        var workspace = new Workspace();
        var mock = new MockChatClient();
        foreach (var (tool, args) in EditTestLoop())
        {
            mock.EnqueueToolCallResponse(tool, args);
        }

        mock.EnqueueResponse("done");
        AITool check = AIFunctionFactory.Create(workspace.Check, "check");
        if (declareCheck)
        {
            check = check.WithReadOnly(false);
        }

        var client = mock.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions())).Build();
        var tools = new List<AITool> { AIFunctionFactory.Create(workspace.Write, "write").WithReadOnly(false), check };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), workspace);
    }

    [Fact]
    public async Task An_edit_and_check_loop_with_the_same_output_runs_on_when_the_check_is_declared_not_read_only()
    {
        var (loop, workspace) = Build(declareCheck: true);

        var response = await loop.RunAsync("prove it", Ct);

        workspace.Checks.Should().Be(4);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    // Positive control: the same loop with an undeclared check is what the guard stopped on the third run.
    [Fact]
    public async Task The_same_loop_with_an_undeclared_check_is_stopped_as_a_repeated_result()
    {
        var (loop, workspace) = Build(declareCheck: false);

        var response = await loop.RunAsync("prove it", Ct);

        workspace.Checks.Should().Be(3);
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Last().Result
            .Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedResult);
    }

    [Fact]
    public void The_built_in_tools_declare_what_they_do()
    {
        var tools = BuiltInTools.GetAll(Path.GetTempPath()).ToDictionary(t => t.Name);

        foreach (var read in new[] { "ReadFile", "ListDirectory", "GlobFiles", "GrepFiles" })
        {
            ToolInvocationHints.IsReadOnly(tools[read]).Should().BeTrue(read);
        }

        foreach (var write in new[] { "WriteFile", "EditFile", "DeleteFile", "MoveFile", "ExecuteCommand" })
        {
            ToolInvocationHints.IsReadOnly(tools[write]).Should().BeFalse(write);
        }

        tools.Values.Count(t => ToolInvocationHints.IsReadOnly(t) is null).Should().Be(0, "every built-in declares itself");
    }

    private static ModelContextProtocol.Client.McpClientTool McpTool(bool? readOnlyHint) =>
        new(NSubstitute.Substitute.For<ModelContextProtocol.Client.McpClient>(),
            new ModelContextProtocol.Protocol.Tool
            {
                Name = "run_tests",
                InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}"""),
                Annotations = readOnlyHint is null ? null : new ModelContextProtocol.Protocol.ToolAnnotations { ReadOnlyHint = readOnlyHint },
            });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void An_MCP_tool_answers_with_its_readOnlyHint_also_when_wrapped(bool? hint)
    {
        var tool = McpTool(hint);

        ToolInvocationHints.IsReadOnly(tool).Should().Be(hint);
        ToolInvocationHints.IsReadOnly(tool.WithRetrievalHints(aliases: ["tests"])).Should().Be(hint);
    }

    [Fact]
    public void An_explicit_declaration_wins_over_the_annotation()
    {
        ToolInvocationHints.IsReadOnly(McpTool(true).WithReadOnly(false)).Should().BeFalse();
    }
}
