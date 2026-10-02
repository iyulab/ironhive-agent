using IronHive.Agent.Permissions;

namespace IronHive.Agent.Mode;

/// <summary>
/// Judges one tool call before it runs: <c>Allow</c>, <c>Deny</c> or <c>Ask</c>. The one place the verdict comes from —
/// <see cref="ApprovalGateMiddleware"/> acts on it for every call of the tool invocation pipeline.
/// </summary>
/// <remarks>
/// The default is <see cref="ToolCallPolicy"/> over the container's <see cref="IPermissionEvaluator"/>. A host with its own
/// rules (a category table, a trust tier) registers its own implementation in its place; the gate's other duties —
/// enforcing Planning mode, asking the approver on <c>Ask</c>, refusing an <c>Ask</c> nobody can answer — stay the gate's.
/// </remarks>
public interface IToolCallPolicy
{
    /// <summary>Judges a call of <paramref name="toolName"/> with <paramref name="arguments"/>.</summary>
    /// <param name="toolName">The tool's function name.</param>
    /// <param name="arguments">The call's arguments; may be null.</param>
    /// <returns>The verdict, with its risk level, reason and approval prompt.</returns>
    RiskAssessment Evaluate(string toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Result of judging a tool call.
/// </summary>
public record RiskAssessment
{
    /// <summary>
    /// Whether the operation is considered risky.
    /// </summary>
    public bool IsRisky { get; init; }

    /// <summary>
    /// What the permission rules decided: <see cref="PermissionAction.Allow"/> runs the tool,
    /// <see cref="PermissionAction.Deny"/> refuses it, <see cref="PermissionAction.Ask"/> defers to a
    /// human. Explicit, so a gate never has to infer "deny" from a missing approval prompt.
    /// </summary>
    public PermissionAction Verdict { get; init; }

    /// <summary>
    /// True when the verdict is <see cref="PermissionAction.Ask"/>.
    /// </summary>
    public bool RequiresApproval => Verdict == PermissionAction.Ask;

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
    public static RiskAssessment Safe => new() { IsRisky = false, Level = RiskLevel.Low, Verdict = PermissionAction.Allow };

    /// <summary>
    /// Creates a risky assessment. The verdict defaults to <see cref="PermissionAction.Ask"/>; pass
    /// <see cref="PermissionAction.Deny"/> for an operation that must not run even with a human present.
    /// </summary>
    public static RiskAssessment Risky(RiskLevel level, string reason, string? approvalPrompt = null, PermissionAction verdict = PermissionAction.Ask) =>
        new() { IsRisky = true, Level = level, Reason = reason, ApprovalPrompt = approvalPrompt, Verdict = verdict };
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
/// The default <see cref="IToolCallPolicy"/>: judges file, shell and MCP tools on their arguments and every other tool by
/// name, all through an <see cref="IPermissionEvaluator"/> (so through one <see cref="PermissionConfig"/>).
/// </summary>
/// <remarks>
/// File reads and edits answer to the <c>Read</c>/<c>Edit</c> path rules, deletes to <see cref="IPermissionEvaluator.EvaluateDelete"/>
/// (the <c>Edit</c> rules, asked about when <see cref="PermissionConfig.AskBeforeDelete"/>), shell commands to
/// <see cref="IPermissionEvaluator.EvaluateBash"/>, <c>mcp__*</c> tools to <c>McpTools</c>, and any other tool to <c>Tools</c>,
/// falling to <see cref="PermissionConfig.DefaultAction"/>.
/// </remarks>
public sealed class ToolCallPolicy : IToolCallPolicy
{
    private readonly IPermissionEvaluator _evaluator;

    /// <summary>Creates the policy over <see cref="PermissionConfig.CreateDefault"/>.</summary>
    public ToolCallPolicy() : this(new PermissionEvaluator())
    {
    }

    /// <summary>Creates the policy over <paramref name="permissionConfig"/>.</summary>
    public ToolCallPolicy(PermissionConfig permissionConfig) : this(new PermissionEvaluator(permissionConfig))
    {
    }

    /// <summary>Creates the policy over <paramref name="evaluator"/>.</summary>
    public ToolCallPolicy(IPermissionEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <inheritdoc />
    public RiskAssessment Evaluate(string toolName, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return BuiltInToolNames.Normalize(toolName) switch
        {
            "read_file" => AssessRead(arguments),
            "write_file" or "edit_file" => AssessWrite(arguments),
            "delete_file" => AssessDelete(arguments),
            "move_file" => AssessMove(arguments),
            "glob_files" or "grep_files" or "list_directory" => AssessDirectoryRead(toolName, arguments),
            "shell" or "execute_command" => AssessShell(arguments),
            _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => ToRiskAssessment(_evaluator.EvaluateMcpTool(toolName), $"MCP tool: {toolName}"),
            _ => AssessTool(toolName)
        };
    }

    // A tool with no dedicated category is judged by name against PermissionConfig.Tools. Anything
    // unmatched there falls to the config's default action — so "ask about tools I don't know" is
    // what the default Ask actually means, instead of every unlisted tool being silently safe.
    private RiskAssessment AssessTool(string toolName) =>
        ToRiskAssessment(_evaluator.EvaluateTool(toolName), $"Tool: {toolName}");

    private RiskAssessment AssessRead(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Safe;
        }

        return ToRiskAssessment(_evaluator.EvaluateRead(path), $"Read file: {TruncatePath(path)}");
    }

    // The directory tools read too: a Read rule that closes a directory has to close it for a search
    // inside it, not only for ReadFile. They keep answering to their tool-name rule as before, and
    // the directory they are pointed at additionally answers to the Read rules - but only where a
    // Read rule actually speaks about that directory (or it lies outside the working directory):
    // Read patterns are written for files, and "no file pattern matches this directory name" is not
    // a verdict on the directory.
    private RiskAssessment AssessDirectoryRead(string toolName, IDictionary<string, object?>? arguments)
    {
        var byName = AssessTool(toolName);
        if (byName.Verdict == PermissionAction.Deny)
        {
            return byName;
        }

        var path = GetStringArgument(arguments, "path");
        var byPath = _evaluator.EvaluateRead(string.IsNullOrEmpty(path) ? "." : path);
        if (byPath.Action == PermissionAction.Allow
            || (byPath.MatchedRule is null && !byPath.OutsideWorkingDirectory))
        {
            return byName;
        }

        var assessment = ToRiskAssessment(byPath, $"Search directory: {TruncatePath(path ?? ".")}");
        return byName.IsRisky && assessment.Verdict != PermissionAction.Deny ? byName : assessment;
    }

    private RiskAssessment AssessWrite(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Risky(RiskLevel.Medium, "Write operation with unknown path");
        }

        return ToRiskAssessment(_evaluator.EvaluateEdit(path), $"Write file: {TruncatePath(path)}");
    }

    private RiskAssessment AssessDelete(IDictionary<string, object?>? arguments)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
        {
            return RiskAssessment.Risky(RiskLevel.High, "Delete operation with unknown path");
        }

        var result = _evaluator.EvaluateDelete(path);

        // Asked only because AskBeforeDelete escalated an edit the rules allow: a confirmation, not a red flag.
        if (result.Action == PermissionAction.Ask && result.MatchedRule?.Action == PermissionAction.Allow)
        {
            return RiskAssessment.Risky(RiskLevel.Medium, $"File deletion: {TruncatePath(path)}", "Allow deleting this file?");
        }

        return ToRiskAssessment(result, $"Delete file: {TruncatePath(path)}", RiskLevel.High);
    }

    // A move deletes its source and writes its destination: each answers to its own rules, and the stricter verdict wins.
    private RiskAssessment AssessMove(IDictionary<string, object?>? arguments)
    {
        var source = GetStringArgument(arguments, "source");
        var destination = GetStringArgument(arguments, "destination");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
        {
            return RiskAssessment.Risky(RiskLevel.High, "Move operation with unknown path");
        }

        var removed = AssessDelete(new Dictionary<string, object?> { ["path"] = source });
        var written = AssessWrite(new Dictionary<string, object?> { ["path"] = destination });
        return Stricter(removed, written);
    }

    private static RiskAssessment Stricter(RiskAssessment a, RiskAssessment b)
    {
        static int Weight(RiskAssessment r) => r.Verdict switch
        {
            PermissionAction.Deny => 2,
            _ when r.IsRisky => 1,
            _ => 0,
        };

        var (wa, wb) = (Weight(a), Weight(b));
        return wa != wb ? (wa > wb ? a : b) : (a.Level >= b.Level ? a : b);
    }

    private RiskAssessment AssessShell(IDictionary<string, object?>? arguments)
    {
        var command = GetStringArgument(arguments, "command");
        if (string.IsNullOrEmpty(command))
        {
            // Nothing to evaluate and nothing that could run: refuse outright rather than ask.
            return RiskAssessment.Risky(RiskLevel.High, "Shell command with no command specified", verdict: PermissionAction.Deny);
        }

        var result = _evaluator.EvaluateBash(command);
        return result.Action switch
        {
            PermissionAction.Allow => RiskAssessment.Safe,
            PermissionAction.Deny => RiskAssessment.Risky(
                RiskLevel.Critical,
                result.Reason ?? $"Denied command: {TruncateCommand(command)}",
                null,
                PermissionAction.Deny),
            PermissionAction.Ask => RiskAssessment.Risky(
                ShellCommandRisk.Classify(command),
                result.Reason ?? $"Shell command: {TruncateCommand(command)}",
                "Allow executing this command?"),
            _ => RiskAssessment.Safe
        };
    }

    private static RiskAssessment ToRiskAssessment(PermissionResult result, string context, RiskLevel? overrideLevel = null) =>
        result.Action switch
        {
            PermissionAction.Allow => RiskAssessment.Safe,
            PermissionAction.Deny => RiskAssessment.Risky(
                overrideLevel ?? RiskLevel.Critical,
                result.Reason ?? $"Denied: {context}",
                null,
                PermissionAction.Deny),
            PermissionAction.Ask => RiskAssessment.Risky(
                overrideLevel ?? RiskLevel.Medium,
                result.Reason ?? context,
                "Allow this operation?"),
            _ => RiskAssessment.Safe
        };

    private static string? GetStringArgument(IDictionary<string, object?>? arguments, string key) =>
        arguments is not null && arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string TruncatePath(string path)
    {
        const int maxLength = 60;
        return path.Length > maxLength ? "..." + path[^(maxLength - 3)..] : path;
    }

    private static string TruncateCommand(string command)
    {
        const int maxLength = 50;
        return command.Length > maxLength ? command[..maxLength] + "..." : command;
    }
}
