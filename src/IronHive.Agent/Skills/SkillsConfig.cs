namespace IronHive.Agent.Skills;

/// <summary>
/// What a host decides about Agent Skills (<c>SKILL.md</c> bundles, <see href="https://agentskills.io/specification"/>):
/// where they are, which of them this session may use, and how much of the system instructions their
/// metadata may take. Shaped like <see cref="Mcp.McpPluginsConfig"/>: roots, enable/exclude, a per-session predicate.
/// </summary>
public sealed record SkillsConfig
{
    /// <summary>
    /// Directories whose immediate subdirectories are skills (each holding a <c>SKILL.md</c>). Order is
    /// precedence: when two roots hold a skill of the same name, the first root's is used and the other
    /// is reported as <see cref="SkillDiagnosticCode.Shadowed"/>. A root that does not exist is reported,
    /// not thrown.
    /// </summary>
    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>
    /// Names to use, in the order the model should see them; <see langword="null"/> means every valid
    /// skill found, in root order then by name. This order is also the drop order when the metadata does
    /// not fit <see cref="MaxMetadataCharacters"/>: the tail goes first.
    /// </summary>
    public IReadOnlyList<string>? Enabled { get; init; }

    /// <summary>Names never used, whatever <see cref="Enabled"/> says.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>
    /// A per-session predicate applied after <see cref="Enabled"/> and <see cref="Exclude"/> — for a
    /// host that decides by the skill's <see cref="SkillManifest.Metadata"/> or <see cref="SkillManifest.Compatibility"/>.
    /// </summary>
    public Func<SkillManifest, bool>? Filter { get; init; }

    /// <summary>
    /// What to do with a frontmatter key the specification does not define. The default rejects the
    /// skill, as <c>skills-ref validate</c> does. <see cref="UnknownFieldPolicy.Accept"/> loads it and
    /// reports the keys as a <see cref="SkillDiagnosticSeverity.Warning"/> — for skill trees written
    /// for a client that adds its own keys. The specification's slot for such keys is <c>metadata</c>.
    /// </summary>
    public UnknownFieldPolicy UnknownFields { get; init; } = UnknownFieldPolicy.Reject;

    /// <summary>
    /// The most characters the skills section of the system instructions may take (the specification
    /// budgets about 100 tokens per skill). Entries past the budget are dropped from the tail of the
    /// order and counted in <see cref="SkillsLoader.DroppedCount"/>; a dropped skill can still be loaded
    /// by name. Default 12 000 (roughly 3 000 tokens).
    /// </summary>
    public int MaxMetadataCharacters { get; init; } = 12_000;
}
