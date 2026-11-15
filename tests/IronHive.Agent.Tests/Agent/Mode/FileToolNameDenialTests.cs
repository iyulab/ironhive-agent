using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// A <c>tools:</c> rule that names a built-in file tool and denies it has to stop the call whatever the path rules say. Before,
/// the tool-name verdict was consulted for the directory tools and for tools with no dedicated category, and forgotten for the
/// five that have one: a host that denied <c>DeleteFile</c> by name still saw the call run under its edit rules.
/// </summary>
public class FileToolNameDenialTests
{
    private static readonly Dictionary<string, (string Pascal, string Snake, IDictionary<string, object?> Arguments)> Tools = new()
    {
        ["read"] = ("ReadFile", "read_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }),
        ["write"] = ("WriteFile", "write_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }),
        ["edit"] = ("EditFile", "edit_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }),
        ["delete"] = ("DeleteFile", "delete_file", new Dictionary<string, object?> { ["path"] = "notes.txt" }),
        ["move"] = ("MoveFile", "move_file", new Dictionary<string, object?> { ["source"] = "a.txt", ["destination"] = "b.txt" }),
    };

    // The path rules allow everything in the working directory, so only the tool-name rule can stop these calls.
    private static PermissionConfig OpenPathRules(params PermissionRule[] toolRules) => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        Read = [new PermissionRule { Pattern = "**", Action = PermissionAction.Allow }],
        Edit = [new PermissionRule { Pattern = "**", Action = PermissionAction.Allow }],
        AskBeforeDelete = false,
        Tools = [.. toolRules],
    };

    public static TheoryData<string, bool, bool> EverySpelling()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var kind in Tools.Keys)
        {
            foreach (var ruleInSnake in new[] { false, true })
            {
                foreach (var callInSnake in new[] { false, true })
                {
                    data.Add(kind, ruleInSnake, callInSnake);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EverySpelling))]
    public void A_tools_denial_refuses_the_call_in_either_spelling_whatever_the_path_rules_say(string kind, bool ruleInSnake, bool callInSnake)
    {
        var (pascal, snake, arguments) = Tools[kind];
        var rule = new PermissionRule { Pattern = ruleInSnake ? snake : pascal, Action = PermissionAction.Deny };
        var policy = new ToolCallPolicy(OpenPathRules(rule));

        var assessment = policy.Evaluate(callInSnake ? snake : pascal, arguments);

        assessment.Verdict.Should().Be(PermissionAction.Deny, "the host denied this tool by name");
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("move")]
    public void A_default_deny_with_no_rule_about_the_tool_keeps_deferring_to_the_path_rules(string kind)
    {
        var (pascal, _, arguments) = Tools[kind];
        var config = OpenPathRules();
        config.DefaultAction = PermissionAction.Deny;
        var policy = new ToolCallPolicy(config);

        var assessment = policy.Evaluate(pascal, arguments);

        assessment.Verdict.Should().NotBe(PermissionAction.Deny, "a default action is not a rule about this tool - the path rules decided before and still do");
    }

    [Fact]
    public void A_tools_denial_of_one_tool_does_not_touch_the_others()
    {
        var policy = new ToolCallPolicy(OpenPathRules(new PermissionRule { Pattern = "DeleteFile", Action = PermissionAction.Deny }));

        policy.Evaluate("ReadFile", Tools["read"].Arguments).Verdict.Should().NotBe(PermissionAction.Deny);
        policy.Evaluate("WriteFile", Tools["write"].Arguments).Verdict.Should().NotBe(PermissionAction.Deny);
        policy.Evaluate("MoveFile", Tools["move"].Arguments).Verdict.Should().NotBe(PermissionAction.Deny);
    }

    [Fact]
    public void The_path_rules_still_judge_both_ends_of_a_move()
    {
        var config = OpenPathRules();
        config.Edit = [new PermissionRule { Pattern = "locked/**", Action = PermissionAction.Deny }, new PermissionRule { Pattern = "**", Action = PermissionAction.Allow }];
        var policy = new ToolCallPolicy(config);

        var intoLocked = policy.Evaluate("MoveFile", new Dictionary<string, object?> { ["source"] = "a.txt", ["destination"] = "locked/a.txt" });
        var outOfLocked = policy.Evaluate("MoveFile", new Dictionary<string, object?> { ["source"] = "locked/a.txt", ["destination"] = "a.txt" });

        intoLocked.Verdict.Should().Be(PermissionAction.Deny);
        outOfLocked.Verdict.Should().Be(PermissionAction.Deny);
    }
}
