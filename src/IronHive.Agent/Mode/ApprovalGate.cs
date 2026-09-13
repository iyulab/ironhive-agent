using IronHive.Agent.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Mode;

/// <summary>
/// The one place a tool call is judged before it runs. Both invocation paths the library offers —
/// <see cref="ApprovalGatedFunctionInvoker"/> for a Microsoft.Extensions.AI function-invoking client,
/// and the Ironbees adapter's own loop — defer to this so a permission verdict means the same thing
/// on either path.
/// </summary>
internal sealed partial class ApprovalGate
{
    private readonly IModeToolFilter _filter;
    private readonly IHumanApprovalService? _approval;
    private readonly ILogger _logger;

    public ApprovalGate(IModeToolFilter filter, IHumanApprovalService? approval, ILogger? logger = null)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _approval = approval;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Decides whether <paramref name="toolName"/> may run with <paramref name="arguments"/>.
    /// Never throws for a refusal: the refusal is a result the model reads, so it can change course.
    /// </summary>
    public async ValueTask<GateDecision> DecideAsync(
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var risk = _filter.AssessRisk(toolName, arguments);

        switch (risk.Verdict)
        {
            case PermissionAction.Allow:
                return GateDecision.Proceed(null);

            case PermissionAction.Deny:
                LogDenied(_logger, toolName, risk.Reason ?? "not allowed");
                return GateDecision.Refuse($"Permission denied: {risk.Reason ?? "Tool execution not allowed"}");

            case PermissionAction.Ask:
                if (_approval is null)
                {
                    // An Ask verdict with nobody to ask is a refusal, not a pass: letting the call through
                    // here is exactly the silent no-op the gate exists to remove.
                    LogNoApprovalService(_logger, toolName, risk.Reason ?? "approval required");
                    return GateDecision.Refuse(
                        $"Approval required but no approval service is configured: {risk.Reason ?? toolName}");
                }

                var result = await _approval.RequestApprovalAsync(new ApprovalRequest
                {
                    ToolName = toolName,
                    Arguments = arguments,
                    RiskAssessment = risk,
                    Description = risk.ApprovalPrompt ?? risk.Reason
                }, cancellationToken);

                if (!result.Approved)
                {
                    LogRejected(_logger, toolName, result.RejectionReason ?? "declined");
                    return GateDecision.Refuse($"Approval rejected: {result.RejectionReason ?? "the operator declined"}");
                }

                return GateDecision.Proceed(result.ModifiedArguments);

            default:
                return GateDecision.Refuse($"Permission denied: unknown verdict {risk.Verdict} for tool {toolName}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {ToolName} denied by permission rules: {Reason}")]
    private static partial void LogDenied(ILogger logger, string toolName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {ToolName} requires approval but no IHumanApprovalService is configured; refusing: {Reason}")]
    private static partial void LogNoApprovalService(ILogger logger, string toolName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool {ToolName} rejected by the operator: {Reason}")]
    private static partial void LogRejected(ILogger logger, string toolName, string reason);
}

/// <summary>
/// Outcome of <see cref="ApprovalGate.DecideAsync"/>: either run (optionally with arguments the
/// approver edited) or return <see cref="Refusal"/> to the model as the tool's result.
/// </summary>
internal readonly record struct GateDecision(bool ShouldProceed, string? Refusal, IDictionary<string, object?>? ModifiedArguments)
{
    public static GateDecision Proceed(IDictionary<string, object?>? modifiedArguments) => new(true, null, modifiedArguments);
    public static GateDecision Refuse(string refusal) => new(false, refusal, null);
}
