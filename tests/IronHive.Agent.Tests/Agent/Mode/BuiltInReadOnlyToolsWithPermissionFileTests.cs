using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// A host's permission file replaces the built-in rules for the sections it writes - that is what a file is for. The built-in
/// tools that only carry text or look (<c>propose_for_approval</c>, <c>advisor</c>, <c>retrieve_tool_result</c>, the search tools)
/// have no rule in such a file because the file cannot know their names, so before, a file that listed only <c>mcp_tools</c>
/// made the approval-proposal tool ask for its own approval and the MCP call behind it never reached the server.
/// </summary>
public class BuiltInReadOnlyToolsWithPermissionFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Approver : IHumanApprovalService
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(ApprovalResult.Approve());
        }
    }

    private static PermissionConfig LoadYaml(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"perm-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return PermissionConfigLoader.LoadFromYaml(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string OnlyMcpToolsAllowed = """
        permissions:
          mcp_tools:
            - pattern: "docs_*"
              action: allow
        """;

    private static ToolCallPolicy PolicyOver(PermissionConfig config) =>
        new(config, isMcpTool: name => name.StartsWith("docs_", StringComparison.Ordinal));

    [Theory]
    [InlineData("propose_for_approval")]
    [InlineData("advisor")]
    [InlineData("retrieve_tool_result")]
    [InlineData("ask_user")]
    public async Task A_file_that_lists_only_mcp_tools_still_lets_the_built_in_read_only_tools_through_without_asking(string tool)
    {
        var approver = new Approver();
        var gate = new ApprovalGate(PolicyOver(LoadYaml(OnlyMcpToolsAllowed)), approver);

        var decision = await gate.DecideAsync(tool, null, "call-1", Ct);

        decision.ShouldProceed.Should().BeTrue();
        approver.Requests.Should().BeEmpty("the tool only carries text to the approval screen, so a person is not asked about it");
    }

    [Fact]
    public async Task The_mcp_call_the_proposal_is_for_is_judged_on_its_own_rule()
    {
        // The shape of the report: the proposal and the call it is for arrive in one step. Both go through the file.
        var approver = new Approver();
        var gate = new ApprovalGate(PolicyOver(LoadYaml(OnlyMcpToolsAllowed)), approver);

        var proposal = await gate.DecideAsync("propose_for_approval", null, "call-1", Ct);
        var call = await gate.DecideAsync("docs_search", new Dictionary<string, object?> { ["q"] = "x" }, "call-2", Ct);

        proposal.ShouldProceed.Should().BeTrue();
        call.ShouldProceed.Should().BeTrue();
        approver.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_tool_that_is_not_a_built_in_read_only_tool_is_judged_as_before()
    {
        // Positive control: the exemption names the built-ins, nothing else.
        var policy = PolicyOver(LoadYaml(OnlyMcpToolsAllowed));

        policy.Evaluate("click_button", arguments: null).Verdict.Should().Be(PermissionAction.Ask);
        policy.Evaluate("WriteFile", new Dictionary<string, object?> { ["path"] = "a.txt" }).Verdict.Should().Be(PermissionAction.Ask);
    }

    [Theory]
    [InlineData("grep_files")]
    [InlineData("glob_files")]
    [InlineData("list_directory")]
    [InlineData("read_file")]
    public void The_tools_that_look_at_files_are_not_lifted_their_path_rules_decide(string tool)
    {
        // AC 2: the exemption does not widen any other tool. With a file that has no Read rule, a search or a read still asks.
        var policy = PolicyOver(LoadYaml(OnlyMcpToolsAllowed));
        var args = new Dictionary<string, object?> { ["path"] = "src", ["pattern"] = "x" };

        policy.Evaluate(tool, args).Verdict.Should().Be(PermissionAction.Ask);
    }

    [Theory]
    [InlineData("ask", PermissionAction.Ask)]
    [InlineData("deny", PermissionAction.Deny)]
    public void A_rule_the_file_writes_for_a_built_in_tool_still_wins(string action, PermissionAction expected)
    {
        var config = LoadYaml($$"""
            permissions:
              tools:
                - pattern: "propose_for_approval"
                  action: {{action}}
            """);

        PolicyOver(config).Evaluate("propose_for_approval", arguments: null).Verdict.Should().Be(expected);
    }

    [Fact]
    public void A_default_action_of_deny_is_not_lifted()
    {
        // The same precedent as a trusted read-only MCP tool: only an Ask default is lifted; a host that denies by default meant it.
        var config = LoadYaml("""
            permissions:
              default_action: deny
            """);

        PolicyOver(config).Evaluate("propose_for_approval", arguments: null).Verdict.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void The_built_in_default_configuration_is_unchanged_for_these_tools()
    {
        var policy = new ToolCallPolicy(PermissionConfig.CreateDefault());

        policy.Evaluate("propose_for_approval", arguments: null).Verdict.Should().Be(PermissionAction.Allow);
        policy.Evaluate("click_button", arguments: null).Verdict.Should().Be(PermissionAction.Ask);
    }
}
