using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Mode;

/// <summary>
/// The built-in <see cref="IApprovalProposalSource"/>: reads the proposal a model made with <see cref="ApprovalProposalTool"/>
/// for the call being approved, from the same step - the assistant message that holds the call and every other call the
/// model made with it.
/// </summary>
/// <remarks>
/// <para>
/// A proposal belongs to a call only when it is unambiguous: the proposal's <c>tool</c> names the call's tool, the step holds
/// exactly one call to that tool, and exactly one valid proposal names it. Two calls to the same tool in one step, or two
/// proposals for one, tie to none - the source never guesses which call the model meant. A proposal that fails the shape of
/// <see cref="ModelAuthoredProposal"/> counts as not made. It reads the calls the model <i>made</i> (their arguments), not
/// their results, so a proposal is found whether it comes before or after the call it is about.
/// </para>
/// <para>
/// <see cref="ApprovalGateMiddleware"/> uses it when no other source is registered. It is stateless and safe to share.
/// </para>
/// </remarks>
public sealed class ToolCallProposalSource : IApprovalProposalSource
{
    internal static readonly ToolCallProposalSource Default = new();

    /// <inheritdoc />
    public ValueTask<ModelAuthoredProposal?> GetProposalAsync(FunctionInvocationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(Find(context));
    }

    private static ModelAuthoredProposal? Find(FunctionInvocationContext context)
    {
        var callId = context.CallContent?.CallId;
        var gated = context.Function.Name;
        if (callId is null || string.Equals(gated, ApprovalProposalTool.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var step = context.Messages.LastOrDefault(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == callId));
        if (step is null)
        {
            return null;
        }

        var calls = step.Contents.OfType<FunctionCallContent>().ToList();
        if (calls.Count(c => string.Equals(c.Name, gated, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return null;
        }

        var proposals = new List<ModelAuthoredProposal>();
        foreach (var call in calls.Where(c => string.Equals(c.Name, ApprovalProposalTool.ToolName, StringComparison.OrdinalIgnoreCase)))
        {
            // The model only ever saw an aliased name for a tool whose own name a provider refuses (ToolNameAliasChatClient),
            // so a proposal for it names the alias; the gate above that step knows the real name.
            if (TryRead(call, out var tool, out var proposal)
                && (string.Equals(tool, gated, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tool, Invocation.ToolNameAliasChatClient.AliasFor(gated), StringComparison.OrdinalIgnoreCase)))
            {
                proposals.Add(proposal!);
            }
        }

        return proposals.Count == 1 ? proposals[0] : null;
    }

    /// <summary>Reads the arguments of one proposal call; false when they do not form a valid proposal.</summary>
    internal static bool TryRead(FunctionCallContent call, out string? tool, out ModelAuthoredProposal? proposal)
    {
        tool = null;
        proposal = null;
        if (call.Arguments is null)
        {
            return false;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.SerializeToElement(call.Arguments);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Arguments no serializer can write are not a proposal.
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        tool = TextOf(Property(root, "tool"));
        if (string.IsNullOrWhiteSpace(tool))
        {
            return false;
        }

        List<ProposalOption>? options = null;
        if (Property(root, "options") is { ValueKind: JsonValueKind.Array } array)
        {
            options = [];
            foreach (var item in array.EnumerateArray().Take(ModelAuthoredProposal.MaxOptions + 1))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                options.Add(new ProposalOption(
                    TextOf(Property(item, "id")) ?? string.Empty,
                    TextOf(Property(item, "label")) ?? string.Empty,
                    TextOf(Property(item, "rationale")) ?? string.Empty));
            }
        }

        return ModelAuthoredProposal.TryBuild(
            options,
            TextOf(Property(root, "recommendedOptionId")),
            TextOf(Property(root, "question")),
            out proposal,
            out _);
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? TextOf(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };
}
