namespace IronHive.Agent.Skills;

/// <summary>
/// A skill's frontmatter as the specification defines it, plus where it was found. The body is not
/// here — it is read on demand by <see cref="SkillsLoader.LoadTool"/>, which is the point of the format.
/// </summary>
public sealed record SkillManifest
{
    /// <summary>The required <c>name</c>: 1–64 characters, <c>a-z</c>, <c>0-9</c> and single hyphens, equal to the directory name.</summary>
    public required string Name { get; init; }

    /// <summary>The required <c>description</c>: what the skill does and when to use it (1–1024 characters).</summary>
    public required string Description { get; init; }

    /// <summary>The optional <c>license</c>.</summary>
    public string? License { get; init; }

    /// <summary>The optional <c>compatibility</c> (1–500 characters when present).</summary>
    public string? Compatibility { get; init; }

    /// <summary>The optional <c>metadata</c> map (string to string).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The optional <c>allowed-tools</c>, split on whitespace. Experimental in the specification; parsed and
    /// exposed here, not enforced — a host's permission layer decides what that means.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>The skill's directory — the boundary <see cref="SkillsLoader.LoadTool"/> reads within.</summary>
    public required string Directory { get; init; }

    /// <summary>The <c>SKILL.md</c> file.</summary>
    public required string SkillFilePath { get; init; }

    /// <summary>The configured root this skill was found under.</summary>
    public required string Root { get; init; }
}
