using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>The built-in rules ask before an environment file is read, wherever in the working directory it lies.</summary>
public sealed class DefaultEnvironmentFileRuleTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("prod.env")]
    [InlineData("config/.env")]
    [InlineData("config/prod.env")]
    [InlineData("services/api/production.env")]
    [InlineData("prod.env.local")]
    [InlineData("config/app.env.production")]
    [InlineData("prod.envrc")]
    [InlineData("prod.env::$DATA")]
    public void The_default_rules_ask_before_an_environment_file_is_read(string path)
    {
        var verdict = new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateRead(path);

        verdict.Action.Should().Be(PermissionAction.Ask);
        verdict.Reason.Should().Be("May contain secrets");
    }

    [Theory]
    [InlineData("secrets/key.txt")]
    [InlineData(".secrets/key.txt")]
    [InlineData("app-secrets/key.txt")]
    [InlineData("services/client_secrets/key.json")]
    public void The_default_rules_refuse_to_read_what_lies_in_a_secrets_folder(string path)
    {
        var verdict = new PermissionEvaluator(PermissionConfig.CreateDefault()).EvaluateRead(path);

        verdict.Action.Should().Be(PermissionAction.Deny);
    }
}
