using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// A <see cref="ContextManager"/> as a Microsoft.Extensions.AI <see cref="IChatReducer"/>: the manager's whole preparation —
/// tool-result compaction, observation masking, compaction when over budget (with its summarizer, if it has one), the goal
/// reminder and the instruction blocks — applied by any pipeline that takes a reducer, such as
/// <c>ChatClientBuilder.UseChatReducer(contextManager.AsChatReducer())</c> or a framework that asks for an <see cref="IChatReducer"/>.
/// </summary>
/// <remarks>
/// The agent loops prepare their history themselves and need no reducer. This is for a loop of someone else's that keeps its
/// own message list and sends it whole on every call. The reduction is of what is sent; the caller's list is not changed.
/// Reducing an already reduced list again is safe: the blocks a preparation added are removed before the next one.
/// </remarks>
public sealed class ContextManagerChatReducer : IChatReducer
{
    /// <summary>Creates a reducer over <paramref name="contextManager"/>.</summary>
    public ContextManagerChatReducer(ContextManager contextManager)
        => ContextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));

    /// <summary>The manager whose preparation this reducer applies.</summary>
    public ContextManager ContextManager { get; }

    /// <inheritdoc />
    public async Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return await ContextManager.PrepareHistoryAsync([.. messages], cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Turns a <see cref="ContextManager"/> into an <see cref="IChatReducer"/>.</summary>
public static class ContextManagerChatReducerExtensions
{
    /// <summary>This manager's preparation as an <see cref="IChatReducer"/> (<see cref="ContextManagerChatReducer"/>).</summary>
    public static IChatReducer AsChatReducer(this ContextManager contextManager) => new ContextManagerChatReducer(contextManager);
}
