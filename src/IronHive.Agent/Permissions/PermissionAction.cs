namespace IronHive.Agent.Permissions;

/// <summary>
/// Actions that can be taken for a permission rule.
/// </summary>
public enum PermissionAction
{
    /// <summary>
    /// Allow the operation without prompting.
    /// </summary>
    Allow,

    /// <summary>
    /// Deny the operation outright.
    /// </summary>
    Deny,

    /// <summary>
    /// Ask the user for approval (HITL).
    /// </summary>
    Ask
}
