using System.Runtime.CompilerServices;
using IronHive.Agent.Exceptions;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tracking;

/// <summary>
/// Enforces an <see cref="IUsageLimiter"/> on every model call — each tool round inside one turn included — by checking
/// the limit before the call and recording the call's usage after it.
/// </summary>
/// <remarks>
/// <para>
/// The agent loops check the limit once per turn and record the turn's usage when it ends. A turn made of many tool
/// rounds runs inside <see cref="FunctionInvokingChatClient"/>, which calls the model again on each round — out of the
/// loop's reach — so a turn could overrun the limit several times over before the next turn's check. Placed
/// <b>inside</b> function invocation, this client sees each of those calls: the round after the one that reached the
/// limit throws <see cref="UsageLimitExceededException"/> instead of calling the model.
/// </para>
/// <para>
/// Two ways to give it its limiter, as with <c>ToolRoundContextChatClient</c>. Building the pipeline yourself:
/// <c>.UseToolInvocationPipeline().UseUsageLimit(limiter)</c>. Building it before the limiter exists (a chat client
/// factory's decorator shared by every client it creates): <c>.UseUsageLimit()</c> unbound — calls pass through
/// unchecked — and the agent loop binds its own limiter when it is constructed. A loop that binds it records usage here
/// only, not again at the end of the turn.
/// </para>
/// </remarks>
public sealed class UsageLimitChatClient : DelegatingChatClient
{
    private Binding? _binding;

    private sealed record Binding(IUsageLimiter Limiter, string? ModelId);

    /// <summary>Creates the client unbound: calls pass through unchecked until <see cref="Bind"/>.</summary>
    /// <param name="innerClient">The client that calls the model.</param>
    public UsageLimitChatClient(IChatClient innerClient)
        : base(innerClient)
    { }

    /// <summary>Creates the client bound to <paramref name="limiter"/>.</summary>
    /// <param name="innerClient">The client that calls the model.</param>
    /// <param name="limiter">The limiter to check and record into.</param>
    /// <param name="modelId">The model id that prices the usage (TokenMeter catalog); <c>null</c> records tokens at no cost.</param>
    public UsageLimitChatClient(IChatClient innerClient, IUsageLimiter limiter, string? modelId = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        _binding = new Binding(limiter, modelId);
    }

    /// <summary>The limiter this client enforces, or <c>null</c> while unbound.</summary>
    public IUsageLimiter? Limiter => _binding?.Limiter;

    /// <summary>
    /// Binds the limiter this client enforces. The agent loops call this with their own limiter when a pipeline built
    /// before it contains this client. Binding the same limiter again does nothing.
    /// </summary>
    /// <param name="limiter">The limiter to check and record into.</param>
    /// <param name="modelId">The model id that prices the usage; <c>null</c> records tokens at no cost.</param>
    /// <exception cref="InvalidOperationException">
    /// Already bound to a different limiter — one pipeline serves one loop; a second loop on it would spend the first
    /// loop's budget.
    /// </exception>
    public void Bind(IUsageLimiter limiter, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var previous = Interlocked.CompareExchange(ref _binding, new Binding(limiter, modelId), null);
        if (previous is not null && !ReferenceEquals(previous.Limiter, limiter))
        {
            throw new InvalidOperationException(
                "This chat client's UsageLimitChatClient is already bound to another loop's usage limiter. " +
                "Create a chat client per loop (IChatClientFactory creates one per call).");
        }
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var binding = _binding;
        ThrowIfExceeded(binding);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Record(binding, response.Usage);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var binding = _binding;
        ThrowIfExceeded(binding);
        UsageDetails? usage = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                usage = TurnGuards.AccumulateUsage(usage, update);
                yield return update;
            }
        }
        finally
        {
            // A stream stopped early still spent what it reported.
            Record(binding, usage);
        }
    }

    private static void ThrowIfExceeded(Binding? binding)
    {
        if (binding?.Limiter.CheckLimits() is { ShouldStop: true } result)
        {
            throw new UsageLimitExceededException(result);
        }
    }

    private static void Record(Binding? binding, UsageDetails? usage)
    {
        if (binding is null || TokenUsage.From(usage) is not { } tokens)
        {
            return;
        }

        TurnGuards.Record(binding.Limiter, tokens, binding.ModelId);
    }
}

/// <summary>Installs a <see cref="UsageLimitChatClient"/> on a chat client pipeline.</summary>
public static class UsageLimitChatClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="UsageLimitChatClient"/> unbound — the agent loop built on this client binds its own limiter. Put it
    /// after <c>UseToolInvocationPipeline()</c> (inside function invocation) so every tool round is checked.
    /// </summary>
    public static ChatClientBuilder UseUsageLimit(this ChatClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new UsageLimitChatClient(inner));
    }

    /// <summary>Adds a <see cref="UsageLimitChatClient"/> bound to <paramref name="limiter"/>.</summary>
    /// <param name="builder">The chat client builder.</param>
    /// <param name="limiter">The limiter to check and record into.</param>
    /// <param name="modelId">The model id that prices the usage; <c>null</c> records tokens at no cost.</param>
    public static ChatClientBuilder UseUsageLimit(this ChatClientBuilder builder, IUsageLimiter limiter, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limiter);
        return builder.Use(inner => new UsageLimitChatClient(inner, limiter, modelId));
    }
}
