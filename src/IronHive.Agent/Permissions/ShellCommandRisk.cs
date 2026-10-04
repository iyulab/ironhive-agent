using System.Text.RegularExpressions;
using IronHive.Agent.Mode;

namespace IronHive.Agent.Permissions;

/// <summary>
/// The one source of shell-command danger: which commands are refused whatever the rules say, and how risky a command the
/// rules leave to a human is.
/// </summary>
internal static class ShellCommandRisk
{
    // Refused outright, before any Bash rule: no rule and no human can make these safe. Each pattern names the dangerous
    // command itself, not a prefix of it: "rm -rf /" is the root, not `rm -rf /tmp/build`; "| sh" is a shell, not
    // `| sha256sum`; dd is dangerous when it writes a disk device, not when it reads /dev/zero into a file. A command that
    // only resembles one of these goes to the rules (and to a human when they say Ask), as any other command does.
    // Matched against the lower-cased command.
    private const string End = @"(?=$|[\s;&|)])";

    private static readonly Regex[] AlwaysDeniedPatterns =
    [
        new(@":\(\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;\s*:", RegexOptions.CultureInvariant),                           // fork bomb
        new(@">\s*/dev/(sd|hd|vd|xvd|nvme|mmcblk|disk)", RegexOptions.CultureInvariant),                               // overwrite a disk
        new(@"\bdd\b[^;&|]*\bof=/dev/(sd|hd|vd|xvd|nvme|mmcblk|disk)", RegexOptions.CultureInvariant),                  // dd onto a disk
        new(@"\bmkfs(\.|\s)", RegexOptions.CultureInvariant),
        new(@"\bfdisk\b", RegexOptions.CultureInvariant),
        new(@"\bformat\s+[a-z]:", RegexOptions.CultureInvariant),
        new(@"\brm\s+(-[a-z]*\s+)*-[a-z]*(rf|fr)[a-z]*\s+(-[a-z-]+\s+)*/\*?" + End, RegexOptions.CultureInvariant),       // remove root
        new(@"\brm\s+(-r\s+-f|-f\s+-r)\s+/\*?" + End, RegexOptions.CultureInvariant),
        new(@"\bchmod\s+(-r\s+)?777\s+/" + End, RegexOptions.CultureInvariant),                                      // world-writable root
        new(@"\|\s*(/bin/|/usr/bin/)?(ba|z|da)?sh" + End, RegexOptions.CultureInvariant),                               // pipe into a shell
    ];

    // Destructive but not refused outright (a rule or a human decides): shown as Critical when asked about.
    private static readonly string[] CriticalPatterns =
    [
        "rm -rf",
        "del /s /q",
        "format ",
        "dd if=",
    ];

    /// <summary>True when <paramref name="command"/> is refused whatever the configured rules say.</summary>
    internal static bool IsAlwaysDenied(string command)
    {
        var lower = command.ToLowerInvariant().Trim();
        return AlwaysDeniedPatterns.Any(p => p.IsMatch(lower));
    }

    /// <summary>The risk level shown for a command the rules leave to a human.</summary>
    internal static RiskLevel Classify(string command)
    {
        var lower = command.ToLowerInvariant();

        if (IsAlwaysDenied(command) || CriticalPatterns.Any(p => lower.Contains(p, StringComparison.Ordinal)))
        {
            return RiskLevel.Critical;
        }

        if (lower.StartsWith("sudo ", StringComparison.Ordinal) ||
            lower.StartsWith("runas ", StringComparison.Ordinal) ||
            lower.Contains("chmod 777", StringComparison.Ordinal) ||
            (lower.Contains("curl", StringComparison.Ordinal) && lower.Contains("| sh", StringComparison.Ordinal)) ||
            (lower.Contains("wget", StringComparison.Ordinal) && lower.Contains("| sh", StringComparison.Ordinal)))
        {
            return RiskLevel.High;
        }

        return RiskLevel.Medium;
    }
}
