using System.ComponentModel;
using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mode;

/// <summary>
/// The tool a model uses to tell the person who approves a tool call what it weighed: the alternatives, the one it
/// recommends, or the one question it would ask. What it writes reaches the approver as a <see cref="ModelAuthoredProposal"/>
/// (<see cref="ApprovalRequest.ModelProposal"/>), marked as the model's own and apart from the verified call.
/// </summary>
/// <remarks>
/// <para>
/// Off until registered: add the tool to the loop's <c>Tools</c>. Nothing else is needed - the approval gate already reads
/// the proposal from the step in which the model called both (<see cref="ToolCallProposalSource"/>); a loop without the tool
/// never has one. The name is reserved: a call to a tool of this name is a proposal, whoever declared it.
/// </para>
/// <para>
/// A model cannot know which of its calls the policy asks about, so say it in your instructions. Measured against a real
/// model, it proposed for every call when the instructions named the tool («every WriteFile call needs the user's approval;
/// in the same step as each, also call propose_for_approval naming the tool»), and for none when they only said approval
/// may be needed, or nothing. The order of the two calls within the step does not matter - a model that writes first and
/// proposes after is read the same.
/// </para>
/// <para>
/// The tool approves nothing and changes no permission. Approval rules see it as read-only (like <c>advisor</c>), so the
/// default policy and planning mode allow it. A proposal reaches an approver only for a call that needs approval and only
/// when the model made it in the same step as that call and named that call's tool - the tool says so to the model, and its
/// result says plainly whether the text was kept, never that anyone saw it.
/// </para>
/// </remarks>
public static class ApprovalProposalTool
{
    /// <summary>The tool's name; a call to it is read as a proposal.</summary>
    public const string ToolName = "propose_for_approval";

    internal const string Description =
        "Tell the person who approves a tool call what you weighed. Call this in the same step as the call it is about - the call " +
        "that needs approval, before or after it - and name that call's tool in 'tool'. List up to 5 alternatives (id, short label, why), say which " +
        "one you recommend with recommendedOptionId, or, when you cannot recommend one, ask the one question that would settle it. " +
        "The person sees this as your own proposal, apart from the call and its arguments. It approves nothing and changes no " +
        "permission: the person still decides. Keep the text short and plain - no markup or links.";

    /// <summary>Creates the tool (declared read-only: a call changes nothing).</summary>
    public static AITool Create()
    {
        string Propose(
            [Description("The name of the tool call, made in this same step, that this proposal is about.")] string tool,
            [Description("The alternatives you weighed, at most 5, each with a short id, a short label and why you weighed it.")] ProposalOption[]? options = null,
            [Description("The id of the option you recommend; leave out when you cannot recommend one.")] string? recommendedOptionId = null,
            [Description("When you cannot recommend one, the one question that would settle it.")] string? question = null)
            => Answer(tool, options, recommendedOptionId, question);

        return AIFunctionFactory.Create(Propose, new AIFunctionFactoryOptions { Name = ToolName, Description = Description })
            .WithReadOnly(true);
    }

    internal static string Answer(string? tool, IEnumerable<ProposalOption>? options, string? recommendedOptionId, string? question)
    {
        var named = ModelAuthoredProposal.Clean(tool, ModelAuthoredProposal.MaxOptionIdLength * 2);
        if (named.Length == 0)
        {
            return "Not recorded: name the tool call this proposal is about in 'tool'.";
        }

        if (!ModelAuthoredProposal.TryBuild(options, recommendedOptionId, question, out _, out var rejection))
        {
            return $"Not recorded: {rejection}. Call again with a valid proposal.";
        }

        return $"Recorded as your proposal for the call to '{named}' in this step. If that call needs approval, the person sees it apart " +
               "from the call, marked as yours. It approves nothing.";
    }
}
