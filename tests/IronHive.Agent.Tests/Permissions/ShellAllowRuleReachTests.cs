using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// An allow rule describes a command. A line that a shell would read as more than that command - chained, piped, redirected
/// or substituted, by either of the shells the tool runs - is not covered by it.
/// </summary>
public sealed class ShellAllowRuleReachTests
{
    // The built-in rules with the wildcard allows a consumer writes: what these facts are about is how far such a rule
    // reaches, not which commands the built-in list happens to allow.
    private static PermissionConfig WithWildcardAllows()
    {
        var config = PermissionConfig.CreateDefault();
        config.Bash.Add(new PermissionRule { Pattern = "git *", Action = PermissionAction.Allow });
        config.Bash.Add(new PermissionRule { Pattern = "dotnet *", Action = PermissionAction.Allow });
        return config;
    }

    private static PermissionEvaluator Default() => new(WithWildcardAllows());

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
    // bash runs these without any of the characters above: a value built by ${x:=...} is evaluated again by $[ ] or @P
    [InlineData("git status ${x:='$'} ${y:='(id)'} ${z:=q[$x$y]} $[z]")]
    [InlineData("git status ${x:='$'} ${y:='(id)'} ${z:=$x$y} ${z@P}")]
    [InlineData("git status ${HOME}")]
    [InlineData("git status $[1+1]")]
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

    // The way to keep allowing one line: add it as it is, beside the broader rule that also matches it.
    [Fact]
    public void An_exact_rule_added_beside_the_default_rules_allows_its_line()
    {
        var config = WithWildcardAllows();
        config.Bash.Add(new PermissionRule { Pattern = "git log; echo done", Action = PermissionAction.Allow });

        var verdict = new PermissionEvaluator(config).EvaluateBash("git log; echo done");

        verdict.Action.Should().Be(PermissionAction.Allow);
    }

    [Fact]
    public void An_exact_rule_below_the_rule_that_decides_does_not_change_the_question()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = "git *", Action = PermissionAction.Allow, Priority = 5 },
                new PermissionRule { Pattern = "git log; echo done", Action = PermissionAction.Allow },
            ],
        });

        evaluator.EvaluateBash("git log; echo done").Action.Should().Be(PermissionAction.Ask);
    }

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
