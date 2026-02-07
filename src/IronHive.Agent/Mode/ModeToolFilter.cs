using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mode;

/// <summary>
/// Filters available tools based on the current agent mode.
/// </summary>
public interface IModeToolFilter
{
    /// <summary>
    /// Filters tools based on the current mode.
    /// </summary>
    /// <param name="tools">All available tools</param>
    /// <param name="mode">Current agent mode</param>
    /// <returns>Tools permitted in the current mode</returns>
    IList<AITool> FilterTools(IList<AITool> tools, AgentMode mode);

    /// <summary>
    /// Checks if a tool is permitted in the current mode.
    /// </summary>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="mode">Current agent mode</param>
    /// <returns>True if the tool is permitted</returns>
    bool IsToolPermitted(string toolName, AgentMode mode);

    /// <summary>
    /// Checks if a tool operation is considered risky and requires HITL approval.
    /// </summary>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="arguments">Tool arguments</param>
    /// <returns>Risk assessment result</returns>
    RiskAssessment AssessRisk(string toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Result of risk assessment for a tool operation.
/// </summary>
public record RiskAssessment
{
    /// <summary>
    /// Whether the operation is considered risky.
    /// </summary>
    public bool IsRisky { get; init; }

    /// <summary>
    /// Risk level (Low, Medium, High, Critical).
    /// </summary>
    public RiskLevel Level { get; init; }

    /// <summary>
    /// Human-readable description of why this is risky.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Suggested user prompt for HITL approval.
    /// </summary>
    public string? ApprovalPrompt { get; init; }

    /// <summary>
    /// Creates a non-risky assessment.
    /// </summary>
    public static RiskAssessment Safe => new() { IsRisky = false, Level = RiskLevel.Low };

    /// <summary>
    /// Creates a risky assessment.
    /// </summary>
    public static RiskAssessment Risky(RiskLevel level, string reason, string? approvalPrompt = null) =>
        new() { IsRisky = true, Level = level, Reason = reason, ApprovalPrompt = approvalPrompt };
}

/// <summary>
/// Risk levels for tool operations.
/// </summary>
public enum RiskLevel
{
    /// <summary>
    /// Low risk - safe operations.
    /// </summary>
    Low,

    /// <summary>
    /// Medium risk - may need review.
    /// </summary>
    Medium,

    /// <summary>
    /// High risk - requires explicit approval.
    /// </summary>
    High,

    /// <summary>
    /// Critical risk - destructive operations.
    /// </summary>
    Critical
}

/// <summary>
/// Default implementation of mode tool filter using permission evaluator.
/// </summary>
public class ModeToolFilter : IModeToolFilter
{
    private readonly IPermissionEvaluator _permissionEvaluator;

    // Read-only tools allowed in Planning mode (both PascalCase and snake_case for compatibility)
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFile", "read_file",
        "GlobFiles", "glob",
        "GrepFiles", "grep",
        "ListDirectory", "list_directory"
    };

    /// <summary>
    /// Creates a new ModeToolFilter with default configuration.
    /// </summary>
    public ModeToolFilter() : this(new PermissionEvaluator())
    {
    }

    /// <summary>
    /// Creates a new ModeToolFilter with the specified permission evaluator.
    /// </summary>
    public ModeToolFilter(IPermissionEvaluator permissionEvaluator)
    {
        _permissionEvaluator = permissionEvaluator ?? throw new ArgumentNullException(nameof(permissionEvaluator));
    }

    /// <summary>
    /// Creates a new ModeToolFilter with the specified permission configuration.
    /// </summary>
    public ModeToolFilter(PermissionConfig permissionConfig)
        : this(new PermissionEvaluator(permissionConfig))
    {
    }

    /// <inheritdoc />
    public IList<AITool> FilterTools(IList<AITool> tools, AgentMode mode)
    {
        return mode switch
        {
            AgentMode.Idle => [], // No tools in idle
            AgentMode.Planning => tools.Where(t => IsToolPermitted(GetToolName(t), mode)).ToList(),
            AgentMode.Working => tools.ToList(), // All tools in working mode
            AgentMode.HumanInTheLoop => [], // No tools while waiting for approval
            _ => []
        };
    }

    /// <inheritdoc />
    public bool IsToolPermitted(string toolName, AgentMode mode)
    {
        return mode switch
        {
            AgentMode.Idle => false,
            AgentMode.Planning => ReadOnlyTools.Contains(toolName),
            AgentMode.Working => true, // All tools permitted (but may trigger HITL)
            AgentMode.HumanInTheLoop => false,
            _ => false
        };
    }

    /// <inheritdoc />
    public RiskAssessment AssessRisk(string toolName, IDictionary<string, object?>? arguments)
    {
        return NormalizeToolName(toolName) switch
        {
            "read_file" => AssessReadRisk(arguments),
            "write_file" => AssessWriteRisk(arguments),
            "delete_file" => AssessDeleteRisk(arguments),
            "shell" or "execute_command" => AssessShellRisk(arguments),
            _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => AssessMcpToolRisk(toolName),
            _ => RiskAssessment.Safe
        };
    }

    // Maps PascalCase tool names to snake_case for consistent risk assessment
    private static string NormalizeToolName(string toolName)
    {
        return toolName switch
        {
            "ReadFile" => "read_file",
            "WriteFile" => "write_file",
            "DeleteFile" => "delete_file",
            "ExecuteCommand" => "execute_command",
            "GlobFiles" => "glob_files",
            "GrepFiles" => "grep_files",
            "ListDirectory" => "list_directory",
            _ => toolName
        };
    }

    private RiskAssessment AssessReadRisk(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Safe;
        }

        var result = _permissionEvaluator.EvaluateRead(path);
        return ToRiskAssessment(result, $"Read file: {TruncatePath(path)}");
    }

    private RiskAssessment AssessWriteRisk(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Risky(RiskLevel.Medium, "Write operation with unknown path");
        }

        var result = _permissionEvaluator.EvaluateEdit(path);
        return ToRiskAssessment(result, $"Write file: {TruncatePath(path)}");
    }

    private RiskAssessment AssessDeleteRisk(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Risky(RiskLevel.High, "Delete operation with unknown path");
        }

        // Delete is always at least medium risk
        var result = _permissionEvaluator.EvaluateEdit(path);
        if (result.Action == PermissionAction.Allow)
        {
            // Even if edit is allowed, delete gets a warning
            return RiskAssessment.Risky(
                RiskLevel.Medium,
                $"File deletion: {TruncatePath(path)}",
                "Allow deleting this file?");
        }

        return ToRiskAssessment(result, $"Delete file: {TruncatePath(path)}", RiskLevel.High);
    }

    private RiskAssessment AssessShellRisk(IDictionary<string, object?>? arguments)
    {
        var command = GetStringArgument(arguments, "command");
        if (string.IsNullOrEmpty(command))
        {
            return RiskAssessment.Risky(RiskLevel.High, "Shell command with no command specified");
        }

        var result = _permissionEvaluator.EvaluateBash(command);

        // Map permission result to risk assessment with appropriate level
        return result.Action switch
        {
            PermissionAction.Allow => RiskAssessment.Safe,
            PermissionAction.Deny => RiskAssessment.Risky(
                RiskLevel.Critical,
                result.Reason ?? $"Denied command: {TruncateCommand(command)}",
                null), // No approval prompt for denied commands
            PermissionAction.Ask => RiskAssessment.Risky(
                DetermineShellRiskLevel(command),
                result.Reason ?? $"Shell command: {TruncateCommand(command)}",
                "Allow executing this command?"),
            _ => RiskAssessment.Safe
        };
    }

    private RiskAssessment AssessMcpToolRisk(string toolName)
    {
        var result = _permissionEvaluator.EvaluateMcpTool(toolName);
        return ToRiskAssessment(result, $"MCP tool: {toolName}");
    }

    private static RiskAssessment ToRiskAssessment(
        PermissionResult result,
        string context,
        RiskLevel? overrideLevel = null)
    {
        return result.Action switch
        {
            PermissionAction.Allow => RiskAssessment.Safe,
            PermissionAction.Deny => RiskAssessment.Risky(
                overrideLevel ?? RiskLevel.Critical,
                result.Reason ?? $"Denied: {context}",
                null),
            PermissionAction.Ask => RiskAssessment.Risky(
                overrideLevel ?? RiskLevel.Medium,
                result.Reason ?? context,
                $"Allow this operation?"),
            _ => RiskAssessment.Safe
        };
    }

    private static RiskLevel DetermineShellRiskLevel(string command)
    {
        var lowerCommand = command.ToLowerInvariant();

        // Critical risk patterns
        if (lowerCommand.Contains("rm -rf") ||
            lowerCommand.Contains("del /s /q") ||
            lowerCommand.Contains("format ") ||
            lowerCommand.Contains("fdisk") ||
            lowerCommand.Contains("mkfs") ||
            lowerCommand.Contains(":(){:|:&};:") ||
            lowerCommand.Contains("> /dev/sda") ||
            lowerCommand.Contains("dd if="))
        {
            return RiskLevel.Critical;
        }

        // High risk patterns
        if (lowerCommand.StartsWith("sudo ", StringComparison.Ordinal) ||
            lowerCommand.StartsWith("runas ", StringComparison.Ordinal) ||
            lowerCommand.Contains("chmod 777") ||
            lowerCommand.Contains("curl") && lowerCommand.Contains("| sh") ||
            lowerCommand.Contains("wget") && lowerCommand.Contains("| sh"))
        {
            return RiskLevel.High;
        }

        // Medium risk for other shell commands
        return RiskLevel.Medium;
    }

    private static string GetToolName(AITool tool)
    {
        if (tool is AIFunction func)
        {
            return func.Name;
        }
        return tool.GetType().Name;
    }

    private static string? GetStringArgument(IDictionary<string, object?>? arguments, string key)
    {
        if (arguments == null)
        {
            return null;
        }

        if (arguments.TryGetValue(key, out var value))
        {
            return value?.ToString();
        }

        return null;
    }

    private static string TruncatePath(string path)
    {
        const int maxLength = 60;
        return path.Length > maxLength
            ? "..." + path[^(maxLength - 3)..]
            : path;
    }

    private static string TruncateCommand(string command)
    {
        const int maxLength = 50;
        return command.Length > maxLength
            ? command[..maxLength] + "..."
            : command;
    }
}
