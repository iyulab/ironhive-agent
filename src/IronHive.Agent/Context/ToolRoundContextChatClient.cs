using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Applies a <see cref="ContextManager"/>'s per-request reductions — tool-result compaction and observation masking
/// (<see cref="ContextManager.ReduceToolResults"/>) — to every model call, including each tool round inside one turn.
/// </summary>
/// <remarks>
/// The agent loops prepare history once per turn, before the first model call. The tool rounds that follow run inside
/// <see cref="FunctionInvokingChatClient"/>, which re-sends the growing message list on each round — out of the loop's
/// reach. Placed <b>inside</b> function invocation (<c>.UseFunctionInvocation().UseToolRoundContext(contextManager)</c>),
/// this client sees each of those calls, so a turn made of one user message and many tool rounds (reading a long
/// document, walking a folder) is reduced on every round, not only before the first. With
/// <see cref="CompactionConfig.ObservationMaskingProtectedRounds"/> set, older rounds' results are masked inside the
/// turn. Only the request sent to the model is reduced; the history the caller keeps is unchanged. No LLM call is made
/// here — summarizing compaction stays with the loop's once-per-turn preparation.
/// </remarks>
public sealed class ToolRoundContextChatClient : DelegatingChatClient
{
    private readonly ContextManager _contextManager;

    public ToolRoundContextChatClient(IChatClient innerClient, ContextManager contextManager)
        : base(innerClient)
    {
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Reduce(messages), options, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Reduce(messages), options, cancellationToken);

    private IReadOnlyList<ChatMessage> Reduce(IEnumerable<ChatMessage> messages)
        => _contextManager.ReduceToolResults(messages as IReadOnlyList<ChatMessage> ?? [.. messages]);
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
}
