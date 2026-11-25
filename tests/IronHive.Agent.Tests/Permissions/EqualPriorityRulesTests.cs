using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// Rules of equal priority that disagree are decided by the stricter action - deny, then ask, then allow - whatever order
/// they are listed in. The built-in rules are listed before the rules a host or a person adds, so an order-based answer
/// would let a built-in allow override an added deny.
/// </summary>
public sealed class EqualPriorityRulesTests
{
    public static TheoryData<string, PermissionAction, PermissionAction> Disagreements()
    {
        var data = new TheoryData<string, PermissionAction, PermissionAction>();
        foreach (var section in new[] { "bash", "tool", "mcp", "read" })
        {
            foreach (var (first, second) in new[]
            {
                (PermissionAction.Allow, PermissionAction.Deny), (PermissionAction.Deny, PermissionAction.Allow),
                (PermissionAction.Allow, PermissionAction.Ask), (PermissionAction.Ask, PermissionAction.Allow),
                (PermissionAction.Ask, PermissionAction.Deny), (PermissionAction.Deny, PermissionAction.Ask),
            })
            {
                data.Add(section, first, second);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void The_stricter_of_two_rules_of_equal_priority_decides(string section, PermissionAction first, PermissionAction second)
    {
        List<PermissionRule> rules =
        [
            new PermissionRule { Pattern = "item*", Action = first },
            new PermissionRule { Pattern = "item*", Action = second },
        ];
        var config = new PermissionConfig
        {
            Bash = section == "bash" ? rules : [],
            Tools = section == "tool" ? rules : [],
            McpTools = section == "mcp" ? rules : [],
            Read = section == "read" ? rules : [],
        };

        var verdict = new PermissionEvaluator(config).Evaluate(section, "item.txt");

        verdict.Action.Should().Be(Stricter(first, second));
    }

    private static PermissionAction Stricter(PermissionAction a, PermissionAction b) =>
        a == PermissionAction.Deny || b == PermissionAction.Deny ? PermissionAction.Deny
        : a == PermissionAction.Ask || b == PermissionAction.Ask ? PermissionAction.Ask
        : PermissionAction.Allow;

    // The documented way to forbid something is to add a deny to the built-in rules.
    [Theory]
    [InlineData("read", "private/**", "private/a.txt")]
    [InlineData("tool", "GrepFiles", "GrepFiles")]
    [InlineData("mcp", "items_get", "items_get")]
    [InlineData("mcp", "fs/*", "fs/items_get")]
    public void A_deny_added_to_the_default_rules_at_their_priority_holds(string section, string pattern, string target)
    {
        var config = PermissionConfig.CreateDefault();
        var section_ = section == "read" ? config.Read : section == "tool" ? config.Tools : config.McpTools;
        section_.Add(new PermissionRule { Pattern = pattern, Action = PermissionAction.Deny });

        new PermissionEvaluator(config).Evaluate(section, target).Action.Should().Be(PermissionAction.Deny);
    }

    [Theory]
    [InlineData("git *", PermissionAction.Allow, "git push**", "git push origin feature/x")]
    [InlineData("cat *", PermissionAction.Allow, "cat **.env", "cat config/.env")]
    [InlineData("rm *", PermissionAction.Ask, "rm **/etc/**", "rm /etc/passwd")]
    public void A_broad_rule_listed_first_does_not_override_a_narrower_deny(string broad, PermissionAction action, string deny, string command)
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = broad, Action = action },
                new PermissionRule { Pattern = deny, Action = PermissionAction.Deny },
            ],
        });

        evaluator.EvaluateBash(command).Action.Should().Be(PermissionAction.Deny);
    }

    // Priority still decides before strictness: this is how an exception to a deny is written.
    [Fact]
    public void A_higher_priority_allow_still_beats_a_lower_priority_deny()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = "git *", Action = PermissionAction.Allow, Priority = 1 },
                new PermissionRule { Pattern = "git push**", Action = PermissionAction.Deny },
            ],
        });

        evaluator.EvaluateBash("git push origin feature/x").Action.Should().Be(PermissionAction.Allow);
    }
}
