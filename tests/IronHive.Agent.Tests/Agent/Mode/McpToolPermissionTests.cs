using IronHive.Agent.Extensions;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// MCP tools keep the names their servers give them (<c>summarize</c>, not <c>mcp__server__summarize</c>), so the
/// <c>McpTools</c> rules have to reach them by origin, not by a name prefix.
/// </summary>
public class McpToolPermissionTests
{
    private static PermissionConfig McpOnly() => new()
    {
        DefaultAction = PermissionAction.Deny,
        McpTools = [new PermissionRule { Pattern = "*", Action = PermissionAction.Allow }],
    };

    [Fact]
    public void An_MCP_tool_with_its_server_name_is_judged_by_the_McpTools_rules()
    {
        var policy = new ToolCallPolicy(McpOnly(), name => name == "summarize");

        Assert.Equal(PermissionAction.Allow, policy.Evaluate("summarize", null).Verdict);
        Assert.Equal(PermissionAction.Deny, policy.Evaluate("not_an_mcp_tool", null).Verdict);
    }

    [Fact]
    public void Without_the_predicate_a_bare_MCP_tool_name_falls_to_the_tools_rules()
    {
        // The shape the CLI had: an mcp_tools allow rule never matched, the call fell to the default action.
        var policy = new ToolCallPolicy(McpOnly());

        Assert.Equal(PermissionAction.Deny, policy.Evaluate("summarize", null).Verdict);
        Assert.Equal(PermissionAction.Allow, policy.Evaluate("mcp__server__summarize", null).Verdict);
    }

    [Fact]
    public void The_container_policy_asks_the_registered_plugin_manager()
    {
        var manager = Substitute.For<IMcpPluginManager>();
        manager.IsMcpTool("summarize").Returns(true);
        var services = new ServiceCollection().AddIronHiveAgent();
        services.AddSingleton(McpOnly());
        services.AddSingleton(manager);
        services.AddIronHiveAgentApprovalGate();
        using var sp = services.BuildServiceProvider();

        Assert.Equal(PermissionAction.Allow, sp.GetRequiredService<IToolCallPolicy>().Evaluate("summarize", null).Verdict);
    }

    [Fact]
    public async Task A_manager_with_no_plugins_claims_no_tool()
    {
        await using var manager = new McpPluginManager();
        Assert.False(manager.IsMcpTool("summarize"));
    }
}
