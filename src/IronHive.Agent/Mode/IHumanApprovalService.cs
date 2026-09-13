namespace IronHive.Agent.Mode;

/// <summary>
/// Service for requesting human approval for risky operations.
/// </summary>
/// <remarks>
/// Consulted by <see cref="ApprovalGatedFunctionInvoker"/> — installed as the <c>FunctionInvoker</c> of
/// Microsoft.Extensions.AI's <c>UseFunctionInvocation()</c> middleware — and by the Ironbees adapter,
/// whenever <see cref="IModeToolFilter.AssessRisk"/> returns an <c>Ask</c> verdict for a call. An
/// <c>IAgentLoop</c> itself never invokes tools, so registering an implementation is not enough on its
/// own: the consumer's chat client must carry the gated invoker. Remembering an
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
