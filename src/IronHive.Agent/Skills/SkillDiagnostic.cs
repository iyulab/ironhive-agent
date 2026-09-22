namespace IronHive.Agent.Skills;

/// <summary>Why a directory under a root did not become a usable skill, or a root could not be read.</summary>
public enum SkillDiagnosticCode
{
    /// <summary>A configured root does not exist.</summary>
    RootNotFound,
    /// <summary><c>SKILL.md</c> has no frontmatter, an unterminated one, or one that is not a YAML mapping of the expected shape.</summary>
    InvalidFrontmatter,
    /// <summary><c>name</c> is missing or empty.</summary>
    MissingName,
    /// <summary><c>name</c> breaks the specification's pattern or length.</summary>
    InvalidName,
    /// <summary><c>name</c> differs from the parent directory's name (compared after NFKC normalisation).</summary>
    NameDoesNotMatchDirectory,
    /// <summary><c>description</c> is missing, empty, or longer than 1024 characters.</summary>
    MissingDescription,
    /// <summary><c>compatibility</c> is present but empty or longer than 500 characters.</summary>
    InvalidCompatibility,
    /// <summary><c>metadata</c> is not a map of strings to strings.</summary>
    InvalidMetadata,
    /// <summary>The frontmatter has a key the specification does not define (an error by default; a warning under <see cref="UnknownFieldPolicy.Accept"/>).</summary>
    UnexpectedField,
    /// <summary>A name in <see cref="SkillsConfig.Enabled"/> matched no valid skill under any root.</summary>
    EnabledNotFound,
    /// <summary>A valid skill under a later root has the same name as one under an earlier root; the earlier one is used.</summary>
    Shadowed
}

/// <summary>Whether a diagnostic kept the skill out (<see cref="Error"/>) or only says something about a skill that loaded (<see cref="Warning"/>).</summary>
public enum SkillDiagnosticSeverity
{
    /// <summary>The skill (or root, or enabled name) was not used.</summary>
    Error,
    /// <summary>The skill loaded; this is a note about it.</summary>
    Warning
}

/// <summary>What to do with frontmatter keys the specification does not define.</summary>
public enum UnknownFieldPolicy
{
    /// <summary>Reject the skill, as the reference validator does.</summary>
    Reject,
    /// <summary>Load the skill and report the keys as a warning.</summary>
    Accept
}

/// <summary>One thing discovery could not accept or wants noted, with where it was found. Reported, never thrown.</summary>
/// <param name="Code">What kind of problem.</param>
/// <param name="Path">The <c>SKILL.md</c> (or root) it concerns.</param>
/// <param name="Message">A sentence a host can show as it is.</param>
/// <param name="Severity">Whether the skill was kept out or loaded with this note.</param>
public sealed record SkillDiagnostic(SkillDiagnosticCode Code, string Path, string Message, SkillDiagnosticSeverity Severity = SkillDiagnosticSeverity.Error);

/// <summary>What <see cref="SkillDiscovery.Discover"/> found: the usable skills in their final order, and everything it could not use.</summary>
/// <param name="Skills">Valid skills after <see cref="SkillsConfig.Enabled"/>, <see cref="SkillsConfig.Exclude"/> and <see cref="SkillsConfig.Filter"/>.</param>
/// <param name="Diagnostics">Rejected skills, shadowed skills and missing roots.</param>
public sealed record SkillDiscoveryResult(IReadOnlyList<SkillManifest> Skills, IReadOnlyList<SkillDiagnostic> Diagnostics);
