namespace IronHive.Agent.Mode;

/// <summary>
/// Service for requesting human approval for risky operations.
/// </summary>
/// <remarks>
/// Consulted by <see cref="ApprovalGateMiddleware"/> — a step of the tool invocation pipeline that a
/// function-invoking client (<c>UseToolInvocationPipeline()</c>) and the Ironbees adapter run every call through —
/// whenever the <see cref="IToolCallPolicy"/> returns an <c>Ask</c> verdict for a call. An
/// <c>IAgentLoop</c> itself never invokes tools, so registering an implementation is not enough on its
/// own: the pipeline must carry the gate (<c>services.AddIronHiveAgentApprovalGate()</c>). Remembering an
/// <see cref="ApprovalResult.AlwaysApprove"/> answer is the implementation's job — the gate asks every
/// time and does not keep its own list, since only the service knows what "this type of operation"
/// means for its users.
/// </remarks>
public interface IHumanApprovalService
{
    /// <summary>
    /// Requests approval for a risky operation.
    /// </summary>
    /// <param name="request">The approval request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The approval result</returns>
    Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Request for human approval.
/// </summary>
public record ApprovalRequest
{
    /// <summary>
    /// The tool name that requires approval.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Tool arguments (may be redacted for security).
    /// </summary>
    public IDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    /// Risk assessment for the operation.
    /// </summary>
    public required RiskAssessment RiskAssessment { get; init; }

    /// <summary>
    /// Human-readable description of what will happen.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The id of the tool call this request is about (the model's call id), when the caller knows it — lets an approver
    /// on a wire pair the request with the call's start/end events. Null when the gate is used outside a pipeline.
    /// </summary>
    public string? CallId { get; init; }
}

/// <summary>
/// Result of a human approval request.
/// </summary>
public record ApprovalResult
{
    /// <summary>
    /// Whether the operation was approved.
    /// </summary>
    public bool Approved { get; init; }

    /// <summary>
    /// Whether the user wants to always approve this type of operation.
    /// </summary>
    public bool AlwaysApprove { get; init; }

    /// <summary>
    /// Optional modified arguments (if user edited them).
    /// </summary>
    public IDictionary<string, object?>? ModifiedArguments { get; init; }

    /// <summary>
    /// Optional rejection reason.
    /// </summary>
    public string? RejectionReason { get; init; }

    /// <summary>
    /// Creates an approved result.
    /// </summary>
    public static ApprovalResult Approve(bool alwaysApprove = false) => new()
    {
        Approved = true,
        AlwaysApprove = alwaysApprove
    };

    /// <summary>
    /// Creates a rejected result.
    /// </summary>
    public static ApprovalResult Reject(string? reason = null) => new()
    {
        Approved = false,
        RejectionReason = reason
    };
}
