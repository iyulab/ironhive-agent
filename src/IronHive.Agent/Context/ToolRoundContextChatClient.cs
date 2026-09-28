using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Applies a <see cref="ContextManager"/>'s per-request reductions — tool-result compaction and observation masking
/// (<see cref="ContextManager.ReduceToolResults"/>) — to every model call, including each tool round inside one turn.
/// </summary>
/// <remarks>
/// <para>
/// The agent loops prepare history once per turn, before the first model call. The tool rounds that follow run inside
/// <see cref="FunctionInvokingChatClient"/>, which re-sends the growing message list on each round — out of the loop's
/// reach. Placed <b>inside</b> function invocation, this client sees each of those calls, so a turn made of one user
/// message and many tool rounds (reading a long document, walking a folder) is reduced on every round, not only before
/// the first. With <see cref="CompactionConfig.ObservationMaskingProtectedRounds"/> set, older rounds' results are masked
/// inside the turn. Only the request sent to the model is reduced; the history the caller keeps is unchanged. No LLM call
/// is made here — summarizing compaction stays with the loop's once-per-turn preparation.
/// </para>
/// <para>
/// Two ways to give it its manager. Building the pipeline yourself: <c>.UseFunctionInvocation().UseToolRoundContext(cm)</c>.
/// Building it before the manager exists (a <c>ChatClientFactory</c> decorator shared by every client it creates):
/// <c>.UseFunctionInvocation().UseToolRoundContext()</c> — unbound, it passes requests through unchanged — and the agent loop
/// binds its own <see cref="ContextManager"/> when it is constructed (<see cref="Bind"/>, found through
/// <see cref="IChatClient.GetService"/>).
/// </para>
/// </remarks>
public sealed class ToolRoundContextChatClient : DelegatingChatClient
{
    private ContextManager? _contextManager;

    /// <summary>Creates the client unbound: requests pass through unchanged until <see cref="Bind"/>.</summary>
    public ToolRoundContextChatClient(IChatClient innerClient)
        : base(innerClient)
    { }

    /// <summary>Creates the client bound to <paramref name="contextManager"/>.</summary>
    public ToolRoundContextChatClient(IChatClient innerClient, ContextManager contextManager)
        : base(innerClient)
    {
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
    }

    /// <summary>The manager whose reductions are applied, or <c>null</c> while unbound.</summary>
    public ContextManager? ContextManager => _contextManager;

    /// <summary>
    /// Binds the manager whose reductions this client applies. The agent loops call this with their own manager when a
    /// pipeline built before it contains this client. Binding the same manager again does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Already bound to a different manager — one pipeline serves one loop's context; reusing it for a second loop would
    /// reduce that loop's requests by the first loop's settings.
    /// </exception>
    public void Bind(ContextManager contextManager)
    {
        ArgumentNullException.ThrowIfNull(contextManager);
        var previous = Interlocked.CompareExchange(ref _contextManager, contextManager, null);
        if (previous is not null && !ReferenceEquals(previous, contextManager))
        {
            throw new InvalidOperationException(
                "This chat client's ToolRoundContextChatClient is already bound to another loop's ContextManager. " +
                "Create a chat client per loop (IChatClientFactory creates one per call).");
        }
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Reduce(messages), options, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Reduce(messages), options, cancellationToken);

    private IEnumerable<ChatMessage> Reduce(IEnumerable<ChatMessage> messages)
        => _contextManager is { } manager
            ? manager.ReduceToolResults(messages as IReadOnlyList<ChatMessage> ?? [.. messages])
            : messages;
}

/// <summary>
/// <see cref="ChatClientBuilder"/> registration for <see cref="ToolRoundContextChatClient"/>.
/// </summary>
public static class ToolRoundContextChatClientBuilderExtensions
{
    /// <summary>
    /// Reduces every model call's tool results with <paramref name="contextManager"/> — call it <b>after</b>
    /// <c>UseFunctionInvocation()</c> so it sits inside function invocation and sees each tool round of a turn.
    /// Pass the same <see cref="ContextManager"/> the agent loop uses.
    /// </summary>
    public static ChatClientBuilder UseToolRoundContext(this ChatClientBuilder builder, ContextManager contextManager)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(contextManager);
        return builder.Use(inner => new ToolRoundContextChatClient(inner, contextManager));
    }

    /// <summary>
    /// Adds an unbound <see cref="ToolRoundContextChatClient"/> — for a pipeline built before the loop's manager exists
    /// (a chat client factory's decorator). The agent loop constructed over the resulting client binds its own
    /// <see cref="ContextManager"/>. Call it <b>after</b> <c>UseFunctionInvocation()</c>.
    /// </summary>
    public static ChatClientBuilder UseToolRoundContext(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new ToolRoundContextChatClient(inner));
    }
}
