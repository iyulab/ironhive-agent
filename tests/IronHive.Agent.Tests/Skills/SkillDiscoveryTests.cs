using IronHive.Agent.Skills;

namespace IronHive.Agent.Tests.Skills;

/// <summary>
/// Discovery is a plain call — roots in, skills and diagnostics out — so a host can list what was
/// found and what was rejected before any agent session exists. The rules are the Agent Skills
/// specification's, as <c>skills-ref validate</c> applies them: what it accepts is accepted here,
/// what it rejects is reported here, never thrown.
/// </summary>
public class SkillDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");

    public SkillDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AValidSkill_IsFound_WithItsFrontmatterAndBodySeparated()
    {
        var root = Root("r1");
        Skill(root, "pdf-processing", """
            ---
            name: pdf-processing
            description: Extract PDF text, fill forms, merge files. Use when handling PDFs.
            license: Apache-2.0
            compatibility: Requires Python 3.14+ and uv
            metadata:
              author: example-org
              version: "1.0"
            allowed-tools: Bash(git:*) Bash(jq:*) Read
            ---
            # PDF processing

            Step one.
            """);

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Empty(result.Diagnostics);
        var skill = Assert.Single(result.Skills);
        Assert.Equal("pdf-processing", skill.Name);
        Assert.Equal("Extract PDF text, fill forms, merge files. Use when handling PDFs.", skill.Description);
        Assert.Equal("Apache-2.0", skill.License);
        Assert.Equal("Requires Python 3.14+ and uv", skill.Compatibility);
        Assert.Equal(new Dictionary<string, string> { ["author"] = "example-org", ["version"] = "1.0" }, skill.Metadata);
        Assert.Equal(["Bash(git:*)", "Bash(jq:*)", "Read"], skill.AllowedTools);
        Assert.Equal(Path.Combine(root, "pdf-processing"), skill.Directory);
        Assert.Equal(root, skill.Root);
    }

    [Theory]
    [InlineData("name: PDF-Processing", "PDF-Processing")]
    [InlineData("name: -pdf", "-pdf")]
    [InlineData("name: pdf--processing", "pdf--processing")]
    [InlineData("name: pdf_processing", "pdf_processing")]
    public void ANameOutsideTheSpec_IsReported_NotLoaded(string nameLine, string dirName)
    {
        var root = Root("r1");
        Skill(root, dirName, $"---\n{nameLine}\ndescription: d\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Empty(result.Skills);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(SkillDiagnosticCode.InvalidName, d.Code);
        Assert.Equal(Path.Combine(root, dirName, "SKILL.md"), d.Path);
    }

    [Fact]
    public void ANameThatDoesNotMatchItsDirectory_IsReported()
    {
        var root = Root("r1");
        Skill(root, "one-thing", "---\nname: other-thing\ndescription: d\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Empty(result.Skills);
        Assert.Equal(SkillDiagnosticCode.NameDoesNotMatchDirectory, Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("---\nname: a-skill\n---\nbody", SkillDiagnosticCode.MissingDescription)]
    [InlineData("---\ndescription: d\n---\nbody", SkillDiagnosticCode.MissingName)]
    [InlineData("---\nname: a-skill\ndescription: d\nauthor: me\n---\nbody", SkillDiagnosticCode.UnexpectedField)]
    [InlineData("---\nname: a-skill\ndescription: d\nmetadata:\n  n: 1\n  nested:\n    k: v\n---\nbody", SkillDiagnosticCode.InvalidMetadata)]
    [InlineData("---\nname: a-skill\ndescription: [not, a, string]\n---\nbody", SkillDiagnosticCode.InvalidFrontmatter)]
    [InlineData("no frontmatter at all", SkillDiagnosticCode.InvalidFrontmatter)]
    [InlineData("---\nname: a-skill\ndescription: d\n", SkillDiagnosticCode.InvalidFrontmatter)]
    public void FrontmatterTheReferenceValidatorRejects_IsReported(string content, SkillDiagnosticCode expected)
    {
        var root = Root("r1");
        Skill(root, "a-skill", content);

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Empty(result.Skills);
        Assert.Contains(result.Diagnostics, d => d.Code == expected);
    }

    [Fact]
    public void LengthLimits_AreTheSpecs()
    {
        var root = Root("r1");
        Skill(root, "a-skill", $"---\nname: a-skill\ndescription: {new string('d', 1025)}\n---\nbody");
        Skill(root, "b-skill", $"---\nname: b-skill\ndescription: d\ncompatibility: {new string('c', 501)}\n---\nbody");
        Skill(root, "c-skill", $"---\nname: c-skill\ndescription: {new string('d', 1024)}\ncompatibility: {new string('c', 500)}\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Equal(["c-skill"], result.Skills.Select(s => s.Name));
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void ADirectoryWithoutSkillMd_IsNotASkill_AndNotAnError()
    {
        var root = Root("r1");
        Directory.CreateDirectory(Path.Combine(root, "just-a-folder"));
        File.WriteAllText(Path.Combine(root, "README.md"), "not a skill");
        Skill(root, "a-skill", "---\nname: a-skill\ndescription: d\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });

        Assert.Equal(["a-skill"], result.Skills.Select(s => s.Name));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void AMissingRoot_IsReported_AndTheOthersStillLoad()
    {
        var root = Root("r1");
        Skill(root, "a-skill", "---\nname: a-skill\ndescription: d\n---\nbody");
        var missing = Path.Combine(_dir, "does-not-exist");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [missing, root] });

        Assert.Equal(["a-skill"], result.Skills.Select(s => s.Name));
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(SkillDiagnosticCode.RootNotFound, d.Code);
        Assert.Equal(missing, d.Path);
    }

    [Fact]
    public void TheSameName_UnderTwoRoots_TheFirstRootWins_AndTheShadowedOneIsReported()
    {
        var r1 = Root("r1");
        var r2 = Root("r2");
        Skill(r1, "a-skill", "---\nname: a-skill\ndescription: from r1\n---\nbody");
        Skill(r2, "a-skill", "---\nname: a-skill\ndescription: from r2\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [r1, r2] });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("from r1", skill.Description);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(SkillDiagnosticCode.Shadowed, d.Code);
        Assert.Equal(Path.Combine(r2, "a-skill", "SKILL.md"), d.Path);
    }

    [Fact]
    public void Order_IsRootOrder_ThenName_AndIsStable()
    {
        var r1 = Root("r1");
        var r2 = Root("r2");
        Skill(r1, "zeta", "---\nname: zeta\ndescription: d\n---\nb");
        Skill(r1, "alpha", "---\nname: alpha\ndescription: d\n---\nb");
        Skill(r2, "beta", "---\nname: beta\ndescription: d\n---\nb");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [r1, r2] });

        Assert.Equal(["alpha", "zeta", "beta"], result.Skills.Select(s => s.Name));
    }

    [Fact]
    public void EnabledAndExclude_NarrowTheSet_AndEnabledFixesTheOrder()
    {
        var root = Root("r1");
        foreach (var n in new[] { "a-skill", "b-skill", "c-skill", "d-skill" })
        {
            Skill(root, n, $"---\nname: {n}\ndescription: d\n---\nb");
        }

        var result = SkillDiscovery.Discover(new SkillsConfig
        {
            Roots = [root],
            Enabled = ["d-skill", "b-skill", "a-skill"],
            Exclude = ["a-skill"]
        });

        Assert.Equal(["d-skill", "b-skill"], result.Skills.Select(s => s.Name));
    }

    [Fact]
    public void AFilter_IsAppliedLast()
    {
        var root = Root("r1");
        Skill(root, "a-skill", "---\nname: a-skill\ndescription: d\nmetadata:\n  tier: pro\n---\nb");
        Skill(root, "b-skill", "---\nname: b-skill\ndescription: d\n---\nb");

        var result = SkillDiscovery.Discover(new SkillsConfig
        {
            Roots = [root],
            Filter = s => !s.Metadata.TryGetValue("tier", out var t) || t != "pro"
        });

        Assert.Equal(["b-skill"], result.Skills.Select(s => s.Name));
    }

    [Fact]
    public void UnknownFields_AreAnErrorByDefault_AndAWarningWhenAccepted()
    {
        var root = Root("r1");
        Skill(root, "a-skill", "---\nname: a-skill\ndescription: d\nargument-hint: <x>\nwhen_to_use: always\n---\nbody");

        var strict = SkillDiscovery.Discover(new SkillsConfig { Roots = [root] });
        var lenient = SkillDiscovery.Discover(new SkillsConfig { Roots = [root], UnknownFields = UnknownFieldPolicy.Accept });

        Assert.Empty(strict.Skills);
        Assert.Equal(SkillDiagnosticSeverity.Error, Assert.Single(strict.Diagnostics).Severity);

        Assert.Equal(["a-skill"], lenient.Skills.Select(s => s.Name));
        var warning = Assert.Single(lenient.Diagnostics);
        Assert.Equal(SkillDiagnosticCode.UnexpectedField, warning.Code);
        Assert.Equal(SkillDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("argument-hint, when_to_use", warning.Message);
        Assert.Contains("metadata", warning.Message);
    }

    [Fact]
    public void AnEnabledName_ThatMatchesNothing_IsReported_NotSilentlyDropped()
    {
        var root = Root("r1");
        Skill(root, "a-skill", "---\nname: a-skill\ndescription: d\n---\nbody");

        var result = SkillDiscovery.Discover(new SkillsConfig { Roots = [root], Enabled = ["a-skill", "a-skil"] });

        Assert.Equal(["a-skill"], result.Skills.Select(s => s.Name));
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(SkillDiagnosticCode.EnabledNotFound, d.Code);
        Assert.Equal("a-skil", d.Path);
    }

    private string Root(string name)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Skill(string root, string dirName, string content)
    {
        var d = Path.Combine(root, dirName);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SKILL.md"), content.ReplaceLineEndings("\n"));
    }
}
