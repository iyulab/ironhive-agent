using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mode;

/// <summary>
/// Where the <see cref="ModelAuthoredProposal"/> of an approval request comes from. Consulted by
/// <see cref="ApprovalGateMiddleware"/> only for a call whose verdict is <c>Ask</c>, after the policy has decided -
/// a proposal never changes a verdict.
/// </summary>
/// <remarks>
/// With none registered the gate uses <see cref="ToolCallProposalSource"/>, which finds a proposal only when the model made
/// one with <see cref="ApprovalProposalTool"/> - a loop that does not offer that tool carries none, exactly as before.
/// Register your own to take the proposal from another place (a structured-output step, say). A source reads the call's
/// <see cref="FunctionInvocationContext"/> (its conversation holds the assistant message with the call and every other call
/// the model made in the same batch) and returns what the model wrote for it, or null. Its output is the model's claim:
/// build it with <see cref="ModelAuthoredProposal.TryCreate"/> so the bounds hold. A source that throws is logged and the
/// approval is asked without a proposal.
/// </remarks>
public interface IApprovalProposalSource
{
    /// <summary>The proposal the model made for the call in <paramref name="context"/>, or null when it made none.</summary>
    /// <param name="context">The call about to be approved, and the conversation it belongs to.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<ModelAuthoredProposal?> GetProposalAsync(FunctionInvocationContext context, CancellationToken cancellationToken = default);
}
