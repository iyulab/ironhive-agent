using System.ComponentModel;
using System.Globalization;
using System.Text;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Skills;

/// <summary>
/// Progressive disclosure of Agent Skills, made enforceable: every skill's name and description go into
/// the system instructions through an <see cref="ISystemInstructionContributor"/> (so they survive
/// compaction and do not depend on the host's prompt), and a skill's body — or a file inside its
/// directory — arrives only through <see cref="LoadTool"/>, which cannot read outside that directory.
/// </summary>
/// <remarks>
/// Create one per session with <see cref="Create"/> (or take <see cref="SkillDiscovery.Discover"/>'s result
/// and pass it to the constructor), add <see cref="Contributor"/> to the <see cref="ContextManager"/> and
/// <see cref="LoadTool"/> to the tool list. Text only: nothing here executes a skill's <c>scripts/</c>.
/// </remarks>
public sealed class SkillsLoader
{
    /// <summary>The name of the tool the model calls to read a skill's instructions.</summary>
    public const string LoadToolName = "load_skill";

    private readonly Dictionary<string, SkillManifest> _byName;
    private readonly string? _section;

    /// <summary>Discovers under <paramref name="config"/> and builds the loader.</summary>
    public static SkillsLoader Create(SkillsConfig config) => new(SkillDiscovery.Discover(config), config);

    /// <summary>Builds the loader from a discovery result a host already has.</summary>
    public SkillsLoader(SkillDiscoveryResult discovery, SkillsConfig config)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(config);

        Skills = discovery.Skills;
        Diagnostics = discovery.Diagnostics;
        _byName = Skills.ToDictionary(s => s.Name, StringComparer.Ordinal);

        (var listed, Dropped) = FitToBudget(Skills, config.MaxMetadataCharacters);
        _section = listed.Count == 0 ? null : RenderSection(listed, Dropped.Count);

        Contributor = new SectionContributor(_section);
        LoadTool = AIFunctionFactory.Create(LoadSkillAsync, new AIFunctionFactoryOptions
        {
            Name = LoadToolName,
            Description = "Loads the full instructions of an available skill by name. With a path (relative to the " +
                          "skill's directory, e.g. references/REFERENCE.md), reads that file of the skill instead."
        });
    }

    /// <summary>The skills this session may use, in the order the model sees them.</summary>
    public IReadOnlyList<SkillManifest> Skills { get; }

    /// <summary>What discovery could not use — for a host to show next to the list.</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Skills whose metadata did not fit <see cref="SkillsConfig.MaxMetadataCharacters"/>, dropped from the tail
    /// of the order. They are not in the instructions, but <see cref="LoadTool"/> still knows them by name.
    /// </summary>
    public IReadOnlyList<SkillManifest> Dropped { get; }

    /// <summary>How many skills were dropped for budget — the number a host should show the user.</summary>
    public int DroppedCount => Dropped.Count;

    /// <summary>The system-instructions section (name "skills"); adds nothing when no skill is available.</summary>
    public ISystemInstructionContributor Contributor { get; }

    /// <summary>The <c>load_skill</c> tool. Add it to the session's tools alongside the built-in ones.</summary>
    public AIFunction LoadTool { get; }

    /// <summary>One entry of the metadata section. Exposed so a host can size its budget.</summary>
    public static string RenderEntry(SkillManifest skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return $"- **{skill.Name}**: {skill.Description.ReplaceLineEndings(" ")}\n";
    }

    /// <summary>
    /// The characters the section takes besides its entries — the header, and the notice when
    /// <paramref name="dropped"/> skills are left out. A budget is entries plus this.
    /// </summary>
    public static int RenderOverhead(int dropped) => Header.Length + DroppedNotice(dropped).Length;

    private static (List<SkillManifest> Listed, List<SkillManifest> Dropped) FitToBudget(IReadOnlyList<SkillManifest> skills, int budget)
    {
        var listed = new List<SkillManifest>();
        var used = Header.Length + DroppedNotice(skills.Count).Length;

        foreach (var skill in skills)
        {
            var entry = RenderEntry(skill).Length;
            if (used + entry > budget)
            {
                break;
            }

            used += entry;
            listed.Add(skill);
        }

        return (listed, skills.Skip(listed.Count).ToList());
    }

    private const string Header =
        "# Skills\n" +
        "The skills below are available. Each entry is a name and when to use it; only the name and " +
        "description are here. To use one, call `" + LoadToolName + "` with its name — that returns the full " +
        "instructions. A file a skill refers to (for example references/REFERENCE.md) is read with the same " +
        "tool and a `path`.\n\n";

    private static string DroppedNotice(int dropped) =>
        dropped == 0 ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"\n({dropped} more skill(s) are available but not listed here; `{LoadToolName}` still accepts their names.)\n");

    private static string RenderSection(IReadOnlyList<SkillManifest> listed, int dropped)
    {
        var sb = new StringBuilder(Header);
        foreach (var skill in listed)
        {
            sb.Append(RenderEntry(skill));
        }

        sb.Append(DroppedNotice(dropped));
        return sb.ToString();
    }

    private async Task<string> LoadSkillAsync(
        [Description("The skill's name, as listed in the instructions.")] string name,
        [Description("Optional: a file inside the skill's directory, relative to it (e.g. references/REFERENCE.md). Omit to load the skill's own instructions.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (!_byName.TryGetValue(name ?? string.Empty, out var skill))
        {
            return _byName.Count == 0
                ? $"No skill named '{name}' — no skills are available in this session."
                : $"No skill named '{name}'. Available: {string.Join(", ", _byName.Keys.OrderBy(k => k, StringComparer.Ordinal))}.";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var content = await File.ReadAllTextAsync(skill.SkillFilePath, Encoding.UTF8, cancellationToken);
            return SkillDiscovery.TrySplitFrontmatter(content, out _, out var body) ? body : content;
        }

        // The skill's directory is the boundary: a rooted path or a `..` that leaves it is refused, and the
        // model is told why rather than given the file. This is what makes "body on demand" a contract.
        var skillDir = Path.GetFullPath(skill.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(skillDir, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(skillDir, comparison))
        {
            return $"'{path}' is outside the skill '{skill.Name}' and cannot be read through it. Paths are relative to the skill's directory.";
        }

        if (!File.Exists(full))
        {
            return $"The skill '{skill.Name}' has no file '{path}'.";
        }

        return await File.ReadAllTextAsync(full, Encoding.UTF8, cancellationToken);
    }

    private sealed class SectionContributor(string? section) : ISystemInstructionContributor
    {
        public string Name => "skills";
        public string? GetInstructions() => section;
    }
}
