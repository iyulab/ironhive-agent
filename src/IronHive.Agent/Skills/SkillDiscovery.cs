using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace IronHive.Agent.Skills;

/// <summary>
/// Finds skills under the configured roots and validates their frontmatter against the Agent Skills
/// specification, as the reference validator (<c>skills-ref validate</c>) applies it. A plain call with
/// no agent involved, so a host can list what was found and what was rejected before a session exists,
/// and the loop consumes the same result — nobody writes a second parser.
/// </summary>
public static partial class SkillDiscovery
{
    /// <summary>The frontmatter keys the specification defines; any other key is <see cref="SkillDiagnosticCode.UnexpectedField"/>.</summary>
    private static readonly HashSet<string> AllowedFields =
        new(StringComparer.Ordinal) { "name", "description", "license", "allowed-tools", "metadata", "compatibility" };

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>Roots in, skills and diagnostics out. Nothing here throws for what it finds on disk.</summary>
    public static SkillDiscoveryResult Discover(SkillsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var found = new List<SkillManifest>();
        var diagnostics = new List<SkillDiagnostic>();
        var seen = new Dictionary<string, SkillManifest>(StringComparer.Ordinal);

        foreach (var root in config.Roots)
        {
            var rootPath = Path.GetFullPath(root);
            if (!Directory.Exists(rootPath))
            {
                diagnostics.Add(new(SkillDiagnosticCode.RootNotFound, root, $"Skills root '{root}' does not exist."));
                continue;
            }

            // Root order, then name: the same tree lists the same way every time.
            foreach (var dir in Directory.EnumerateDirectories(rootPath).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile))
                {
                    continue; // a folder without SKILL.md is not a skill, and not an error
                }

                var manifest = Parse(skillFile, dir, rootPath, config.UnknownFields, diagnostics);
                if (manifest is null)
                {
                    continue;
                }

                if (seen.TryGetValue(manifest.Name, out var earlier))
                {
                    diagnostics.Add(new(SkillDiagnosticCode.Shadowed, skillFile,
                        $"Skill '{manifest.Name}' under '{rootPath}' is shadowed by the one under '{earlier.Root}' (the first root wins)."));
                    continue;
                }

                seen[manifest.Name] = manifest;
                found.Add(manifest);
            }
        }

        var excluded = new HashSet<string>(config.Exclude, StringComparer.Ordinal);
        IEnumerable<SkillManifest> selected;
        if (config.Enabled is null)
        {
            selected = found;
        }
        else
        {
            // A name that matches nothing is a typo or a skill that failed validation — say so rather
            // than let it vanish, the same way a shadowed skill is reported.
            foreach (var missing in config.Enabled.Where(n => !seen.ContainsKey(n)))
            {
                diagnostics.Add(new(SkillDiagnosticCode.EnabledNotFound, missing,
                    $"Enabled skill '{missing}' was not found under any root (or did not validate — see the other diagnostics)."));
            }

            selected = config.Enabled.Where(seen.ContainsKey).Select(n => seen[n]);
        }

        selected = selected.Where(s => !excluded.Contains(s.Name));
        if (config.Filter is not null)
        {
            selected = selected.Where(config.Filter);
        }

        return new SkillDiscoveryResult(selected.ToList(), diagnostics);
    }

    /// <summary>
    /// Splits a <c>SKILL.md</c> into its YAML frontmatter and Markdown body: an opening <c>---</c> on the
    /// first line, a closing <c>---</c> on its own line. Returns <see langword="false"/> when either is missing.
    /// </summary>
    internal static bool TrySplitFrontmatter(string content, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = string.Empty;

        var text = content.TrimStart((char)0xFEFF);
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
        {
            return false;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r') != "---")
            {
                continue;
            }

            frontmatter = string.Join('\n', lines, 1, i - 1);
            body = i + 1 < lines.Length ? string.Join('\n', lines, i + 1, lines.Length - i - 1) : string.Empty;
            return true;
        }

        return false;
    }

    private static SkillManifest? Parse(string skillFile, string dir, string root, UnknownFieldPolicy unknownFields, List<SkillDiagnostic> diagnostics)
    {
        string content;
        try
        {
            content = File.ReadAllText(skillFile, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile, $"SKILL.md could not be read: {ex.Message}"));
            return null;
        }

        if (!TrySplitFrontmatter(content, out var yaml, out _))
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile,
                "SKILL.md must start with YAML frontmatter: a '---' line, the fields, and a closing '---' line."));
            return null;
        }

        Dictionary<object, object?>? map;
        try
        {
            map = Yaml.Deserialize<object>(yaml) as Dictionary<object, object?>;
        }
        catch (YamlException ex)
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile, $"Frontmatter is not valid YAML: {ex.Message}"));
            return null;
        }

        if (map is null)
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile, "Frontmatter must be a YAML mapping."));
            return null;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            fields[k?.ToString() ?? string.Empty] = v;
        }

        var unexpected = fields.Keys.Where(k => !AllowedFields.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unexpected.Count > 0)
        {
            var reject = unknownFields == UnknownFieldPolicy.Reject;
            diagnostics.Add(new(SkillDiagnosticCode.UnexpectedField, skillFile,
                $"Unexpected fields in frontmatter: {string.Join(", ", unexpected)}. Only {string.Join(", ", AllowedFields.OrderBy(k => k, StringComparer.Ordinal))} are allowed; extension keys belong under 'metadata'.",
                reject ? SkillDiagnosticSeverity.Error : SkillDiagnosticSeverity.Warning));
            if (reject)
            {
                return null;
            }

            foreach (var key in unexpected)
            {
                fields.Remove(key);
            }
        }

        var before = diagnostics.Count;

        var name = Scalar(fields, "name", skillFile, diagnostics);
        if (name is null)
        {
            diagnostics.Add(new(SkillDiagnosticCode.MissingName, skillFile, "Frontmatter is missing the required 'name'."));
        }
        else if (!ValidName(name))
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidName, skillFile,
                $"Name '{name}' must be 1-64 characters of a-z, 0-9 and single hyphens, not starting or ending with a hyphen."));
        }
        else if (name.Normalize(NormalizationForm.FormKC) != Path.GetFileName(dir).Normalize(NormalizationForm.FormKC))
        {
            diagnostics.Add(new(SkillDiagnosticCode.NameDoesNotMatchDirectory, skillFile,
                $"Directory name '{Path.GetFileName(dir)}' must match skill name '{name}'."));
        }

        var description = Scalar(fields, "description", skillFile, diagnostics);
        if (description is null || description.Length > 1024)
        {
            diagnostics.Add(new(SkillDiagnosticCode.MissingDescription, skillFile,
                "Frontmatter needs a 'description' of 1 to 1024 characters."));
        }

        var license = OptionalScalar(fields, "license", skillFile, diagnostics);
        var compatibility = OptionalScalar(fields, "compatibility", skillFile, diagnostics);
        if (fields.ContainsKey("compatibility") && (compatibility is null || compatibility.Length > 500))
        {
            diagnostics.Add(new(SkillDiagnosticCode.InvalidCompatibility, skillFile,
                "'compatibility' must be 1 to 500 characters when present."));
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fields.TryGetValue("metadata", out var metaValue))
        {
            if (metaValue is Dictionary<object, object?> metaMap && metaMap.All(kv => kv.Key is string && kv.Value is string))
            {
                foreach (var (k, v) in metaMap)
                {
                    metadata[(string)k] = (string)v!;
                }
            }
            else
            {
                diagnostics.Add(new(SkillDiagnosticCode.InvalidMetadata, skillFile, "'metadata' must be a map from string keys to string values."));
            }
        }

        var allowedTools = OptionalScalar(fields, "allowed-tools", skillFile, diagnostics);

        if (diagnostics.Count > before)
        {
            return null;
        }

        return new SkillManifest
        {
            Name = name!,
            Description = description!,
            License = license,
            Compatibility = compatibility,
            Metadata = metadata,
            AllowedTools = allowedTools is null ? [] : allowedTools.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Directory = dir,
            SkillFilePath = skillFile,
            Root = root
        };
    }

    /// <summary>A required non-empty string field; a wrong type is an invalid-frontmatter diagnostic, absence is left to the caller.</summary>
    private static string? Scalar(Dictionary<string, object?> fields, string key, string skillFile, List<SkillDiagnostic> diagnostics)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s.Length == 0 ? null : s;
        }

        diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile, $"'{key}' must be a string."));
        return null;
    }

    private static string? OptionalScalar(Dictionary<string, object?> fields, string key, string skillFile, List<SkillDiagnostic> diagnostics)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s.Length == 0 ? null : s;
        }

        diagnostics.Add(new(SkillDiagnosticCode.InvalidFrontmatter, skillFile, $"'{key}' must be a string."));
        return null;
    }

    private static bool ValidName(string name) =>
        name.Length is >= 1 and <= 64 && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
