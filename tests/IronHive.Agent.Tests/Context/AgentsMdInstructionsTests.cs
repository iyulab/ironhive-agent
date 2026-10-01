using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// AGENTS.md files apply from the repository root down to the working directory, root first so the nearest comes last
/// and wins; nothing above the repository is read; an edit applies on the next turn.
/// </summary>
public sealed class AgentsMdInstructionsTests : IDisposable
{
    private readonly string _outer = Path.Combine(Path.GetTempPath(), "agentsmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _package;

    public AgentsMdInstructionsTests()
    {
        _repo = Path.Combine(_outer, "repo");
        _package = Path.Combine(_repo, "packages", "core");
        Directory.CreateDirectory(_package);
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_outer, "AGENTS.md"), "OUTSIDE the repository — never read");
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "Root rule: run the tests before committing.");
        File.WriteAllText(Path.Combine(_package, "AGENTS.md"), "Package rule: this package targets net10.0 only.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_outer, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task InAPackage_TheRootAndThePackageFilesApply_RootFirst_AndTheyReachTheModelAsSystemInstructions()
    {
        var manager = new ContextManager(new ContextTokenCounter("gpt-4o"), instructionContributors: [new AgentsMdInstructions(_package)]);

        var prepared = await manager.PrepareHistoryAsync(
            [new ChatMessage(ChatRole.System, "You are a file agent."), new ChatMessage(ChatRole.User, "hi")],
            TestContext.Current.CancellationToken);

        var section = prepared[1];
        Assert.Equal(ChatRole.System, section.Role);
        Assert.Contains("Root rule", section.Text);
        Assert.Contains("Package rule", section.Text);
        Assert.True(section.Text.IndexOf("Root rule", StringComparison.Ordinal) < section.Text.IndexOf("Package rule", StringComparison.Ordinal),
            "outermost first, so the nearest file comes last and wins");
        Assert.Contains("## packages/core/AGENTS.md", section.Text);
        Assert.DoesNotContain("OUTSIDE", section.Text);
    }

    [Fact]
    public void OutsideARepository_OnlyTheWorkingDirectorysOwnFileApplies()
    {
        var loose = Path.Combine(_outer, "loose", "deeper");
        Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, "AGENTS.md"), "Loose rule");

        var text = new AgentsMdInstructions(loose).GetInstructions();

        Assert.NotNull(text);
        Assert.Contains("Loose rule", text);
        Assert.DoesNotContain("OUTSIDE", text);
    }

    [Fact]
    public void ADirectoryWithoutTheFile_AddsNothing_WhenNoParentInTheRepositoryHasOne()
    {
        File.Delete(Path.Combine(_repo, "AGENTS.md"));
        var empty = Path.Combine(_repo, "docs");
        Directory.CreateDirectory(empty);

        Assert.Null(new AgentsMdInstructions(empty).GetInstructions());
    }

    [Fact]
    public void OverTheLimit_TheFarthestFilesAreLeftOutFirst_AndTheSectionSaysSo()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), new string('r', 5_000));

        var text = new AgentsMdInstructions(_package, new AgentsMdOptions { MaxCharacters = 1_000 }).GetInstructions();

        Assert.NotNull(text);
        Assert.Contains("Package rule", text);
        Assert.DoesNotContain(new string('r', 100), text);
        Assert.Contains("1 outer file(s) were left out", text);
    }

    [Fact]
    public void AnEdit_AppliesOnTheNextRead()
    {
        var contributor = new AgentsMdInstructions(_package);
        Assert.Contains("net10.0 only", contributor.GetInstructions());

        File.WriteAllText(Path.Combine(_package, "AGENTS.md"), "Package rule: multi-targets now.");

        Assert.Contains("multi-targets now", contributor.GetInstructions());
    }
}
