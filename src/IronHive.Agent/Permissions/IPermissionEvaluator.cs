namespace IronHive.Agent.Permissions;

/// <summary>
/// Result of a permission evaluation.
/// </summary>
public record PermissionResult
{
    /// <summary>
    /// The action to take.
    /// </summary>
    public required PermissionAction Action { get; init; }

    /// <summary>
    /// The rule that matched (if any).
    /// </summary>
    public PermissionRule? MatchedRule { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Creates an Allow result.
    /// </summary>
    public static PermissionResult Allow(PermissionRule? rule = null) =>
        new() { Action = PermissionAction.Allow, MatchedRule = rule, Reason = rule?.Reason };

    /// <summary>
    /// Creates a Deny result.
    /// </summary>
    public static PermissionResult Deny(string? reason = null, PermissionRule? rule = null) =>
        new() { Action = PermissionAction.Deny, MatchedRule = rule, Reason = reason ?? rule?.Reason };

    /// <summary>
    /// Creates an Ask result.
    /// </summary>
    public static PermissionResult Ask(string? reason = null, PermissionRule? rule = null) =>
        new() { Action = PermissionAction.Ask, MatchedRule = rule, Reason = reason ?? rule?.Reason };
}

/// <summary>
/// Evaluates permissions for tool operations based on configured rules.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>
    /// Evaluates permission for a file read operation.
    /// </summary>
    /// <param name="filePath">Path to the file being read.</param>
    /// <returns>Permission result.</returns>
    PermissionResult EvaluateRead(string filePath);

    /// <summary>
    /// Evaluates permission for a file edit/write operation.
    /// </summary>
    /// <param name="filePath">Path to the file being edited.</param>
    /// <returns>Permission result.</returns>
    PermissionResult EvaluateEdit(string filePath);

    /// <summary>
    /// Evaluates permission for a bash/shell command.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>Permission result.</returns>
    PermissionResult EvaluateBash(string command);

    /// <summary>
    /// Evaluates permission for accessing an external directory.
    /// </summary>
    /// <param name="directoryPath">Path to the directory.</param>
    /// <returns>Permission result.</returns>
    PermissionResult EvaluateExternalDirectory(string directoryPath);

    /// <summary>
    /// Evaluates permission for an MCP tool call.
    /// </summary>
    /// <param name="toolName">Name of the MCP tool.</param>
    /// <returns>Permission result.</returns>
    PermissionResult EvaluateMcpTool(string toolName);

    /// <summary>
    /// Evaluates permission for a generic tool operation.
    /// </summary>
    /// <param name="permissionType">Type of permission (read, edit, bash, etc.).</param>
    /// <param name="target">The target to evaluate (file path, command, etc.).</param>
    /// <returns>Permission result.</returns>
    PermissionResult Evaluate(string permissionType, string target);
}
