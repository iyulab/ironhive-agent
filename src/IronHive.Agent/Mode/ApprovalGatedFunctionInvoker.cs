using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Mode;

/// <summary>
/// Builds the <c>FunctionInvoker</c> that puts the permission rules and human approval in front of
/// every tool call a Microsoft.Extensions.AI function-invoking client makes.
/// </summary>
/// <remarks>
/// <para>
/// An <c>IAgentLoop</c> never invokes tools itself — the consumer's <c>UseFunctionInvocation()</c>
/// middleware does — so this is the seam where "ask before running" belongs. Install it on the chat
/// client the loop is given:
/// </para>
/// <code>
/// var client = inner.AsBuilder()
///     .UseFunctionInvocation(configure: c =&gt;
///         c.FunctionInvoker = ApprovalGatedFunctionInvoker.Create(modeToolFilter, approvalService))
///     .Build();
/// </code>
/// <para>
/// For each call the gate runs <see cref="IModeToolFilter.AssessRisk"/>: <c>Allow</c> invokes the
/// tool; <c>Deny</c> returns the reason as the tool's result (never an exception — the model reads it
/// and changes course); <c>Ask</c> consults <see cref="IHumanApprovalService"/> and invokes only on
/// approval, applying <see cref="ApprovalResult.ModifiedArguments"/> when the approver edited them.
/// An <c>Ask</c> verdict with no approval service is refused, not passed: a gate that lets "ask"
/// through when nobody can be asked is the silent no-op this exists to remove — register a service,
/// or set the rule (or <c>PermissionConfig.DefaultAction</c>) to <c>Allow</c>.
/// </para>
/// </remarks>
public static class ApprovalGatedFunctionInvoker
{
    /// <summary>
    /// Creates the invoker delegate.
    /// </summary>
    /// <param name="modeToolFilter">Produces the verdict for each call.</param>
    /// <param name="approvalService">
    /// Asked when the verdict is <c>Ask</c>. May be null, in which case every <c>Ask</c> verdict is
    /// refused with a reason that says so.
    /// </param>
    /// <param name="inner">
    /// What runs an approved call. Defaults to invoking the function directly; pass another invoker
    /// (for instance one that turns marshalling errors into recovery directives) to compose.
    /// </param>
    /// <param name="logger">Receives a line per refusal; optional.</param>
    public static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Create(
        IModeToolFilter modeToolFilter,
        IHumanApprovalService? approvalService,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? inner = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(modeToolFilter);

        var gate = new ApprovalGate(modeToolFilter, approvalService, logger);

        return async (context, cancellationToken) =>
        {
            var decision = await gate.DecideAsync(context.Function.Name, context.Arguments, cancellationToken);
            if (!decision.ShouldProceed)
            {
                return decision.Refusal;
            }

            if (decision.ModifiedArguments is not null)
            {
                foreach (var (key, value) in decision.ModifiedArguments)
                {
                    context.Arguments[key] = value;
                }
            }

            return inner is null
                ? await context.Function.InvokeAsync(context.Arguments, cancellationToken)
                : await inner(context, cancellationToken);
        };
    }
}
