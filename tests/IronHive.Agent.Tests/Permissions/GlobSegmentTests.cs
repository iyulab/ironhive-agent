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
    [InlineData("secrets/**/", "secrets/key.txt", true)]
    [InlineData("secrets/**/", "secrets/deep/key.txt", true)]
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

    // A command is not a path: its words are separated by spaces, so there is no folder for "**/" to stand for, and a deny
    // written with it keeps covering what stands between the words.
    [Theory]
    [InlineData("cat **/.env*", "cat .env")]
    [InlineData("cat **/.env*", "cat -n .env")]
    [InlineData("cat **/.env*", "cat config/.env.local")]
    [InlineData("**/danger *", "x && danger y")]
    public void A_command_pattern_reads_a_double_star_as_any_text(string pattern, string command)
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            Bash = [new PermissionRule { Pattern = pattern, Action = PermissionAction.Deny }],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateBash(command).Action.Should().Be(PermissionAction.Deny);
    }
}
