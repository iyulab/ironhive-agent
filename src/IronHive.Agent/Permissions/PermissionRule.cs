namespace IronHive.Agent.Permissions;

/// <summary>
/// A permission rule with pattern matching and priority.
/// </summary>
public record PermissionRule
{
    /// <summary>
    /// Glob pattern to match (e.g., "*.env", "src/**/*", "git *").
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// Action to take when the pattern matches.
    /// </summary>
    public PermissionAction Action { get; init; } = PermissionAction.Ask;

    /// <summary>
    /// Priority of this rule. Higher values take precedence; among matching rules of one priority the stricter action wins
    /// (deny, then ask, then allow), whatever order they are listed in.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Optional reason or description for this rule.
    /// </summary>
    public string? Reason { get; init; }
}
