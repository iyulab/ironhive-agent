using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// An allow rule describes a command. A line that a shell would read as more than that command - chained, piped, redirected
/// or substituted, by either of the shells the tool runs - is not covered by it.
/// </summary>
public sealed class ShellAllowRuleReachTests
{
    private static PermissionEvaluator Default() => new(PermissionConfig.CreateDefault());

    private static PermissionEvaluator Allowing(string pattern, PermissionAction otherwise = PermissionAction.Ask) => new(new PermissionConfig
    {
        Bash = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Allow }],
        DefaultAction = otherwise,
    });

    [Theory]
    [InlineData("git status & type .env")]
    [InlineData("git status && del notes.txt")]
    [InlineData("git log; shutdown -h now")]
    [InlineData("git status || whoami")]
    [InlineData("dotnet --info | findstr Version")]
    [InlineData("git status\nwhoami")]
    [InlineData("git status\r\nwhoami")]
    [InlineData("git commit -m \"$(whoami)\"")]
    [InlineData("git commit -m `whoami`")]
    [InlineData("git log > notes.txt")]
    [InlineData("git apply < change.patch")]
    public void An_allow_rule_does_not_reach_past_a_shell_control_operator(string command)
    {
        var verdict = Default().EvaluateBash(command);

        verdict.Action.Should().Be(PermissionAction.Ask);
        verdict.Reason.Should().Contain("more than one command");
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet test")]
    [InlineData("git commit -m \"a message with $HOME in it\"")]
    public void A_single_command_stays_allowed(string command) =>
        Default().EvaluateBash(command).Action.Should().Be(PermissionAction.Allow);

    // The text is not parsed: the two shells quote differently, and a reading of quotes that one of them does not share
    // would let a command through. A quoted operator is asked about.
    [Fact]
    public void An_operator_inside_quotes_is_asked_about_too() =>
        Default().EvaluateBash("git commit -m \"first; second\"").Action.Should().Be(PermissionAction.Ask);

    [Fact]
    public void A_rule_that_is_the_exact_line_allows_it() =>
        Allowing("make build && make test").EvaluateBash("make build && make test").Action.Should().Be(PermissionAction.Allow);

    [Theory]
    [InlineData("*")]
    [InlineData("**")]
    public void A_rule_that_allows_every_command_allows_chains(string pattern) =>
        Allowing(pattern).EvaluateBash("echo a; echo b").Action.Should().Be(PermissionAction.Allow);

    [Fact]
    public void A_rule_with_words_and_an_operator_and_a_wildcard_does_not_allow_what_the_wildcard_chains() =>
        Allowing("npm test | tee *").EvaluateBash("npm test | tee out.txt & del notes.txt").Action.Should().Be(PermissionAction.Ask);

    [Fact]
    public void A_default_of_allow_with_no_rule_about_the_line_still_allows_it() =>
        Allowing("never-used", PermissionAction.Allow).EvaluateBash("echo a; echo b").Action.Should().Be(PermissionAction.Allow);

    [Fact]
    public void A_deny_rule_still_reads_the_whole_line() =>
        Default().EvaluateBash("sudo ls; git status").Action.Should().Be(PermissionAction.Deny);
}
