namespace IronHive.Agent.Permissions;

/// <summary>
/// Configuration for permission rules organized by permission type.
/// </summary>
public class PermissionConfig
{
    /// <summary>
    /// Rules for file read operations.
    /// Patterns match file paths.
    /// </summary>
    public List<PermissionRule> Read { get; set; } = [];

    /// <summary>
    /// Rules for file edit/write operations.
    /// Patterns match file paths.
    /// </summary>
    public List<PermissionRule> Edit { get; set; } = [];

    /// <summary>
    /// Rules for shell/bash command execution.
    /// Patterns match command strings.
    /// </summary>
    public List<PermissionRule> Bash { get; set; } = [];

    /// <summary>
    /// Rules for accessing directories outside the working directory.
    /// Patterns match directory paths.
    /// </summary>
    public List<PermissionRule> ExternalDirectory { get; set; } = [];

    /// <summary>
    /// Rules for MCP tool calls.
    /// Patterns match tool names (e.g., "mcp__*", "memory_indexer_*").
    /// </summary>
    public List<PermissionRule> McpTools { get; set; } = [];

    /// <summary>
    /// Default action when no rule matches.
    /// </summary>
    public PermissionAction DefaultAction { get; set; } = PermissionAction.Ask;

    /// <summary>
    /// Creates a default permissive configuration for development.
    /// </summary>
    public static PermissionConfig CreateDefault() => new()
    {
        Read =
        [
            new() { Pattern = "**/*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "*.env", Action = PermissionAction.Ask, Priority = 10, Reason = "May contain secrets" },
            new() { Pattern = ".env*", Action = PermissionAction.Ask, Priority = 10, Reason = "May contain secrets" },
            new() { Pattern = "**/.env*", Action = PermissionAction.Ask, Priority = 10, Reason = "May contain secrets" },
            new() { Pattern = "**/secrets/**", Action = PermissionAction.Deny, Priority = 20, Reason = "Protected directory" }
        ],
        Edit =
        [
            new() { Pattern = "src/**/*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "tests/**/*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "*.json", Action = PermissionAction.Ask, Priority = 5, Reason = "Configuration file" },
            new() { Pattern = "*.yaml", Action = PermissionAction.Ask, Priority = 5, Reason = "Configuration file" },
            new() { Pattern = "*.yml", Action = PermissionAction.Ask, Priority = 5, Reason = "Configuration file" }
        ],
        Bash =
        [
            new() { Pattern = "git *", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "dotnet *", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "npm *", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "cargo *", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "rm -rf *", Action = PermissionAction.Deny, Priority = 100, Reason = "Destructive command" },
            new() { Pattern = "sudo *", Action = PermissionAction.Deny, Priority = 100, Reason = "Elevated privileges" },
            new() { Pattern = "curl * | *sh*", Action = PermissionAction.Deny, Priority = 100, Reason = "Remote code execution" }
        ],
        DefaultAction = PermissionAction.Ask
    };

    /// <summary>
    /// Creates a restrictive configuration for untrusted environments.
    /// </summary>
    public static PermissionConfig CreateRestrictive() => new()
    {
        Read =
        [
            new() { Pattern = "src/**/*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "tests/**/*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "docs/**/*", Action = PermissionAction.Allow, Priority = 0 }
        ],
        Edit = [],
        Bash =
        [
            new() { Pattern = "git status", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "git diff*", Action = PermissionAction.Allow, Priority = 0 },
            new() { Pattern = "git log*", Action = PermissionAction.Allow, Priority = 0 }
        ],
        DefaultAction = PermissionAction.Ask
    };
}
