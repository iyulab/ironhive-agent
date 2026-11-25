using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// Which command lines the built-in rule sets run without asking. A rule for a program with any arguments lets that program
/// start other programs or write files, so the built-in sets name whole lines only.
/// </summary>
public sealed class BuiltInShellRulesTests
{
    private static PermissionAction Restrictive(string command) =>
        new PermissionEvaluator(PermissionConfig.CreateRestrictive()).EvaluateBash(command).Action;

    [Theory]
    // "git diff*" also names "git difftool", which runs the program given with -x
    [InlineData("git difftool -y -x whoami HEAD~1")]
    [InlineData("git difftool --extcmd=whoami -y HEAD~1")]
    // and "git log*" an option that writes a file, where the restrictive set has no edit rule at all
    [InlineData("git log -1 --output=out.txt")]
    [InlineData("git diff --output=out.txt")]
    [InlineData("git diff")]
    [InlineData("git log")]
    public void The_restrictive_rules_ask_about_a_git_command_that_is_not_their_exact_line(string command) =>
        Restrictive(command).Should().Be(PermissionAction.Ask);

    [Fact]
    public void The_restrictive_rules_still_allow_git_status() =>
        Restrictive("git status").Should().Be(PermissionAction.Allow);

    // A set for untrusted environments must not allow a line the ordinary default asks about. Its allow rules are whole
    // lines, so each one can be put to the default rules as a line.
    [Fact]
    public void Every_line_the_restrictive_rules_allow_is_one_the_default_rules_allow()
    {
        var lines = AllowedShellPatterns(PermissionConfig.CreateRestrictive());
        var byDefault = new PermissionEvaluator(PermissionConfig.CreateDefault());

        lines.Should().NotBeEmpty().And.OnlyContain(line => !line.Contains('*') && !line.Contains('?'));
        lines.Should().OnlyContain(line => byDefault.EvaluateBash(line).Action == PermissionAction.Allow);
    }

    [Theory]
    // each of these runs another program, or writes a file, with no shell operator in the line
    [InlineData("git -c alias.z=!whoami z")]
    [InlineData("git difftool -y -x whoami HEAD~1")]
    [InlineData("git log -1 --output=out.txt")]
    [InlineData("npm exec whoami")]
    [InlineData("npm install")]
    [InlineData("dotnet run")]
    [InlineData("dotnet test")]
    [InlineData("cargo run")]
    [InlineData("git log")]
    public void The_default_rules_ask_before_running_git_dotnet_npm_or_cargo(string command) =>
        new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateBash(command).Action
            .Should().Be(PermissionAction.Ask);

    [Fact]
    public void The_default_rules_still_allow_git_status() =>
        new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateBash("git status").Action
            .Should().Be(PermissionAction.Allow);

    private static List<string> AllowedShellPatterns(PermissionConfig config) =>
        [.. config.Bash.Where(rule => rule.Action == PermissionAction.Allow).Select(rule => rule.Pattern)];
}
