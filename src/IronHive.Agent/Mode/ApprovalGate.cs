using IronHive.Agent.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IronHive.Agent.Mode;

/// <summary>
/// The one place a tool call is judged before it runs. <see cref="ApprovalGateMiddleware"/> applies it in the tool
/// invocation pipeline that every path running tools goes through, so a permission verdict means the same thing on
/// each. Public so a host's own tool loop (another framework's adapter) can judge calls by the same rule.
/// </summary>
/// <remarks>
/// Order per call: Planning mode first (when a mode manager is given — a tool the mode does not permit is denied whatever
/// the policy says), then the <see cref="IToolCallPolicy"/> verdict, then, for <c>Ask</c>, the approver.
/// </remarks>
public sealed partial class ApprovalGate
{
    private readonly IToolCallPolicy _policy;
    private readonly IHumanApprovalService? _approval;
    private readonly IModeManager? _modes;
    private readonly IModeToolFilter? _modeFilter;
    private readonly ILogger _logger;

    /// <summary>Creates a gate over a tool-call policy and, for <c>Ask</c> verdicts, an approver.</summary>
    /// <param name="policy">Judges each call.</param>
    /// <param name="approval">Asked when the verdict is <c>Ask</c>; without one such a call is refused.</param>
    /// <param name="logger">Receives a line per refusal; optional.</param>
    /// <param name="modeManager">When given, a call the current mode does not permit is denied — only
    /// <see cref="AgentMode.Planning"/> is enforced (Idle and HumanInTheLoop are interaction states, not verdicts).</param>
    /// <param name="modeToolFilter">Says which tools a mode permits; required with <paramref name="modeManager"/>.</param>
    public ApprovalGate(
        IToolCallPolicy policy,
        IHumanApprovalService? approval = null,
        ILogger? logger = null,
        IModeManager? modeManager = null,
        IModeToolFilter? modeToolFilter = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (modeManager is not null && modeToolFilter is null)
        {
            throw new ArgumentException(
                "A mode manager needs the mode tool filter that says which tools a mode permits.", nameof(modeToolFilter));
        }

        _approval = approval;
        _modes = modeManager;
        _modeFilter = modeToolFilter;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Decides whether <paramref name="toolName"/> may run with <paramref name="arguments"/>.
    /// Never throws for a refusal: the refusal is a result the model reads, so it can change course.
    /// </summary>
    public ValueTask<GateDecision> DecideAsync(
        string toolName,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken) =>
        DecideAsync(toolName, arguments, callId: null, cancellationToken);

    /// <summary>
    /// Decides whether <paramref name="toolName"/> may run with <paramref name="arguments"/>; <paramref name="callId"/> is
    /// passed to the approver (<see cref="ApprovalRequest.CallId"/>) so a request can be paired with the call it is about.
    /// Never throws for a refusal: the refusal is a result the model reads, so it can change course.
    /// </summary>
    public async ValueTask<GateDecision> DecideAsync(
        string toolName,
        IDictionary<string, object?>? arguments,
        string? callId,
        CancellationToken cancellationToken)
    {
        if (_modes is { CurrentMode: AgentMode.Planning } && !_modeFilter!.IsToolPermitted(toolName, AgentMode.Planning))
        {
            const string reason = "Planning mode permits read-only tools only";
            LogDenied(_logger, toolName);
            return GateDecision.Refuse(new ToolCallRefusal(ToolCallRefusalKind.Denied, $"{reason}: {toolName}"));
        }

        var risk = _policy.Evaluate(toolName, arguments);

        switch (risk.Verdict)
        {
            case PermissionAction.Allow:
                return GateDecision.Proceed(null);

            case PermissionAction.Deny:
                LogDenied(_logger, toolName);
                return GateDecision.Refuse(new ToolCallRefusal(ToolCallRefusalKind.Denied, risk.Reason ?? "Tool execution not allowed"));

            case PermissionAction.Ask:
                if (_approval is null)
                {
                    // An Ask verdict with nobody to ask is a refusal, not a pass: letting the call through
                    // here is exactly the silent no-op the gate exists to remove.
                    LogNoApprovalService(_logger, toolName);
                    return GateDecision.Refuse(new ToolCallRefusal(ToolCallRefusalKind.ApprovalUnavailable, risk.Reason ?? toolName));
                }

                var result = await _approval.RequestApprovalAsync(new ApprovalRequest
                {
                    ToolName = toolName,
                    Arguments = arguments,
                    RiskAssessment = risk,
                    Description = risk.ApprovalPrompt ?? risk.Reason,
                    CallId = callId
                }, cancellationToken);

                if (!result.Approved)
                {
                    var hasReason = !string.IsNullOrWhiteSpace(result.RejectionReason);
                    LogRejected(_logger, toolName, hasReason);
                    return GateDecision.Refuse(new ToolCallRefusal(ToolCallRefusalKind.Rejected, result.RejectionReason ?? "the operator declined"));
                }

                return GateDecision.Proceed(result.ModifiedArguments);

            default:
                return GateDecision.Refuse(new ToolCallRefusal(ToolCallRefusalKind.Denied, $"unknown verdict {risk.Verdict} for tool {toolName}"));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {ToolName} denied by permission rules")]
    private static partial void LogDenied(ILogger logger, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {ToolName} requires approval but no IHumanApprovalService is configured; refusing")]
    private static partial void LogNoApprovalService(ILogger logger, string toolName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool {ToolName} rejected by the operator (reason given: {HasReason})")]
    private static partial void LogRejected(ILogger logger, string toolName, bool hasReason);
}

/// <summary>What <see cref="ApprovalGate.DecideAsync(string, IDictionary{string, object?}?, CancellationToken)"/> decided about one tool call.</summary>
/// <param name="ShouldProceed">Run the call.</param>
/// <param name="Refusal">When not proceeding: the result the model reads instead of the tool's.</param>
/// <param name="ModifiedArguments">When proceeding: arguments the approver changed, to run with instead; null to keep the call's own.</param>
public readonly record struct GateDecision(bool ShouldProceed, ToolCallRefusal? Refusal, IDictionary<string, object?>? ModifiedArguments)
{
    /// <summary>Run the call, with <paramref name="modifiedArguments"/> when the approver changed them.</summary>
    public static GateDecision Proceed(IDictionary<string, object?>? modifiedArguments) => new(true, null, modifiedArguments);

    /// <summary>Do not run the call; the model reads <paramref name="refusal"/>.</summary>
    public static GateDecision Refuse(ToolCallRefusal refusal) => new(false, refusal, null);
}
