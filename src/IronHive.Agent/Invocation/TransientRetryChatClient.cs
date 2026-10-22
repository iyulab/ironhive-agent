using System.Runtime.CompilerServices;
using IronHive.Agent.ErrorRecovery;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Retries a model call that failed for a transient reason — the provider's gateway answered 502/504, the connection could
/// not be made or was reset, the response stream ended before it began — as long as nothing of that call has reached the
/// caller yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> a turn under function invocation makes many model calls. Without a retry, one gateway blip on the twentieth
/// call ends the whole turn and loses the tool work before it, where an HTTP client with retries (the convention of the
/// provider SDKs and of LiteLLM-based agents) would have carried on.
/// </para>
/// <para>
/// <b>What is retried:</b> what <see cref="ErrorRecoveryService"/> classifies as a network failure, a timeout or a rate
/// limit whose wait fits <see cref="MaxDelay"/> — the same classification the agent loop already uses for a buffered call
/// (the IronProw gateway's view of transient statuses). A refusal (401/403/402, an invalid request, a context overflow) is
/// not. A streamed call is retried only when it fails <b>before its first update</b>; once an update has been passed on, a
/// retry would hand the caller a second beginning, so the failure propagates as before.
/// </para>
/// <para>
/// <b>Placement:</b> directly around the provider client, inside function invocation, so each model call of a turn is
/// retried on its own: <c>new TransientRetryChatClient(providerClient)</c> under the rest of the pipeline.
/// </para>
/// </remarks>
public sealed class TransientRetryChatClient : DelegatingChatClient
{
    private readonly IErrorRecoveryService _recovery;

    /// <summary>Creates the client.</summary>
    /// <param name="innerClient">The provider client whose calls are retried.</param>
    /// <param name="recovery">Classifies a failure; null uses an <see cref="ErrorRecoveryService"/> with default settings.</param>
    public TransientRetryChatClient(IChatClient innerClient, IErrorRecoveryService? recovery = null)
        : base(innerClient)
    {
        _recovery = recovery ?? new ErrorRecoveryService();
    }

    /// <summary>How many times a failed call is tried again. Default 1.</summary>
    public int MaxRetries { get; init; } = 1;

    /// <summary>The longest wait before a retry; a failure that asks for longer (a long rate-limit window) is not retried. Default 30 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(request, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetries && TryGetRetryDelay(ex, cancellationToken, out var delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages as IList<ChatMessage> ?? messages.ToList();
        for (var attempt = 0; ; attempt++)
        {
            var updates = base.GetStreamingResponseAsync(request, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                bool hasFirst;
                TimeSpan? retryAfter = null;
                try
                {
                    hasFirst = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxRetries && TryGetRetryDelay(ex, cancellationToken, out var delay))
                {
                    hasFirst = false;
                    retryAfter = delay;
                }

                if (retryAfter is { } wait)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!hasFirst)
                {
                    yield break;
                }

                // From the first update on, the caller has seen this call: it is not retried.
                yield return updates.Current;
                while (await updates.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return updates.Current;
                }
                yield break;
            }
            finally
            {
                await updates.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Retried: a failure the recovery service would wait out and retry, within MaxDelay. Never the caller's cancellation.
    private bool TryGetRetryDelay(Exception exception, CancellationToken cancellationToken, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var analysis = _recovery.AnalyzeException(exception);
        if (analysis.Error.Category is not (ErrorCategory.Network or ErrorCategory.Timeout or ErrorCategory.RateLimit)
            || analysis.RecommendedAction is not (RecoveryAction.WaitAndRetry or RecoveryAction.Retry))
        {
            return false;
        }

        delay = analysis.RetryDelay ?? TimeSpan.FromSeconds(1);
        return delay <= MaxDelay;
    }
}
