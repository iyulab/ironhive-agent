using IronHive.Agent.Webhook;

namespace IronHive.Agent.Tracking;

/// <summary>
/// Limit status.
/// </summary>
public enum LimitStatus
{
    /// <summary>Under limit, normal operation.</summary>
    Normal,

    /// <summary>Approaching limit, warning issued.</summary>
    Warning,

    /// <summary>At or over limit.</summary>
    Exceeded
}

/// <summary>
/// Usage limit check result.
/// </summary>
public record UsageLimitResult
{
    /// <summary>
    /// Current token usage.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Token limit (0 = unlimited).
    /// </summary>
    public int TokenLimit { get; init; }

    /// <summary>
    /// Current cost in USD.
    /// </summary>
    public decimal CostUsed { get; init; }

    /// <summary>
    /// Cost limit in USD (0 = unlimited).
    /// </summary>
    public decimal CostLimit { get; init; }

    /// <summary>
    /// Token limit status.
    /// </summary>
    public LimitStatus TokenStatus { get; init; }

    /// <summary>
    /// Cost limit status.
    /// </summary>
    public LimitStatus CostStatus { get; init; }

    /// <summary>
    /// Whether execution should stop.
    /// </summary>
    public bool ShouldStop { get; init; }

    /// <summary>
    /// Token usage percentage (0.0 - 1.0, or null if unlimited).
    /// </summary>
    public float? TokenPercentage => TokenLimit > 0 ? (float)TokensUsed / TokenLimit : null;

    /// <summary>
    /// Cost usage percentage (0.0 - 1.0, or null if unlimited).
    /// </summary>
    public float? CostPercentage => CostLimit > 0 ? (float)(CostUsed / CostLimit) : null;

    /// <summary>
    /// Human-readable message about usage.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Configuration for usage limits.
/// </summary>
public class UsageLimitsConfig
{
    /// <summary>
    /// Maximum tokens per session (0 = unlimited).
    /// </summary>
    public int MaxSessionTokens { get; set; }

    /// <summary>
    /// Maximum cost per session in USD (0 = unlimited).
    /// </summary>
    public decimal MaxSessionCost { get; set; }

    /// <summary>
    /// Warning threshold as percentage of limit (0.0 - 1.0).
    /// </summary>
    public float WarningThreshold { get; set; } = 0.8f;

    /// <summary>
    /// Whether to stop execution when limit is reached.
    /// </summary>
    public bool StopOnLimit { get; set; } = true;
}

/// <summary>
/// Service for enforcing token and cost limits.
/// </summary>
public interface IUsageLimiter
{
    /// <summary>
    /// Checks current usage against limits.
    /// </summary>
    UsageLimitResult CheckLimits();

    /// <summary>
    /// Records token usage.
    /// </summary>
    void RecordTokenUsage(int tokens, decimal cost);

    /// <summary>
    /// Gets current usage.
    /// </summary>
    (int Tokens, decimal Cost) GetCurrentUsage();

    /// <summary>
    /// Resets usage counters.
    /// </summary>
    void Reset();
}

/// <summary>
/// Implementation of usage limiter.
/// </summary>
public class UsageLimiter : IUsageLimiter
{
    private readonly UsageLimitsConfig _config;
    private readonly IWebhookService? _webhookService;
    private int _tokensUsed;
    private decimal _costUsed;
    private bool _warningTokenSent;
    private bool _warningCostSent;

    public UsageLimiter(UsageLimitsConfig config, IWebhookService? webhookService = null)
    {
        _config = config;
        _webhookService = webhookService;
    }

    /// <inheritdoc />
    public void RecordTokenUsage(int tokens, decimal cost)
    {
        _tokensUsed += tokens;
        _costUsed += cost;
    }

    /// <inheritdoc />
    public (int Tokens, decimal Cost) GetCurrentUsage()
    {
        return (_tokensUsed, _costUsed);
    }

    /// <inheritdoc />
    public UsageLimitResult CheckLimits()
    {
        var tokenStatus = GetTokenStatus();
        var costStatus = GetCostStatus();

        var shouldStop = _config.StopOnLimit &&
            (tokenStatus == LimitStatus.Exceeded || costStatus == LimitStatus.Exceeded);

        var message = BuildMessage(tokenStatus, costStatus);

        // Send webhook notifications
        SendNotificationsIfNeeded(tokenStatus, costStatus);

        return new UsageLimitResult
        {
            TokensUsed = _tokensUsed,
            TokenLimit = _config.MaxSessionTokens,
            CostUsed = _costUsed,
            CostLimit = _config.MaxSessionCost,
            TokenStatus = tokenStatus,
            CostStatus = costStatus,
            ShouldStop = shouldStop,
            Message = message
        };
    }

    /// <inheritdoc />
    public void Reset()
    {
        _tokensUsed = 0;
        _costUsed = 0;
        _warningTokenSent = false;
        _warningCostSent = false;
    }

    private LimitStatus GetTokenStatus()
    {
        if (_config.MaxSessionTokens <= 0)
        {
            return LimitStatus.Normal;
        }

        var percentage = (float)_tokensUsed / _config.MaxSessionTokens;

        if (percentage >= 1.0f)
        {
            return LimitStatus.Exceeded;
        }

        if (percentage >= _config.WarningThreshold)
        {
            return LimitStatus.Warning;
        }

        return LimitStatus.Normal;
    }

    private LimitStatus GetCostStatus()
    {
        if (_config.MaxSessionCost <= 0)
        {
            return LimitStatus.Normal;
        }

        var percentage = (float)(_costUsed / _config.MaxSessionCost);

        if (percentage >= 1.0f)
        {
            return LimitStatus.Exceeded;
        }

        if (percentage >= _config.WarningThreshold)
        {
            return LimitStatus.Warning;
        }

        return LimitStatus.Normal;
    }

    private string BuildMessage(LimitStatus tokenStatus, LimitStatus costStatus)
    {
        var parts = new List<string>();

        if (tokenStatus == LimitStatus.Exceeded)
        {
            parts.Add($"Token limit exceeded ({_tokensUsed:N0}/{_config.MaxSessionTokens:N0})");
        }
        else if (tokenStatus == LimitStatus.Warning && _config.MaxSessionTokens > 0)
        {
            var pct = (float)_tokensUsed / _config.MaxSessionTokens * 100;
            parts.Add($"Token usage at {pct:F1}% ({_tokensUsed:N0}/{_config.MaxSessionTokens:N0})");
        }

        if (costStatus == LimitStatus.Exceeded)
        {
            parts.Add($"Cost limit exceeded (${_costUsed:F4}/${_config.MaxSessionCost:F4})");
        }
        else if (costStatus == LimitStatus.Warning && _config.MaxSessionCost > 0)
        {
            var pct = (float)(_costUsed / _config.MaxSessionCost) * 100;
            parts.Add($"Cost usage at {pct:F1}% (${_costUsed:F4}/${_config.MaxSessionCost:F4})");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "Usage within limits";
    }

    private void SendNotificationsIfNeeded(LimitStatus tokenStatus, LimitStatus costStatus)
    {
        if (_webhookService == null || !_webhookService.IsConfigured)
        {
            return;
        }

        // Token warning
        if (tokenStatus == LimitStatus.Warning && !_warningTokenSent)
        {
            _warningTokenSent = true;
            _ = _webhookService.SendAsync(
                WebhookEventType.TokenLimitWarning,
                data: new Dictionary<string, object?>
                {
                    ["tokensUsed"] = _tokensUsed,
                    ["tokenLimit"] = _config.MaxSessionTokens,
                    ["percentage"] = (float)_tokensUsed / _config.MaxSessionTokens
                });
        }

        // Cost warning
        if (costStatus == LimitStatus.Warning && !_warningCostSent)
        {
            _warningCostSent = true;
            _ = _webhookService.SendAsync(
                WebhookEventType.CostLimitWarning,
                data: new Dictionary<string, object?>
                {
                    ["costUsed"] = _costUsed,
                    ["costLimit"] = _config.MaxSessionCost,
                    ["percentage"] = (float)(_costUsed / _config.MaxSessionCost)
                });
        }
    }
}
