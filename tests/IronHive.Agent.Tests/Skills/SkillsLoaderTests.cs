using System.Text.Json;
using IronHive.Agent.Skills;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Skills;

/// <summary>
/// The loader is progressive disclosure made enforceable: metadata is always in the system
/// instructions, the body arrives only through a tool, and that tool cannot read outside a skill's
/// own directory. What does not fit the metadata budget is dropped in a stated order and the count is
/// readable — a consumer shows the user why a skill never fires instead of guessing.
/// </summary>
public class SkillsLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skills-loader-{Guid.NewGuid():N}");

    public SkillsLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheMetadataSection_ListsNameAndDescription_AndTellsTheModelHowToLoadABody()
    {
        Skill("pdf-processing", "Extract PDF text. Use when handling PDFs.", "# PDF\n\nStep one.");
        Skill("code-review", "Review a diff.", "# Review");
        var loader = Load();

        var section = loader.Contributor.GetInstructions();

        Assert.NotNull(section);
        Assert.Contains("pdf-processing", section);
        Assert.Contains("Extract PDF text. Use when handling PDFs.", section);
        Assert.Contains("code-review", section);
        Assert.Contains(SkillsLoader.LoadToolName, section);
        Assert.DoesNotContain("Step one.", section);
        Assert.Equal("skills", loader.Contributor.Name);
    }

    [Fact]
    public async Task TheLoadTool_ReturnsTheBody_WithoutTheFrontmatter()
    {
        Skill("pdf-processing", "Extract PDF text.", "# PDF\n\nStep one.");
        var loader = Load();

        var body = await Invoke(loader.LoadTool, new { name = "pdf-processing" });

        Assert.Equal("# PDF\n\nStep one.", body.Trim().ReplaceLineEndings("\n"));
        Assert.DoesNotContain("---", body);
        Assert.DoesNotContain("description:", body);
    }

    [Fact]
    public async Task TheLoadTool_ReadsAFileInsideTheSkill_AndRefusesOneOutsideIt()
    {
        Skill("pdf-processing", "Extract PDF text.", "See references/REFERENCE.md");
        var refs = Path.Combine(_dir, "root", "pdf-processing", "references");
        Directory.CreateDirectory(refs);
        await File.WriteAllTextAsync(Path.Combine(refs, "REFERENCE.md"), "the reference", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_dir, "secret.txt"), "s3cr3t-content", TestContext.Current.CancellationToken);
        var loader = Load();

        var inside = await Invoke(loader.LoadTool, new { name = "pdf-processing", path = "references/REFERENCE.md" });
        var escape = await Invoke(loader.LoadTool, new { name = "pdf-processing", path = "../../secret.txt" });
        var rooted = await Invoke(loader.LoadTool, new { name = "pdf-processing", path = Path.Combine(_dir, "secret.txt") });

        Assert.Equal("the reference", inside);
        Assert.DoesNotContain("s3cr3t-content", escape);
        Assert.Contains("outside the skill", escape);
        Assert.DoesNotContain("s3cr3t-content", rooted);
        Assert.Contains("outside the skill", rooted);
    }

    [Fact]
    public async Task TheLoadTool_NamesTheAvailableSkills_WhenAskedForAnUnknownOne()
    {
        Skill("pdf-processing", "Extract PDF text.", "body");
        var loader = Load();

        var answer = await Invoke(loader.LoadTool, new { name = "nope" });

        Assert.Contains("nope", answer);
        Assert.Contains("pdf-processing", answer);
    }

    [Fact]
    public async Task ASkillExcludedFromTheSet_CannotBeLoadedByName()
    {
        Skill("pdf-processing", "Extract PDF text.", "body");
        Skill("hidden", "Not for this session.", "secret body");
        var loader = Load(cfg => cfg with { Exclude = ["hidden"] });

        var answer = await Invoke(loader.LoadTool, new { name = "hidden" });

        Assert.DoesNotContain("secret body", answer);
    }

    [Fact]
    public void OverBudget_TheTailIsDropped_InTheEnabledOrder_AndTheCountIsReadable()
    {
        foreach (var n in new[] { "a-skill", "b-skill", "c-skill", "d-skill" })
        {
            Skill(n, new string('x', 400), "body");
        }

        var oneEntry = SkillsLoader.RenderEntry(new SkillManifest
        {
            Name = "a-skill", Description = new string('x', 400), Directory = "", Root = "", SkillFilePath = ""
        }).Length;

        var loader = Load(cfg => cfg with
        {
            Enabled = ["d-skill", "c-skill", "b-skill", "a-skill"],
            MaxMetadataCharacters = SkillsLoader.RenderOverhead(2) + oneEntry * 2
        });

        var section = loader.Contributor.GetInstructions()!;
        Assert.Contains("d-skill", section);
        Assert.Contains("c-skill", section);
        Assert.DoesNotContain("b-skill\n", section);
        Assert.Equal(["b-skill", "a-skill"], loader.Dropped.Select(s => s.Name));
        Assert.Equal(2, loader.DroppedCount);
        Assert.Contains("2 more", section);
    }

    [Fact]
    public async Task ADroppedSkill_CanStillBeLoadedByName()
    {
        // The budget bounds what the model is told about, not what exists.
        Skill("a-skill", new string('x', 400), "a body");
        Skill("b-skill", new string('x', 400), "b body");
        var oneEntry = SkillsLoader.RenderEntry(new SkillManifest
        {
            Name = "a-skill", Description = new string('x', 400), Directory = "", Root = "", SkillFilePath = ""
        }).Length;
        var loader = Load(cfg => cfg with { Enabled = ["a-skill", "b-skill"], MaxMetadataCharacters = SkillsLoader.RenderOverhead(1) + oneEntry });

        Assert.Equal(["b-skill"], loader.Dropped.Select(s => s.Name));
        Assert.Equal("b body", (await Invoke(loader.LoadTool, new { name = "b-skill" })).Trim());
    }

    [Fact]
    public void NoSkills_MeansNoSection_AndNoTool()
    {
        var loader = Load();

        Assert.Null(loader.Contributor.GetInstructions());
        Assert.Empty(loader.Skills);
    }

    private SkillsLoader Load(Func<SkillsConfig, SkillsConfig>? adjust = null)
    {
        var cfg = new SkillsConfig { Roots = [Path.Combine(_dir, "root")] };
        return SkillsLoader.Create(adjust is null ? cfg : adjust(cfg));
    }

    private void Skill(string name, string description, string body)
    {
        var d = Path.Combine(_dir, "root", name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n{body}");
    }

    private static async Task<string> Invoke(AIFunction tool, object args)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await tool.InvokeAsync(new AIFunctionArguments(dict), TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }
}
