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

    // The rules name a tool in either spelling and the call arrives in either: the verdict is the highest-priority rule that
    // speaks for the tool in any spelling, not the first denial found - an explicit allow of the tool beats a catch-all deny.
    [Theory]
    [MemberData(nameof(EverySpelling))]
    public void An_explicit_allow_of_the_tool_beats_a_lower_priority_catch_all_deny(string kind, bool allowInSnake, bool callInSnake)
    {
        var (pascal, snake, arguments) = Tools[kind];
        var policy = new ToolCallPolicy(OpenPathRules(
            new PermissionRule { Pattern = "*", Action = PermissionAction.Deny, Priority = 0 },
            new PermissionRule { Pattern = allowInSnake ? snake : pascal, Action = PermissionAction.Allow, Priority = 10 }));

        var assessment = policy.Evaluate(callInSnake ? snake : pascal, arguments);

        assessment.Verdict.Should().NotBe(PermissionAction.Deny, "the host allowed this tool by name above the catch-all deny");
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("move")]
    public void A_catch_all_deny_refuses_a_file_tool_nothing_allows_by_name(string kind)
    {
        var (pascal, snake, arguments) = Tools[kind];
        var policy = new ToolCallPolicy(OpenPathRules(new PermissionRule { Pattern = "*", Action = PermissionAction.Deny }));

        policy.Evaluate(pascal, arguments).Verdict.Should().Be(PermissionAction.Deny);
        policy.Evaluate(snake, arguments).Verdict.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void A_deny_and_an_allow_of_equal_priority_in_the_two_spellings_is_a_denial()
    {
        var policy = new ToolCallPolicy(OpenPathRules(
            new PermissionRule { Pattern = "ReadFile", Action = PermissionAction.Deny, Priority = 5 },
            new PermissionRule { Pattern = "read_file", Action = PermissionAction.Allow, Priority = 5 }));

        policy.Evaluate("ReadFile", Tools["read"].Arguments).Verdict.Should().Be(PermissionAction.Deny);
        policy.Evaluate("read_file", Tools["read"].Arguments).Verdict.Should().Be(PermissionAction.Deny, "a conflict between equal rules fails closed");
    }

    // The shell has its own category (the Bash rules judge the command) and forgot the tool-name verdict the same way.
    [Theory]
    [InlineData("ExecuteCommand", "ExecuteCommand")]
    [InlineData("ExecuteCommand", "execute_command")]
    [InlineData("execute_command", "ExecuteCommand")]
    [InlineData("execute_command", "execute_command")]
    [InlineData("shell", "ExecuteCommand")]
    [InlineData("ExecuteCommand", "shell")]
    [InlineData("shell", "shell")]
    public void A_tools_denial_of_the_shell_refuses_the_command_whatever_the_bash_rules_say(string ruleName, string callName)
    {
        var config = OpenPathRules(new PermissionRule { Pattern = ruleName, Action = PermissionAction.Deny });
        config.Bash = [new PermissionRule { Pattern = "*", Action = PermissionAction.Allow }];
        var policy = new ToolCallPolicy(config);

        var assessment = policy.Evaluate(callName, new Dictionary<string, object?> { ["command"] = "echo hello" });

        assessment.Verdict.Should().Be(PermissionAction.Deny, "the host denied the shell tool by name");
    }

    [Fact]
    public void The_shell_with_no_rule_about_it_still_answers_to_the_bash_rules()
    {
        var config = OpenPathRules();
        config.Bash = [new PermissionRule { Pattern = "*", Action = PermissionAction.Allow }];
        var policy = new ToolCallPolicy(config);

        policy.Evaluate("ExecuteCommand", new Dictionary<string, object?> { ["command"] = "echo hello" }).Verdict
            .Should().Be(PermissionAction.Allow, "the Bash rules allow the command and no rule names the tool");
    }

    // The directory tools answered to a rule in the spelling of the call only; a rule written in the other one was ignored.
    [Theory]
    [InlineData("ListDirectory", "list_directory")]
    [InlineData("GlobFiles", "glob_files")]
    [InlineData("GrepFiles", "grep_files")]
    public void A_tools_denial_of_a_directory_tool_refuses_the_call_in_either_spelling(string pascal, string snake)
    {
        foreach (var ruleName in new[] { pascal, snake })
        {
            foreach (var callName in new[] { pascal, snake })
            {
                var policy = new ToolCallPolicy(OpenPathRules(new PermissionRule { Pattern = ruleName, Action = PermissionAction.Deny }));

                policy.Evaluate(callName, new Dictionary<string, object?> { ["path"] = "." }).Verdict
                    .Should().Be(PermissionAction.Deny, $"the rule '{ruleName}' denies the tool and the call is '{callName}'");
            }
        }
    }

    [Fact]
    public void A_higher_priority_ask_of_the_tool_over_a_catch_all_deny_is_not_a_denial()
    {
        // The rule that names the tool and outranks the catch-all says «not denied»; a name rule's ask is not a question the
        // file tools put (their path rules ask), so the call goes on to the path rules.
        var policy = new ToolCallPolicy(OpenPathRules(
            new PermissionRule { Pattern = "*", Action = PermissionAction.Deny, Priority = 0 },
            new PermissionRule { Pattern = "ReadFile", Action = PermissionAction.Ask, Priority = 10 }));

        policy.Evaluate("ReadFile", Tools["read"].Arguments).Verdict.Should().NotBe(PermissionAction.Deny);
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
