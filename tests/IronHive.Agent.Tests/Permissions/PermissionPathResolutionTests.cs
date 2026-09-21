using AwesomeAssertions;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Xunit;

namespace IronHive.Agent.Tests.Permissions;

/// <summary>
/// A path rule judges the path the tool will open, not the text the model typed.
/// </summary>
/// <remarks>
/// The file tools resolve <c>path</c> against the working directory (<c>Path.GetFullPath</c>), so
/// <c>src/../../x</c>, <c>../x</c> and an absolute path can all name the same file. A policy that
/// matches the unresolved text lets one spelling through where it stops another. Every fact here
/// comes in both directions: the spelling that must be stopped, and the one that must still pass.
/// </remarks>
public class PermissionPathResolutionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "perm-root-" + Guid.NewGuid().ToString("N"));
    private static readonly string Elsewhere = Path.Combine(Path.GetTempPath(), "perm-else-" + Guid.NewGuid().ToString("N"));

    private static PermissionEvaluator AllowSrcOnly() => new(new PermissionConfig
    {
        WorkingDirectory = Root,
        Read = [new() { Pattern = "src/**", Action = PermissionAction.Allow }],
        Edit = [new() { Pattern = "src/**", Action = PermissionAction.Allow }],
        DefaultAction = PermissionAction.Deny,
    });

    [Theory]
    [InlineData("src/../../outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData("src/nested/../../../outside.txt")]
    public void APathThatClimbsOutOfTheWorkingDirectory_IsNotCoveredByARuleForADirectoryInsideIt(string path)
    {
        AllowSrcOnly().EvaluateRead(path).Action.Should().Be(PermissionAction.Deny);
        AllowSrcOnly().EvaluateEdit(path).Action.Should().Be(PermissionAction.Deny);
    }

    [Fact]
    public void EverySpellingOfOneFileInsideTheWorkingDirectory_GetsTheSameAnswer()
    {
        var evaluator = AllowSrcOnly();
        var spellings = new[]
        {
            "src/a.cs",
            "./src/a.cs",
            "docs/../src/a.cs",
            "src\\a.cs",
            Path.Combine(Root, "src", "a.cs"),
        };

        foreach (var spelling in spellings)
        {
            evaluator.EvaluateRead(spelling).Action.Should().Be(PermissionAction.Allow, $"'{spelling}' is src/a.cs");
        }
    }

    [Fact]
    public void ADenyRuleForADirectory_HoldsWhenThePathEntersItThroughAnotherOne()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = Root,
            Read =
            [
                new() { Pattern = "**/*", Action = PermissionAction.Allow },
                new() { Pattern = "secrets/**", Action = PermissionAction.Deny, Priority = 20 },
            ],
            DefaultAction = PermissionAction.Allow,
        });

        evaluator.EvaluateRead("public/../secrets/key.pem").Action.Should().Be(PermissionAction.Deny);
        evaluator.EvaluateRead("public/readme.md").Action.Should().Be(PermissionAction.Allow);
    }

    [Fact]
    public void APathOutsideTheWorkingDirectory_IsJudgedByTheExternalDirectoryRules()
    {
        var evaluator = new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = Root,
            Read = [new() { Pattern = "**/*", Action = PermissionAction.Allow }],
            ExternalDirectory =
            [
                new() { Pattern = Elsewhere.Replace('\\', '/') + "/**", Action = PermissionAction.Allow },
            ],
            DefaultAction = PermissionAction.Deny,
        });

        // The catch-all Read rule describes files of the working directory; it does not reach out.
        evaluator.EvaluateRead(Path.Combine(Path.GetTempPath(), "somewhere-else", "x.txt"))
            .Action.Should().Be(PermissionAction.Deny);
        evaluator.EvaluateRead(Path.Combine(Elsewhere, "shared", "x.txt"))
            .Action.Should().Be(PermissionAction.Allow);
    }

    [Theory]
    [InlineData("GrepFiles")]
    [InlineData("GlobFiles")]
    [InlineData("ListDirectory")]
    public void TheDirectoryTools_AnswerToTheReadRules(string tool)
    {
        var filter = new ModeToolFilter(new PermissionEvaluator(new PermissionConfig
        {
            WorkingDirectory = Root,
            Read =
            [
                new() { Pattern = "**", Action = PermissionAction.Allow },
                new() { Pattern = "secrets", Action = PermissionAction.Deny, Priority = 20 },
                new() { Pattern = "secrets/**", Action = PermissionAction.Deny, Priority = 20 },
            ],
            Tools = [new() { Pattern = "*", Action = PermissionAction.Allow }],
            DefaultAction = PermissionAction.Allow,
        }));

        var denied = filter.AssessRisk(tool, new Dictionary<string, object?> { ["path"] = "public/../secrets", ["pattern"] = "*" });
        var allowed = filter.AssessRisk(tool, new Dictionary<string, object?> { ["path"] = "public", ["pattern"] = "*" });

        denied.Verdict.Should().Be(PermissionAction.Deny);
        allowed.IsRisky.Should().BeFalse();
    }

    [Fact]
    public void RulesLoadedForADirectory_ResolvePathsAgainstThatDirectory_NotTheProcessDirectory()
    {
        var config = PermissionConfigLoader.LoadFromDefaultLocations(Root);

        config.WorkingDirectory.Should().Be(Root);
        new PermissionEvaluator(config).EvaluateRead(Path.Combine(Root, "src", "a.cs"))
            .OutsideWorkingDirectory.Should().BeFalse();
    }

    [Fact]
    public void TheDefaultConfiguration_StillAllowsOrdinaryReads_AndAsksAboutAPathThatLeavesTheWorkingDirectory()
    {
        var evaluator = new PermissionEvaluator(PermissionConfig.CreateDefault());

        evaluator.EvaluateRead("src/Program.cs").Action.Should().Be(PermissionAction.Allow);
        evaluator.EvaluateRead(Path.Combine(Directory.GetCurrentDirectory(), "src", "Program.cs"))
            .Action.Should().Be(PermissionAction.Allow);
        evaluator.EvaluateRead("../../outside.txt").Action.Should().Be(PermissionAction.Ask);
    }
}
