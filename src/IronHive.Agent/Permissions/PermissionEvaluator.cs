using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace IronHive.Agent.Permissions;

/// <summary>
/// Default implementation of IPermissionEvaluator using glob pattern matching.
/// </summary>
public class PermissionEvaluator : IPermissionEvaluator
{
    private readonly PermissionConfig _config;
    private readonly ConcurrentDictionary<(string Pattern, bool ForPaths), Regex> _patternCache = new();

    public PermissionEvaluator(PermissionConfig? config = null)
    {
        _config = config ?? PermissionConfig.CreateDefault();
    }

    /// <inheritdoc />
    public PermissionResult EvaluateRead(string filePath)
    {
        return EvaluatePathRules(_config.Read, filePath);
    }

    /// <inheritdoc />
    public PermissionResult EvaluateEdit(string filePath)
    {
        return EvaluatePathRules(_config.Edit, filePath);
    }

    /// <inheritdoc />
    public PermissionResult EvaluateDelete(string filePath)
    {
        var edit = EvaluateEdit(filePath);
        if (edit.Action == PermissionAction.Allow && _config.AskBeforeDelete)
        {
            // The edit rules allow it; deleting still asks. The allowing rule stays as the match, so a caller can tell
            // this confirmation from a rule that asks on its own.
            return new PermissionResult
            {
                Action = PermissionAction.Ask,
                MatchedRule = edit.MatchedRule ?? new PermissionRule { Pattern = "*", Action = PermissionAction.Allow },
                OutsideWorkingDirectory = edit.OutsideWorkingDirectory,
                Reason = "File deletion (AskBeforeDelete)"
            };
        }

        return edit;
    }

    /// <inheritdoc />
    public PermissionResult EvaluateBash(string command)
    {
        // First check for always-deny patterns (dangerous commands)
        if (ShellCommandRisk.IsAlwaysDenied(command))
        {
            return PermissionResult.Deny("Potentially dangerous command");
        }

        var line = command.Trim();
        var verdict = EvaluateRules(_config.Bash, line);
        if (verdict.Action != PermissionAction.Allow || verdict.MatchedRule is not { } rule
            || CoversWhatAShellMakesOfIt(rule.Pattern) || !CanRunMoreThanItsCommand(line))
        {
            return verdict;
        }

        // A rule that is this very line is the way to keep allowing it, also where a broader rule of the same priority
        // matched first. One below the deciding priority does not change the question.
        var asWritten = _config.Bash.FirstOrDefault(other => other.Action == PermissionAction.Allow
            && other.Priority == rule.Priority && CoversWhatAShellMakesOfIt(other.Pattern) && MatchesPattern(line, other.Pattern));
        if (asWritten is not null)
        {
            return new PermissionResult { Action = PermissionAction.Allow, MatchedRule = asWritten, Reason = asWritten.Reason };
        }

        // The rule allowed one command; this line can run more than that. It stays as the match, as for AskBeforeDelete.
        return new PermissionResult
        {
            Action = PermissionAction.Ask,
            MatchedRule = rule,
            Reason = $"The command line can run more than one command or redirect it, which the rule '{rule.Pattern}' does not cover"
        };
    }

    // The two rules taken at their word: one that is the exact line, and one that allows every command.
    private static bool CoversWhatAShellMakesOfIt(string pattern) =>
        !pattern.AsSpan().ContainsAny('*', '?') || pattern.AsSpan().IndexOfAnyExcept('*') < 0;

    // What lets one line do more than the command it starts with, in cmd.exe or in bash: a separator or a pipe, a
    // redirection, a command substitution - and bash's "${" and "$[", through which a value the line builds is evaluated
    // again and can run a command with none of the others in sight. The line is not parsed - the two shells quote
    // differently, and a quoting rule only one of them follows would let a command through - so a quoted operator counts.
    private static bool CanRunMoreThanItsCommand(string line) =>
        line.AsSpan().ContainsAny(ShellControlCharacters)
        || line.Contains("$(", StringComparison.Ordinal)
        || line.Contains("${", StringComparison.Ordinal)
        || line.Contains("$[", StringComparison.Ordinal);

    private static readonly System.Buffers.SearchValues<char> ShellControlCharacters =
        System.Buffers.SearchValues.Create(";&|<>`\r\n");

    /// <inheritdoc />
    public PermissionResult EvaluateExternalDirectory(string directoryPath)
    {
        return EvaluateRules(_config.ExternalDirectory, ResolvePath(directoryPath).AbsolutePath, forPaths: true);
    }

    // A path rule judges the path the tool will open. The file tools resolve their argument with
    // Path.GetFullPath against the working directory, so the policy resolves it the same way first:
    // inside the working directory it becomes the relative path the Read/Edit patterns describe;
    // outside, Read/Edit do not apply at all and the ExternalDirectory rules decide.
    private PermissionResult EvaluatePathRules(List<PermissionRule> rules, string path)
    {
        // Only no text at all is "no path". Blanks are a name as far as the tool is concerned - on Windows they resolve to the
        // folder they are written in - so they are resolved and judged like any other path.
        if (string.IsNullOrEmpty(path))
        {
            return EvaluateRules(rules, string.Empty, forPaths: true);
        }

        var resolved = ResolvePath(path);
        if (!resolved.IsExternal)
        {
            return EvaluateRules(rules, resolved.RelativePath, forPaths: true);
        }

        var external = EvaluateRules(_config.ExternalDirectory, resolved.AbsolutePath, forPaths: true);
        if (external.MatchedRule is not null)
        {
            return external with { OutsideWorkingDirectory = true };
        }

        // A name that cannot be turned into a drive path may be another name of a file a rule speaks about, so a default
        // of Allow does not stretch to it.
        var unmapped = _config.DefaultAction == PermissionAction.Allow && IsUnmappedName(resolved.AbsolutePath);
        return new PermissionResult
        {
            Action = unmapped ? PermissionAction.Ask : _config.DefaultAction,
            OutsideWorkingDirectory = true,
            Reason = unmapped
                ? "A network or device name of a path cannot be matched against the rules for local files"
                : "Path is outside the working directory and no external-directory rule covers it"
        };
    }

    // A share, a volume or another device, as opposed to a drive path; the path is given with '/' separators.
    private static bool IsUnmappedName(string absolutePath) =>
        OperatingSystem.IsWindows()
        && (absolutePath.StartsWith("//", StringComparison.Ordinal) || absolutePath.StartsWith("/??/", StringComparison.Ordinal));

    private (string RelativePath, string AbsolutePath, bool IsExternal) ResolvePath(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(
            AsOpened(_config.WorkingDirectory ?? Directory.GetCurrentDirectory()));

        string full;
        try
        {
            full = AsOpened(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the runtime cannot resolve is not a path inside the working directory.
            var text = NormalizePath(path);
            return (text, text, true);
        }

        var relative = Path.GetRelativePath(root, full);
        var isExternal = relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative);

        return (relative.Replace('\\', '/'), full.Replace('\\', '/'), isExternal);
    }

    // The file a name opens, in one spelling. On Windows one file has many: normalization comes first, because
    // "\\.\.\C:\x" and "\\.\Z:\..\C:\x" only show their drive once normalized; then the device prefix goes, and what it
    // kept from being normalized is normalized; then NTFS stream names ("dir::$INDEX_ALLOCATION\x" opens dir\x).
    private static string AsOpened(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        var plain = Path.GetFullPath(WithoutDevicePrefix(full));
        var named = WithoutStreamNames(plain);
        return ReferenceEquals(named, plain) ? plain : Path.GetFullPath(named);
    }

    // "\\?\C:\x", "\\.\C:\x" and "\??\C:\x" open the same file as "C:\x" ("\\?\UNC\server\share" as "\\server\share").
    // Judged as written they are on another root, so no Read or Edit rule would speak about a file inside the working
    // directory.
    private static string WithoutDevicePrefix(string path)
    {
        if (path.Length < 4)
        {
            return path;
        }

        var spelled = path.Replace('/', '\\');
        if (!spelled.StartsWith(@"\\?\", StringComparison.Ordinal) && !spelled.StartsWith(@"\\.\", StringComparison.Ordinal)
            && !spelled.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return path;
        }

        var rest = spelled[4..];
        if (rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\')
        {
            return rest;
        }

        // Any other device (a volume, a pipe) has no drive spelling and stays what it is: outside the working directory
        return rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + rest[4..] : path;
    }

    // In the names below the root a ':' starts a stream name. The file or folder it belongs to is what the rules speak
    // about.
    private static string WithoutStreamNames(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        if (path.AsSpan(root.Length).IndexOf(':') < 0)
        {
            return path;
        }

        var names = path[root.Length..].Split('\\')
            .Select(name => name.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? name[..colon] : name);
        return root + string.Join('\\', names);
    }

    /// <inheritdoc />
    public PermissionResult EvaluateMcpTool(string toolName)
    {
        return EvaluateRules(_config.McpTools, toolName);
    }

    /// <inheritdoc />
    public PermissionResult EvaluateTool(string toolName)
    {
        var result = EvaluateRules(_config.Tools, toolName);

        // A permission file writes its own Tools rules and cannot know the built-in tools that touch no path and only return
        // or carry text (propose_for_approval, advisor, retrieve_tool_result): with no rule about them the default action
        // decided, and an Ask default made the approval-proposal tool wait for its own approval. So such a tool that no rule
        // speaks about is allowed when the default would only ask - a rule (allow, ask or deny) wins, and so does a default of
        // Deny, as for a trusted read-only MCP tool. The tools that look at files are not lifted: their path rules decide.
        if (result.MatchedRule is null
            && result.Action == PermissionAction.Ask
            && BuiltInToolNames.PathFreeReadOnlyPatterns.Any(pattern => MatchesPattern(toolName, pattern)))
        {
            return new PermissionResult
            {
                Action = PermissionAction.Allow,
                Reason = "Built-in read-only tool"
            };
        }

        return result;
    }

    /// <inheritdoc />
    public bool IsReadOnlyTool(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && (BuiltInToolNames.ReadOnlyPatterns.Any(pattern => MatchesPattern(toolName, pattern))
            || _config.ReadOnlyTools.Any(pattern => MatchesPattern(toolName, pattern)));

    /// <inheritdoc />
    public PermissionResult Evaluate(string permissionType, string target)
    {
        return permissionType.ToLowerInvariant() switch
        {
            "read" => EvaluateRead(target),
            "edit" or "write" => EvaluateEdit(target),
            "bash" or "shell" => EvaluateBash(target),
            "external_directory" or "directory" => EvaluateExternalDirectory(target),
            "mcp" or "mcp_tool" => EvaluateMcpTool(target),
            "tool" or "function" => EvaluateTool(target),
            _ => new PermissionResult { Action = _config.DefaultAction, Reason = "Unknown permission type" }
        };
    }

    private PermissionResult EvaluateRules(List<PermissionRule> rules, string target, bool forPaths = false)
    {
        // Handle empty/null targets. A name made of blanks is still a name the rules can match.
        if (string.IsNullOrEmpty(target))
        {
            return new PermissionResult
            {
                Action = _config.DefaultAction,
                Reason = "Empty or null target, using default"
            };
        }

        if (rules.Count == 0)
        {
            return new PermissionResult
            {
                Action = _config.DefaultAction,
                Reason = "No rules configured, using default"
            };
        }

        // Find all matching rules, highest priority first. Among rules of one priority the stricter action goes first - the
        // built-in rules are listed before the ones a host adds, so list order must not let an allow override an added deny.
        // Rules that agree keep their list order.
        var matchingRules = rules
            .Where(r => MatchesPattern(target, r.Pattern, forPaths))
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => Strictness(r.Action))
            .ToList();

        if (matchingRules.Count == 0)
        {
            return new PermissionResult
            {
                Action = _config.DefaultAction,
                Reason = "No matching rule found, using default"
            };
        }

        // Use the highest priority matching rule
        var rule = matchingRules[0];
        return new PermissionResult
        {
            Action = rule.Action,
            MatchedRule = rule,
            Reason = rule.Reason
        };
    }

    private static int Strictness(PermissionAction action) => action switch
    {
        PermissionAction.Deny => 2,
        PermissionAction.Ask => 1,
        _ => 0,
    };

    private bool MatchesPattern(string input, string pattern, bool forPaths = false)
    {
        var regex = GetOrCreatePatternRegex(pattern, forPaths);
        return regex.IsMatch(input);
    }

    private Regex GetOrCreatePatternRegex(string pattern, bool forPaths)
    {
        // The evaluator is asked from the tools' own bodies now (a read filter), possibly from several calls at once.
        // "." crosses line breaks, so a rule still speaks about a command of several lines or a name that holds one.
        return _patternCache.GetOrAdd((pattern, forPaths), static key => new Regex(
            GlobToRegex(key.Pattern, key.ForPaths),
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline));
    }

    /// <summary>
    /// Converts a glob pattern to a regex pattern.
    /// Supports: *, **, ?
    /// </summary>
    /// <param name="pattern">The glob.</param>
    /// <param name="forPaths">
    /// In a path pattern "*" and "?" stay inside one name and "**/" stands for whole folders. A command line and a tool name
    /// are not paths: there every wildcard is any text, so a deny keeps reaching across "/" and "\".
    /// </param>
    private static string GlobToRegex(string pattern, bool forPaths)
    {
        var result = new System.Text.StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    // Check for **
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++; // Skip next *

                        if (i + 1 < pattern.Length && (pattern[i + 1] == '/' || pattern[i + 1] == '\\'))
                        {
                            i++;
                            // In a path, "**/" before a name is any number of whole folders, none included: what follows
                            // starts a name. A pattern that ends in "**/" covers everything under what precedes it.
                            result.Append(forPaths && i + 1 < pattern.Length ? "(?:.*[/\\\\])?" : ".*");
                        }
                        else
                        {
                            // ** matches anything including path separators
                            result.Append(".*");
                        }
                    }
                    else
                    {
                        // in a path, * matches anything except separators
                        result.Append(forPaths ? "[^/\\\\]*" : ".*");
                    }
                    break;

                case '?':
                    // in a path, ? matches any single character except a separator
                    result.Append(forPaths ? "[^/\\\\]" : ".");
                    break;

                case '.':
                case '+':
                case '^':
                case '$':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '|':
                case '\\':
                    // Escape regex special characters
                    result.Append('\\');
                    result.Append(c);
                    break;

                default:
                    result.Append(c);
                    break;
            }
        }

        // \z, not $: $ also matches before a final line break.
        result.Append(@"\z");
        return result.ToString();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        // Normalize path separators to forward slashes for consistent matching
        return path.Replace('\\', '/').TrimStart('/');
    }
}
