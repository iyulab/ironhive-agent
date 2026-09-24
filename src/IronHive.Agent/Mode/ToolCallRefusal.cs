namespace IronHive.Agent.Mode;

/// <summary>
/// Why a gated tool call was not run.
/// </summary>
public enum ToolCallRefusalKind
{
    /// <summary>The permission rules said <c>Deny</c>.</summary>
    Denied,

    /// <summary>The rules said <c>Ask</c> and there was no <see cref="IHumanApprovalService"/> to ask.</summary>
    ApprovalUnavailable,

    /// <summary>The rules said <c>Ask</c> and the approver said no.</summary>
    Rejected,

    /// <summary>The tool ran, and an <see cref="IToolResultGuard"/> withheld its result from the model.</summary>
    ResultWithheld
}

/// <summary>
/// The result a gated tool call returns instead of running. It is a tool <i>result</i>, never an
/// exception — the model reads <see cref="Message"/> and changes course — but it is not a tool
/// <i>outcome</i>: a loop reports it with <c>Success = false</c>, so an observer or a client can tell
/// "the tool was refused" from "the tool ran and said this" without matching on the text.
/// </summary>
/// <param name="Kind">Which gate refused the call (or withheld its result).</param>
/// <param name="Reason">The rule's or the approver's reason.</param>
public sealed record ToolCallRefusal(ToolCallRefusalKind Kind, string Reason)
{
    /// <summary>
    /// The text the model receives.
    /// </summary>
    public string Message => Kind switch
    {
        ToolCallRefusalKind.Denied => $"Permission denied: {Reason}",
        ToolCallRefusalKind.ApprovalUnavailable => $"Approval required but no approval service is configured: {Reason}",
        ToolCallRefusalKind.Rejected => $"Approval rejected: {Reason}",
        ToolCallRefusalKind.ResultWithheld => $"Tool result withheld by guard: {Reason}",
        _ => $"Tool call refused: {Reason}"
    };

    /// <inheritdoc />
    public override string ToString() => Message;
}
