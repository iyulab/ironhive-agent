using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// How a turn that reached the function-invocation middleware's iteration cap with its work unfinished
/// (<see cref="TurnStopReason.StepLimit"/>) is closed instead of ending there (<see cref="AgentOptions.WrapUpAtStepLimit"/>).
/// </summary>
/// <remarks>
/// <para>
/// At the cap, <see cref="FunctionInvokingChatClient"/> runs the last round's calls and asks the model once more without
/// tools, so an agent whose work only counts once a final tool runs (a write, a submit, a save) cannot finish: everything
/// it read is lost with the turn, and a «try again» reads it all again. With a wrap-up the loop answers any call the
/// model still made (with <see cref="UnrunCallNotice"/>; such calls are not run), adds <see cref="Instruction"/> as a
/// user message, and makes one closing request offering only <see cref="ToolNames"/>. The closing request runs through
/// the same chat client, so a call to one of those tools is run as usual. The turn's record covers both requests; it
/// reports <see cref="TurnStopReason.WrappedUp"/> when the closing request finished, or the closing request's own stop
/// reason otherwise.
/// </para>
/// <para>
/// The closing request is a new request to the function-invocation middleware, so its own iteration cap applies to it
/// again. Offer only the tools that finish the work.
/// </para>
/// </remarks>
public sealed class StepLimitWrapUp
{
    /// <summary>
    /// The closing instruction, sent as a user message after the capped rounds. Default: asks the model to finish the task
    /// now from what it has, or to say what it still needs.
    /// </summary>
    public string Instruction { get; init; } =
        "You have used all the tool rounds this turn allows. Finish the task now from what you have already read: make the "
        + "change or give the answer with the tools still available, or say what you would still need.";

    /// <summary>
    /// The names of the tools the closing request offers, from those the turn offered. Empty (the default): none, so the
    /// closing request is text only.
    /// </summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>
    /// The result a call the model made at the cap receives instead of running. Default: says the call was not run because
    /// the rounds were used up.
    /// </summary>
    public string UnrunCallNotice { get; init; } = "Not run: the tool rounds for this turn are used up.";
}

/// <summary>The messages and options of a <see cref="StepLimitWrapUp"/>'s closing request.</summary>
internal static class StepLimitWrapUps
{
    /// <summary>
    /// What the loop adds before the closing request: a tool message answering each call in <paramref name="producedMessages"/>
    /// that has no result (with <see cref="StepLimitWrapUp.UnrunCallNotice"/>), then the instruction as a user message.
    /// </summary>
    public static List<ChatMessage> ClosingMessages(IEnumerable<ChatMessage> producedMessages, StepLimitWrapUp wrapUp)
    {
        var produced = producedMessages as IReadOnlyCollection<ChatMessage> ?? [.. producedMessages];
        var answered = produced.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var unrun = produced.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Where(c => !answered.Contains(c.CallId)).ToList();

        var messages = new List<ChatMessage>();
        if (unrun.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, [.. unrun.Select(c => (AIContent)new FunctionResultContent(c.CallId, wrapUp.UnrunCallNotice))]));
        }

        messages.Add(new ChatMessage(ChatRole.User, wrapUp.Instruction));
        return messages;
    }

    /// <summary>The turn's options with only <see cref="StepLimitWrapUp.ToolNames"/> left of its tools.</summary>
    public static ChatOptions Options(ChatOptions turnOptions, StepLimitWrapUp wrapUp)
    {
        var options = turnOptions.Clone();
        var names = wrapUp.ToolNames.ToHashSet(StringComparer.Ordinal);
        var kept = (turnOptions.Tools ?? []).Where(t => names.Contains(t.Name)).ToList();
        options.Tools = kept.Count > 0 ? kept : null;
        if (kept.Count == 0)
        {
            options.ToolMode = null;
        }

        return options;
    }

    /// <summary>A completed closing request reads as wrapped up; any other ending is reported as it is.</summary>
    public static TurnStopReason StopReason(TurnStopReason closing) =>
        closing == TurnStopReason.Completed ? TurnStopReason.WrappedUp : closing;
}
