using IronHive.Agent.Mode;

namespace IronHive.Agent.Permissions;

/// <summary>
/// The one source of shell-command danger: which commands are refused whatever the rules say, and how risky a command the
/// rules leave to a human is.
/// </summary>
internal static class ShellCommandRisk
{
    // Refused outright, before any Bash rule: no rule and no human can make these safe.
    private static readonly string[] AlwaysDeniedPatterns =
    [
        ":(){:|:&};:",     // fork bomb
        "> /dev/sda",
        "dd if=/dev/",
        "mkfs.",
        "fdisk",
        "format c:",
        "rm -rf /",        // remove root
        "rm -rf /*",       // remove root contents
        "chmod 777 /",     // insecure permissions on root
        "chmod -r 777 /",  // recursive insecure permissions
        "| bash",          // pipe to a shell (remote code execution)
        "| sh",
        "| /bin/bash",
        "| /bin/sh",
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
        return AlwaysDeniedPatterns.Any(p => lower.Contains(p, StringComparison.Ordinal));
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
