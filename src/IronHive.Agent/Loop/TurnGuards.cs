using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Exceptions;
using IronHive.Agent.Tracking;
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

    public TurnGuards(IUsageLimiter? usageLimiter, IErrorRecoveryService? errorRecovery, string? modelId)
    {
        _usageLimiter = usageLimiter;
        _errorRecovery = errorRecovery;
        _modelId = modelId;
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
        if (_usageLimiter is null)
        {
            return;
        }

        var pricing = !string.IsNullOrEmpty(_modelId) ? ModelCatalog.FindModel(_modelId) : null;
        var cost = pricing?.CalculateCost((int)usage.InputTokens, (int)usage.OutputTokens) ?? 0m;

        _usageLimiter.RecordTokenUsage((int)usage.TotalTokens, cost);
    }

    /// <summary>
    /// Runs a buffered model call, retrying it once after the recommended delay when error recovery
    /// classifies the failure as transient. Without an error-recovery service the call runs once.
    /// </summary>
    public async Task<T> CallWithRecoveryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
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

            if (analysis.RecommendedAction is RecoveryAction.WaitAndRetry or RecoveryAction.Retry
                && analysis.RetryDelay is not null)
            {
                await Task.Delay(analysis.RetryDelay.Value, cancellationToken);
                return await call(cancellationToken);
            }

            throw;
        }
    }

    /// <summary>
    /// Records a failure raised while starting a streaming call. Streams are not retried: part of the
    /// turn may already have reached the consumer.
    /// </summary>
    public void RecordStreamingError(Exception ex) => _errorRecovery?.RecordError(ex);
}
