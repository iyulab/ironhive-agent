using System.Runtime.CompilerServices;
using IronHive.Abstractions.Exceptions;
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
/// <para>
/// With <see cref="Context.ContextManager.CompactOnOverflow"/> on, this is also where an overflow is caught: a call that
/// fails with <see cref="ContextOverflowException"/> is compacted once by the manager (a summarizing compaction, the one
/// LLM call this client may cause), the window is learned from the error, and the call is retried once; a second overflow
/// propagates. A streaming call is retried only if the failure came before its first update. The compacted prefix is
/// reused for the turn's later tool rounds, which re-send the same messages plus the new ones, so one turn compacts once.
/// </para>
/// </remarks>
public sealed class ToolRoundContextChatClient : DelegatingChatClient
{
    private ContextManager? _contextManager;

    // The messages an overflow compaction replaced, and what replaced them. A later call whose messages start with the
    // same instances (the next tool round of the turn) sends the replacement plus its new tail instead.
    private (ChatMessage[] Original, IReadOnlyList<ChatMessage> Compacted)? _overflowCompaction;

    /// <summary>Creates the client unbound: requests pass through unchanged until <see cref="Bind"/>.</summary>
    public ToolRoundContextChatClient(IChatClient innerClient)
        : base(innerClient)
    { }

    /// <summary>Creates the client bound to <paramref name="contextManager"/>.</summary>
    public ToolRoundContextChatClient(IChatClient innerClient, ContextManager contextManager)
        : base(innerClient)
    {
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
        contextManager.AttachOverflowHandler();
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
        if (previous is null)
        {
            contextManager.AttachOverflowHandler();
        }
        else if (!ReferenceEquals(previous, contextManager))
        {
            throw new InvalidOperationException(
                "This chat client's ToolRoundContextChatClient is already bound to another loop's ContextManager. " +
                "Create a chat client per loop (IChatClientFactory creates one per call).");
        }
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = Prepare(messages, out var original);
        try
        {
            return await base.GetResponseAsync(request, options, cancellationToken);
        }
        catch (ContextOverflowException overflow) when (_contextManager is { CompactOnOverflow: true })
        {
            var compacted = await CompactAfterOverflowAsync(original, request, overflow, cancellationToken);
            return await base.GetResponseAsync(compacted, options, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Prepare(messages, out var original);
        var updates = base.GetStreamingResponseAsync(request, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            bool any;
            try
            {
                any = await updates.MoveNextAsync();
            }
            catch (ContextOverflowException overflow) when (_contextManager is { CompactOnOverflow: true })
            {
                // Nothing has been yielded yet, so the call can still be made again as if it were the first.
                await updates.DisposeAsync();
                var compacted = await CompactAfterOverflowAsync(original, request, overflow, cancellationToken);
                updates = base.GetStreamingResponseAsync(compacted, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                any = await updates.MoveNextAsync();
            }

            if (!any)
            {
                yield break;
            }

            yield return updates.Current;
            while (await updates.MoveNextAsync())
            {
                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync();
        }
    }

    private IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, out IReadOnlyList<ChatMessage> original)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        original = list;
        if (_contextManager is not { } manager)
        {
            return list;
        }

        if (_overflowCompaction is { } previous && StartsWith(list, previous.Original))
        {
            list = [.. previous.Compacted, .. list.Skip(previous.Original.Length)];
        }

        return manager.ReduceToolResults(list);
    }

    private async Task<IReadOnlyList<ChatMessage>> CompactAfterOverflowAsync(
        IReadOnlyList<ChatMessage> original, IEnumerable<ChatMessage> sent, ContextOverflowException overflow,
        CancellationToken cancellationToken)
    {
        var compacted = await _contextManager!.CompactAfterOverflowAsync(
            sent as IReadOnlyList<ChatMessage> ?? [.. sent], overflow, cancellationToken);
        _overflowCompaction = ([.. original], compacted);
        return compacted;
    }

    private static bool StartsWith(IReadOnlyList<ChatMessage> messages, ChatMessage[] prefix)
    {
        if (messages.Count < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!ReferenceEquals(messages[i], prefix[i]))
            {
                return false;
            }
        }

        return true;
    }
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
