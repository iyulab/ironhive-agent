using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Mode;

/// <summary>
/// The permission rules and human approval in front of every tool call of a <see cref="ToolInvocationPipeline"/>.
/// Opt-in: a pipeline without it has no permission gate. Register it with <c>services.AddIronHiveAgentApprovalGate()</c>,
/// or put it in a pipeline you build yourself.
/// </summary>
/// <remarks>
/// For each call the gate (through <see cref="ApprovalGate"/>) first enforces Planning mode when it has a mode manager,
/// then acts on the <see cref="IToolCallPolicy"/> verdict: <c>Allow</c> runs the call; <c>Deny</c> returns a
/// <see cref="ToolCallRefusal"/> as the tool's result (never an exception — the model reads its message and changes
/// course, while a loop reports the call with <c>Success = false</c>); <c>Ask</c> consults the
/// <see cref="IHumanApprovalService"/> and runs the call only on approval, with <see cref="ApprovalResult.ModifiedArguments"/>
/// applied when the approver edited them. An <c>Ask</c> verdict with no approver is refused, not passed: a gate that lets
/// "ask" through when nobody can be asked is a silent no-op. To have no gate, do not register one; to let a call through,
/// make its rule (or <c>PermissionConfig.DefaultAction</c>) <c>Allow</c>.
/// </remarks>
public sealed class ApprovalGateMiddleware : IToolInvocationMiddleware
{
    private readonly ApprovalGate _gate;

    /// <summary>Creates the gate.</summary>
    /// <param name="policy">Produces the verdict for each call.</param>
    /// <param name="approvalService">Asked when the verdict is <c>Ask</c>; without one such a call is refused.</param>
    /// <param name="logger">Receives a line per refusal; optional.</param>
    /// <param name="modeManager">When given, Planning mode is enforced: a tool it does not permit is denied.</param>
    /// <param name="modeToolFilter">Says which tools a mode permits; required with <paramref name="modeManager"/>.</param>
    public ApprovalGateMiddleware(
        IToolCallPolicy policy,
        IHumanApprovalService? approvalService = null,
        ILogger<ApprovalGateMiddleware>? logger = null,
        IModeManager? modeManager = null,
        IModeToolFilter? modeToolFilter = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _gate = new ApprovalGate(policy, approvalService, logger, modeManager, modeToolFilter);
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var decision = await _gate.DecideAsync(context.Function.Name, context.Arguments, context.CallContent?.CallId, cancellationToken);
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

        return await next(context, cancellationToken);
    }
}
