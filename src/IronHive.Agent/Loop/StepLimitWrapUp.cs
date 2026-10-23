using IronHive.Agent.Delegation;
using IronHive.Agent.Invocation;
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
/// it read is lost with the turn, and a «try again» reads it all again. With a wrap-up the loop runs any call the model
/// still made to one of <see cref="ToolNames"/> — through the chat client's <see cref="ToolInvocationPipeline"/>, so the
/// permission rules and guards apply as to any call — answers the other calls with <see cref="UnrunCallNotice"/> (they
/// are not run), adds <see cref="Instruction"/> as a user message, and makes one closing request offering only
/// <see cref="ToolNames"/>. A finishing call that ends the turn (<see cref="FunctionInvocationContext.Terminate"/>) ends it
/// there, without the closing request (<see cref="TurnStopReason.ToolTerminated"/>). The closing request runs through
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
    /// The result a call the model made at the cap to a tool not in <see cref="ToolNames"/> receives instead of running.
    /// Default: says the call was not run because the rounds were used up.
    /// </summary>
    public string UnrunCallNotice { get; init; } = "Not run: the tool rounds for this turn are used up.";
}

/// <summary>The messages and options of a <see cref="StepLimitWrapUp"/>'s closing request.</summary>
internal static class StepLimitWrapUps
{
    /// <summary>
    /// What the loop adds before the closing request: a tool message answering each call in <paramref name="producedMessages"/>
    /// that has no result — a call to one of <see cref="StepLimitWrapUp.ToolNames"/> run through <paramref name="pipeline"/>
    /// (or the tool itself when the client has none), any other with <see cref="StepLimitWrapUp.UnrunCallNotice"/> — then the
    /// instruction as a user message, unless a call ended the turn.
    /// </summary>
    public static async Task<ClosingPrelude> ClosingMessagesAsync(
        IEnumerable<ChatMessage> producedMessages,
        StepLimitWrapUp wrapUp,
        IList<AITool>? turnTools,
        ToolInvocationPipeline? pipeline,
        IReadOnlyList<ChatMessage> conversation,
        CancellationToken cancellationToken)
    {
        var produced = producedMessages as IReadOnlyCollection<ChatMessage> ?? [.. producedMessages];
        var answered = produced.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        var unrun = produced.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Where(c => !answered.Contains(c.CallId)).ToList();
        var finishing = wrapUp.ToolNames.ToHashSet(StringComparer.Ordinal);

        var results = new List<FunctionResultContent>();
        var ran = new List<FunctionResultContent>();
        var terminated = false;
        for (var index = 0; index < unrun.Count; index++)
        {
            var call = unrun[index];
            if (terminated
                || !finishing.Contains(call.Name)
                || turnTools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name) is not { } function)
            {
                results.Add(new FunctionResultContent(call.CallId, wrapUp.UnrunCallNotice));
                continue;
            }

            var result = await RunAsync(call, function, pipeline, conversation, index, unrun.Count, cancellationToken);
            results.Add(result.Content);
            ran.Add(result.Content);
            terminated = result.Terminate;
        }

        var messages = new List<ChatMessage>();
        if (results.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, [.. results]));
        }

        if (!terminated)
        {
            messages.Add(new ChatMessage(ChatRole.User, wrapUp.Instruction));
        }

        return new ClosingPrelude(messages, results, ran, terminated);
    }

    private static async Task<(FunctionResultContent Content, bool Terminate)> RunAsync(
        FunctionCallContent call, AIFunction function, ToolInvocationPipeline? pipeline, IReadOnlyList<ChatMessage> conversation,
        int index, int count, CancellationToken cancellationToken)
    {
        var context = new FunctionInvocationContext
        {
            Function = function,
            // A copy: a middleware that edits the arguments must not rewrite the call the conversation records.
            Arguments = new AIFunctionArguments(call.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments)),
            CallContent = call,
            Messages = [.. conversation],
            FunctionCallIndex = index,
            FunctionCount = count,
        };

        try
        {
            object? result;
            using (ToolInvocationScope.Enter(conversation))
            {
                result = pipeline is not null
                    ? await pipeline.InvokeAsync(context, cancellationToken)
                    : await function.InvokeAsync(context.Arguments, cancellationToken);
            }

            return (new FunctionResultContent(call.CallId, result), context.Terminate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new FunctionResultContent(call.CallId, $"Error: {ex.Message}") { Exception = ex }, false);
        }
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

/// <summary>What the loop adds before a wrap-up's closing request, and what it ran.</summary>
/// <param name="Messages">The tool message answering the calls left at the cap, then the instruction (absent when a call ended the turn).</param>
/// <param name="Results">Every result in the tool message.</param>
/// <param name="Ran">The results of the finishing calls that ran.</param>
/// <param name="Terminated">A finishing call ended the turn: no closing request follows.</param>
internal sealed record ClosingPrelude(
    List<ChatMessage> Messages, IReadOnlyList<FunctionResultContent> Results, IReadOnlyList<FunctionResultContent> Ran, bool Terminated);
