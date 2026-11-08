using System.Runtime.CompilerServices;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Enforces a turn's own cap on tool rounds (<see cref="TurnLimits.WithMaxIterations"/>) on every model call of the turn.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/> is state of the shared client, so it cannot differ
/// between two turns that run through one client at the same time. This client keeps no state: on each call it counts the
/// tool rounds already in the request (the assistant messages that called tools, after the last user message) and, once they
/// reach the turn's cap, offers <b>no tools</b> — the same thing function invocation does at its own cap, so the model answers
/// in text and the turn ends. The calls of the last round still run: this client sits under function invocation and only
/// shapes the request that follows them.
/// </para>
/// <para>
/// The effective cap of a turn is the smaller of this value and the function-invoking client's own, so a turn can be
/// bounded lower than the process but never higher (<see cref="TurnStopReasons.MaximumIterationsOf(IChatClient, ChatOptions?)"/>).
/// Place it <b>directly under</b> function invocation and <b>above</b> <see cref="ToolRoundContextChatClient"/>, so it counts the rounds
/// before any compaction replaces them: <c>.UseFunctionInvocation().UseTurnLimits().UseToolRoundContext()</c>.
/// </para>
/// <para>
/// Rounds are counted from the request's messages, so a turn continued after host-run tools counts the rounds of its earlier
/// segment too, while function invocation counts a continuation from zero.
/// </para>
/// </remarks>
public sealed class TurnLimitChatClient : DelegatingChatClient
{
    /// <summary>Creates the client over <paramref name="innerClient"/>.</summary>
    public TurnLimitChatClient(IChatClient innerClient)
        : base(innerClient)
    { }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        return base.GetResponseAsync(list, Limit(list, options), cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        await foreach (var update in base.GetStreamingResponseAsync(list, Limit(list, options), cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>The options to send: the turn's own, or the same without tools once its cap on tool rounds is used up.</summary>
    internal static ChatOptions? Limit(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        if (TurnLimits.MaxIterationsOf(options) is not { } cap || options?.Tools is not { Count: > 0 })
        {
            return options;
        }

        if (ToolRoundsOfTheTurn(messages) < cap)
        {
            return options;
        }

        var limited = options.Clone();
        limited.Tools = null;
        limited.ToolMode = null;
        return limited;
    }

    /// <summary>The assistant messages that called tools after the request's last user message (an injected reminder is not one).</summary>
    internal static int ToolRoundsOfTheTurn(IReadOnlyList<ChatMessage> messages)
    {
        var rounds = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role == ChatRole.User && !ContextManager.IsInjected(message))
            {
                break;
            }

            if (message.Role == ChatRole.Assistant && message.Contents.Any(c => c is FunctionCallContent))
            {
                rounds++;
            }
        }

        return rounds;
    }
}

/// <summary>Pipeline registration for <see cref="TurnLimitChatClient"/>.</summary>
public static class TurnLimitChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="TurnLimitChatClient"/> so a turn can carry its own cap on tool rounds
    /// (<see cref="TurnLimits.WithMaxIterations"/>). Call it <b>after</b> <c>UseFunctionInvocation()</c> (it must sit inside function
    /// invocation) and <b>before</b> <c>UseToolRoundContext()</c>.
    /// </summary>
    public static ChatClientBuilder UseTurnLimits(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new TurnLimitChatClient(inner));
    }
}
