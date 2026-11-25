using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// A command line is not a path: in a <c>Bash</c> rule a wildcard stands for any text, so a rule still speaks about a line
/// that carries a path or runs over more than one line.
/// </summary>
public sealed class ShellWildcardReachTests
{
    private static PermissionEvaluator With(string pattern, PermissionAction action, PermissionAction otherwise) =>
        new(new PermissionConfig
        {
            Bash = [new PermissionRule { Pattern = pattern, Action = action }],
            DefaultAction = otherwise,
        });

    [Theory]
    [InlineData("rm *", "rm x")]
    [InlineData("rm *", "rm -rf /tmp/x")]
    [InlineData("rm *", @"rm C:\data\x")]
    [InlineData("rm *", "rm x\necho done")]
    [InlineData("rm *", "rm x\r\necho done")]
    [InlineData("rm *", "rm -rf /tmp/x\necho done")]
    [InlineData("rm **", "rm -rf /tmp/x")]
    [InlineData("rm **", "rm x\necho done")]
    [InlineData("rm **", "rm -rf /tmp/x\r\necho done")]
    [InlineData("rm ?", "rm /")]
    public void A_deny_rule_refuses_a_line_with_a_path_or_a_second_line(string pattern, string command) =>
        With(pattern, PermissionAction.Deny, PermissionAction.Allow).EvaluateBash(command).Action
            .Should().Be(PermissionAction.Deny);

    [Fact]
    public void An_ask_rule_asks_about_a_line_with_a_path() =>
        With("cat *", PermissionAction.Ask, PermissionAction.Allow).EvaluateBash("cat secrets/key.txt").Action
            .Should().Be(PermissionAction.Ask);

    [Fact]
    public void An_allow_rule_covers_its_command_with_a_path_argument() =>
        With("dotnet test *", PermissionAction.Allow, PermissionAction.Ask)
            .EvaluateBash("dotnet test tests/App/App.csproj").Action.Should().Be(PermissionAction.Allow);

    // What the wildcard reaches is text of one command: a path does not let it carry a second one past the question.
    [Theory]
    [InlineData("dotnet test tests/App/App.csproj && del notes.txt")]
    [InlineData("dotnet test tests/App/App.csproj\ndel notes.txt")]
    public void An_allow_rule_with_a_path_argument_still_asks_about_a_chain(string command) =>
        With("dotnet test *", PermissionAction.Allow, PermissionAction.Ask).EvaluateBash(command).Action
            .Should().Be(PermissionAction.Ask);

    [Theory]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("sudo ls /etc")]
    public void The_default_deny_rules_refuse_a_line_with_a_path(string command) =>
        new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateBash(command).Action
            .Should().Be(PermissionAction.Deny);

    // A tool name is not a path either: a server may name a tool "fs/read".
    [Theory]
    [InlineData("*", "fs/read")]
    [InlineData("*", @"fs\read")]
    [InlineData("*_delete", "fs/items_delete")]
    [InlineData("fs*", "fs/read")]
    public void A_tool_name_rule_refuses_a_name_with_a_separator(string pattern, string toolName)
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Tools = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Deny }],
            McpTools = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Deny }],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateTool(toolName).Action.Should().Be(PermissionAction.Deny);
        evaluator.EvaluateMcpTool(toolName).Action.Should().Be(PermissionAction.Deny);
    }

    // A file name may hold a line break outside Windows; a deny ending in "**" has to keep covering it.
    [Theory]
    [InlineData("secrets/key\n")]
    [InlineData("secrets/two\nlines.txt")]
    public void A_path_deny_covers_a_name_with_a_line_break(string path) =>
        new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateRead(path).Action
            .Should().Be(PermissionAction.Deny);

    [Fact]
    public void A_path_rule_wildcard_still_stays_inside_one_name()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Read = [new PermissionRule { Pattern = "notes/*", Action = PermissionAction.Deny }],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateRead("notes/a.txt").Action.Should().Be(PermissionAction.Deny);
        evaluator.EvaluateRead("notes/old/a.txt").Action.Should().Be(PermissionAction.Allow);
    }
}
