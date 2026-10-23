using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Exceptions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Loop;

/// <summary>
/// The per-turn safeguards every agent loop applies around its model call: the usage-limit check
/// before the call, the usage record after it, and the single retry of a transient failure.
/// </summary>
/// <remarks>
/// <see cref="AgentLoop"/> and <see cref="ThinkingAgentLoop"/> run the same turn. They used to carry these
/// safeguards separately, and only <see cref="AgentLoop"/> had them — so a consumer that configured a usage
/// limit got no stop at all on the thinking path, which is the one hosts prefer. Keeping them in one place
/// is what keeps the two loops from drifting apart again.
/// </remarks>
internal sealed class TurnGuards
{
    private readonly IUsageLimiter? _usageLimiter;
    private readonly IErrorRecoveryService? _errorRecovery;
    private readonly string? _modelId;
    private readonly bool _recordedPerCall;
    private readonly ToolInvocationPipeline? _toolPipeline;

    /// <param name="usageLimiter">The session limit, or <c>null</c>.</param>
    /// <param name="errorRecovery">Transient-failure retry, or <c>null</c>.</param>
    /// <param name="modelId">The model id that prices the usage.</param>
    /// <param name="chatClient">
    /// The loop's chat client. When its pipeline holds a <see cref="UsageLimitChatClient"/>, the limiter is bound to it:
    /// the limit is then checked and usage recorded on every model call — each tool round included — and not again here
    /// at the end of the turn.
    /// </param>
    public TurnGuards(IUsageLimiter? usageLimiter, IErrorRecoveryService? errorRecovery, string? modelId, IChatClient? chatClient = null)
    {
        _usageLimiter = usageLimiter;
        _errorRecovery = errorRecovery;
        _modelId = modelId;
        _toolPipeline = chatClient?.GetService<ToolInvocationPipeline>();
        if (usageLimiter is not null && chatClient?.GetService<UsageLimitChatClient>() is { } perCall)
        {
            perCall.Bind(usageLimiter, modelId);
            _recordedPerCall = true;
        }
    }

    /// <summary>
    /// Throws <see cref="UsageLimitExceededException"/> before the next model call when the session's
    /// token/cost limit has already been reached and <see cref="UsageLimitsConfig.StopOnLimit"/> is set.
    /// No-ops when no limiter is configured.
    /// </summary>
    public void ThrowIfUsageLimitExceeded()
    {
        var result = _usageLimiter?.CheckLimits();
        if (result is { ShouldStop: true })
        {
            throw new UsageLimitExceededException(result);
        }
    }

    /// <summary>
    /// Feeds a turn's token usage into the limiter, priced with the same <see cref="ModelCatalog"/> lookup
    /// <see cref="IUsageTracker"/> uses, so the next turn's check sees an up-to-date cumulative total.
    /// No-ops when no limiter is configured.
    /// </summary>
    public void RecordUsage(TokenUsage usage)
    {
        if (_usageLimiter is null || _recordedPerCall)
        {
            return;
        }

        Record(_usageLimiter, usage, _modelId);
    }

    /// <summary>Records <paramref name="usage"/> into <paramref name="limiter"/>, priced from the TokenMeter catalog.</summary>
    internal static void Record(IUsageLimiter limiter, TokenUsage usage, string? modelId)
    {
        var pricing = !string.IsNullOrEmpty(modelId) ? ModelCatalog.FindModel(modelId) : null;
        var cost = usage.CostAt(pricing) ?? 0m;

        limiter.RecordTokenUsage((int)usage.TotalTokens, cost);
    }

    /// <summary>
    /// Makes the turn's model request, and sends it once more after a failure the error recovery service says to retry —
    /// but only when no tool ran during the failed attempt. A function-invoking request runs tool rounds inside it, and
    /// sending it again replays them: a tool that writes, deletes or hands out the next item does it twice, and the model
    /// starts over against the changed state. A failure after a tool ran surfaces instead (a failure of one model call
    /// inside the request is retried by <c>UseTransientRetry()</c>, which sits inside function invocation).
    /// </summary>
    /// <param name="call">The request.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <param name="offersTools">
    /// The request offers tools. Whether one ran is read from the chat client's <see cref="ToolInvocationPipeline"/>; without
    /// one it cannot be told, and a request that offers tools is not retried.
    /// </param>
    public async Task<T> CallWithRecoveryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken, bool offersTools = false)
    {
        var toolRunsBefore = _toolPipeline?.InvocationCount;
        try
        {
            return await call(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (_errorRecovery is not null)
        {
            _errorRecovery.RecordError(ex);
            var analysis = _errorRecovery.AnalyzeException(ex);

            var replaySafe = !offersTools || (_toolPipeline is { } pipeline && pipeline.InvocationCount == toolRunsBefore);
            if (replaySafe
                && analysis.RecommendedAction is RecoveryAction.WaitAndRetry or RecoveryAction.Retry
                && analysis.RetryDelay is not null)
            {
                await Task.Delay(analysis.RetryDelay.Value, cancellationToken);
                return await call(cancellationToken);
            }

            throw;
        }
    }

    /// <summary>
    /// Adds a streamed update's usage to the turn's running total. A turn under function invocation makes several model
    /// calls and each reports its own usage, so the turn's usage is their sum — the same rule
    /// <see cref="ChatResponseExtensions.ToChatResponse(IEnumerable{ChatResponseUpdate})"/> applies. (It used to keep only
    /// the last one, so a streamed turn with a tool call reported just its final round-trip.)
    /// </summary>
    public static UsageDetails? AccumulateUsage(UsageDetails? total, ChatResponseUpdate update)
    {
        foreach (var usage in update.Contents.OfType<UsageContent>())
        {
            total ??= new UsageDetails();
            total.Add(usage.Details);
        }

        return total;
    }

    /// <summary>
    /// Records a failure raised while starting a streaming call. Streams are not retried: part of the
    /// turn may already have reached the consumer.
    /// </summary>
    public void RecordStreamingError(Exception ex) => _errorRecovery?.RecordError(ex);
}
