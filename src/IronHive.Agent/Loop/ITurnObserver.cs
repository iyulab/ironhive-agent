namespace IronHive.Agent.Loop;

/// <summary>
/// Observes a completed turn, with the turn's own record of what it did.
/// </summary>
/// <remarks>
/// <para>
/// The loop assembles the correlation between what the model said and which tools it called, uses it
/// to build the turn's history message, and — before this seam existed — dropped it. A consumer could
/// only watch the streamed output and separately instrument its own tool implementations, then try to
/// rebuild that correlation from outside. This hands over the record the loop already has.
/// </para>
/// <para>
/// <b>Observe and append, never edit.</b> The observer runs after the turn is complete, which on the
/// streaming path means every text delta has already been yielded and, in practice, rendered — there
/// is nothing left to amend. An observer may return text to append after the turn's output; it cannot
/// change what the model already said. The two entry points behave identically, so a check does not
/// silently stop firing because a consumer switched to streaming.
/// </para>
/// <para>
/// An addendum reaches the consumer, not the conversation: it is not written into history, because the
/// model did not say it and feeding a fabricated assistant utterance back into the next turn's context
/// would be worse than the problem it corrects.
/// </para>
/// <para>
/// What counts as a claim worth correcting is deliberately not the library's judgment — that is
/// product policy and lives in the observer.
/// </para>
/// </remarks>
public interface ITurnObserver
{
    /// <summary>
    /// Called once per completed turn, before the loop finishes returning it.
    /// </summary>
    /// <param name="turn">What the turn produced and which tools produced it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Text to append after the turn's output, or <c>null</c> to observe only. When several observers
    /// return text, the pieces are joined in registration order.
    /// </returns>
    ValueTask<string?> OnTurnCompletedAsync(TurnRecord turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// What one completed turn produced, and the tool activity that produced it.
/// </summary>
public record TurnRecord
{
    /// <summary>
    /// The turn's final assistant text, as the model wrote it — an observer's addendum is never
    /// folded into this.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The tool calls this turn made.
    /// </summary>
    /// <remarks>
    /// <see cref="ToolCallResult.Success"/> is <c>null</c> unless the <c>IChatClient</c> given to the
    /// loop was wrapped with <c>UseFunctionInvocation()</c> — the loop extracts the calls the model
    /// requested, it does not invoke them. The presence and arguments of a call are always populated;
    /// its outcome is knowable only when something in the pipeline actually ran it.
    /// </remarks>
    public IReadOnlyList<ToolCallResult> ToolCalls { get; init; } = [];

    /// <summary>
    /// Token usage for this turn, when the provider reported it.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Thinking/reasoning content for this turn, when the loop extracts it.
    /// </summary>
    public ThinkingContent? ThinkingContent { get; init; }
}

/// <summary>
/// Runs a turn's observers and joins whatever they want appended. Shared by every
/// <see cref="IAgentLoop"/> implementation so the seam behaves identically on all of them.
/// </summary>
internal static class TurnObserverNotifier
{
    internal const string AddendumSeparator = "\n\n";

    public static async ValueTask<string?> NotifyAsync(
        IReadOnlyList<ITurnObserver> observers,
        TurnRecord turn,
        CancellationToken cancellationToken)
    {
        if (observers.Count == 0)
        {
            return null;
        }

        List<string>? addenda = null;

        foreach (var observer in observers)
        {
            var addendum = await observer.OnTurnCompletedAsync(turn, cancellationToken);
            if (string.IsNullOrEmpty(addendum))
            {
                continue;
            }

            addenda ??= [];
            addenda.Add(addendum);
        }

        return addenda is null ? null : string.Join(AddendumSeparator, addenda);
    }
}
