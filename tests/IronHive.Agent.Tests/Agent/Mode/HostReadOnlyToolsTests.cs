using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// A host declares its own read-only tools (<see cref="PermissionConfig.ReadOnlyTools"/>) and Planning mode offers and
/// permits them next to the built-in read-only file tools. Before, the Planning set was the hardcoded file tools only,
/// so a host tool that only reads could never run in Planning.
/// </summary>
public class HostReadOnlyToolsTests
{
    private static AIFunction Tool(string name) => AIFunctionFactory.Create(() => "ok", name);

    [Fact]
    public void Planning_OffersDeclaredHostTools_AndStillRefusesTheRest()
    {
        var filter = new ModeToolFilter(new PermissionConfig { ReadOnlyTools = ["read_current_tab", "list_saved_*"] });
        IList<AITool> tools = [Tool("read_current_tab"), Tool("list_saved_items"), Tool("click_button"), Tool("read_file")];

        var offered = filter.FilterTools(tools, AgentMode.Planning).Select(t => t.Name).ToList();

        Assert.Equal(["read_current_tab", "list_saved_items", "read_file"], offered);
        Assert.True(filter.IsToolPermitted("list_saved_items", AgentMode.Planning));
        Assert.False(filter.IsToolPermitted("click_button", AgentMode.Planning));
    }

    [Fact]
    public void WithoutADeclaration_AHostToolIsRefusedInPlanning()
    {
        // Positive control: the same tool without the declaration.
        var filter = new ModeToolFilter(new PermissionConfig());

        Assert.False(filter.IsToolPermitted("read_current_tab", AgentMode.Planning));
    }

    [Fact]
    public void Declaring_ReadOnly_DoesNotChangeTheRiskDecision()
    {
        // Side-effect class and permission are separate: an Ask default still asks about the tool in Working mode.
        var filter = new ModeToolFilter(new PermissionConfig
        {
            ReadOnlyTools = ["read_current_tab"],
            DefaultAction = PermissionAction.Ask,
        });

        var risk = filter.AssessRisk("read_current_tab", arguments: null);

        Assert.Equal(PermissionAction.Ask, risk.Verdict);
    }

    [Fact]
    public void ReadOnlyTools_LoadsFromAPermissionFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perm-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "permissions": { "readOnlyTools": ["read_current_tab"] } }""");
        try
        {
            var filter = new ModeToolFilter(PermissionConfigLoader.LoadFromJson(path));

            Assert.True(filter.IsToolPermitted("read_current_tab", AgentMode.Planning));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadOnlyTools_LoadsFromAYamlPermissionFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"perm-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            permissions:
              read_only_tools:
                - read_current_tab
                - "list_saved_*"
              tools:
                - pattern: "click_*"
                  action: deny
            """);
        try
        {
            var config = PermissionConfigLoader.LoadFromYaml(path);
            var filter = new ModeToolFilter(config);

            Assert.Equal(["read_current_tab", "list_saved_*"], config.ReadOnlyTools);
            Assert.True(filter.IsToolPermitted("list_saved_items", AgentMode.Planning));
            Assert.Single(config.Tools); // the rule section after the list still parses
        }
        finally
        {
            File.Delete(path);
        }
    }
}
