using AwesomeAssertions;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// <c>**/</c> stands for any number of whole folders, including none. It does not let the name after it start in the middle
/// of another name: a rule for <c>tests/</c> says nothing about <c>contests/</c>.
/// </summary>
public sealed class GlobSegmentTests
{
    [Theory]
    [InlineData("**/tests/**", "tests/a.txt", true)]
    [InlineData("**/tests/**", "src/tests/a.txt", true)]
    [InlineData("**/tests/**", "contests/a.txt", false)]
    [InlineData("**/tests/**", "src/contests/a.txt", false)]
    [InlineData("**/secrets/**", "mysecrets/key.txt", false)]
    [InlineData("**/*secrets/**", "mysecrets/key.txt", true)]
    [InlineData("**/*secrets/**", "app/mysecrets/key.txt", true)]
    [InlineData("**/*secrets/**", "secrets/key.txt", true)]
    [InlineData("**/.env*", ".env", true)]
    [InlineData("**/.env*", "config/.env.local", true)]
    [InlineData("**/.env*", "config/build.envelope", false)]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "src/deep/a.cs", true)]
    [InlineData("src/**/generated.txt", "src/generated.txt", true)]
    [InlineData("src/**/generated.txt", "src/x/y/generated.txt", true)]
    [InlineData("src/**/generated.txt", "src/not-generated.txt", false)]
    [InlineData("src/**", "src/x/y.txt", true)]
    [InlineData("**/*", ".", true)]
    [InlineData("**", "any/depth/at.all", true)]
    public void A_double_star_folder_matches_whole_folders_only(string pattern, string path, bool covered)
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Read = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Deny,
        });

        var verdict = evaluator.EvaluateRead(path);

        verdict.Action.Should().Be(covered ? PermissionAction.Allow : PermissionAction.Deny);
    }
}
