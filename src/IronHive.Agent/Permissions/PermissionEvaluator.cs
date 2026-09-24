using System.Text.RegularExpressions;

namespace IronHive.Agent.Permissions;

/// <summary>
/// Default implementation of IPermissionEvaluator using glob pattern matching.
/// </summary>
public class PermissionEvaluator : IPermissionEvaluator
{
    private readonly PermissionConfig _config;
    private readonly Dictionary<string, Regex> _patternCache = new();

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
    public PermissionResult EvaluateBash(string command)
    {
        // First check for always-deny patterns (dangerous commands)
        if (IsDangerousCommand(command))
        {
            return PermissionResult.Deny("Potentially dangerous command");
        }

        return EvaluateRules(_config.Bash, command.Trim());
    }

    /// <inheritdoc />
    public PermissionResult EvaluateExternalDirectory(string directoryPath)
    {
        return EvaluateRules(_config.ExternalDirectory, ResolvePath(directoryPath).AbsolutePath);
    }

    // A path rule judges the path the tool will open. The file tools resolve their argument with
    // Path.GetFullPath against the working directory, so the policy resolves it the same way first:
    // inside the working directory it becomes the relative path the Read/Edit patterns describe;
    // outside, Read/Edit do not apply at all and the ExternalDirectory rules decide.
    private PermissionResult EvaluatePathRules(List<PermissionRule> rules, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return EvaluateRules(rules, string.Empty);
        }

        var resolved = ResolvePath(path);
        if (!resolved.IsExternal)
        {
            return EvaluateRules(rules, resolved.RelativePath);
        }

        var external = EvaluateRules(_config.ExternalDirectory, resolved.AbsolutePath);
        return external.MatchedRule is not null
            ? external with { OutsideWorkingDirectory = true }
            : new PermissionResult
            {
                Action = _config.DefaultAction,
                OutsideWorkingDirectory = true,
                Reason = "Path is outside the working directory and no external-directory rule covers it"
            };
    }

    private (string RelativePath, string AbsolutePath, bool IsExternal) ResolvePath(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_config.WorkingDirectory ?? Directory.GetCurrentDirectory()));

        string full;
        try
        {
            full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
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

    /// <inheritdoc />
    public PermissionResult EvaluateMcpTool(string toolName)
    {
        return EvaluateRules(_config.McpTools, toolName);
    }

    /// <inheritdoc />
    public PermissionResult EvaluateTool(string toolName)
    {
        return EvaluateRules(_config.Tools, toolName);
    }

    /// <inheritdoc />
    public bool IsReadOnlyTool(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _config.ReadOnlyTools.Any(pattern => MatchesPattern(toolName, pattern));

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

    private PermissionResult EvaluateRules(List<PermissionRule> rules, string target)
    {
        // Handle empty/null targets
        if (string.IsNullOrWhiteSpace(target))
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

        // Find all matching rules, sorted by priority (highest first)
        var matchingRules = rules
            .Where(r => MatchesPattern(target, r.Pattern))
            .OrderByDescending(r => r.Priority)
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

    private bool MatchesPattern(string input, string pattern)
    {
        var regex = GetOrCreatePatternRegex(pattern);
        return regex.IsMatch(input);
    }

    private Regex GetOrCreatePatternRegex(string pattern)
    {
        if (_patternCache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        var regexPattern = GlobToRegex(pattern);
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _patternCache[pattern] = regex;
        return regex;
    }

    /// <summary>
    /// Converts a glob pattern to a regex pattern.
    /// Supports: *, **, ?
    /// </summary>
    private static string GlobToRegex(string pattern)
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
                        // ** matches anything including path separators
                        result.Append(".*");
                        i++; // Skip next *

                        // Skip following path separator if present
                        if (i + 1 < pattern.Length && (pattern[i + 1] == '/' || pattern[i + 1] == '\\'))
                        {
                            i++;
                        }
                    }
                    else
                    {
                        // * matches anything except path separators
                        result.Append("[^/\\\\]*");
                    }
                    break;

                case '?':
                    // ? matches any single character except path separator
                    result.Append("[^/\\\\]");
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

        result.Append('$');
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

    private static bool IsDangerousCommand(string command)
    {
        var lowerCommand = command.ToLowerInvariant().Trim();

        // Critical danger patterns that should always be denied
        string[] criticalPatterns =
        [
            ":(){:|:&};:",     // Fork bomb
            "> /dev/sda",
            "dd if=/dev/",
            "mkfs.",
            "fdisk",
            "format c:",
            "rm -rf /",        // Remove root
            "rm -rf /*",       // Remove root contents
            "chmod 777 /",     // Insecure permissions on root
            "chmod -r 777 /",  // Recursive insecure permissions
            "| bash",          // Pipe to bash (remote code execution)
            "| sh",            // Pipe to sh (remote code execution)
            "| /bin/bash",
            "| /bin/sh",
        ];

        return criticalPatterns.Any(p => lowerCommand.Contains(p));
    }
}
