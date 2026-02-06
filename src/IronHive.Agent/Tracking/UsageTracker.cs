using IronHive.Agent.Loop;
using TokenMeter;

namespace IronHive.Agent.Tracking;

/// <summary>
/// Tracks token usage across a session.
/// </summary>
public interface IUsageTracker
{
    /// <summary>
    /// Sets the current model ID for accurate pricing.
    /// </summary>
    void SetModel(string modelId);

    /// <summary>
    /// Records token usage from a request.
    /// </summary>
    void Record(TokenUsage usage);

    /// <summary>
    /// Gets the cumulative usage for the session.
    /// </summary>
    SessionUsage GetSessionUsage();

    /// <summary>
    /// Resets the session statistics.
    /// </summary>
    void Reset();
}

/// <summary>
/// Session-level usage statistics.
/// </summary>
public record SessionUsage
{
    /// <summary>
    /// Total input tokens used in the session.
    /// </summary>
    public long TotalInputTokens { get; init; }

    /// <summary>
    /// Total output tokens used in the session.
    /// </summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>
    /// Total tokens used in the session.
    /// </summary>
    public long TotalTokens => TotalInputTokens + TotalOutputTokens;

    /// <summary>
    /// Number of requests made in the session.
    /// </summary>
    public int RequestCount { get; init; }

    /// <summary>
    /// Average tokens per request.
    /// </summary>
    public double AverageTokensPerRequest => RequestCount > 0 ? (double)TotalTokens / RequestCount : 0;

    /// <summary>
    /// Estimated cost in USD based on model pricing.
    /// Uses TokenMeter pricing data for accurate calculations.
    /// </summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>
    /// The model ID used for pricing calculation.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// The pricing information used for cost calculation.
    /// Null if the model is not found in TokenMeter pricing data.
    /// </summary>
    public ModelPricing? Pricing { get; init; }
}

/// <summary>
/// Default implementation of usage tracker.
/// Uses TokenMeter for accurate model-specific pricing.
/// </summary>
public class UsageTracker : IUsageTracker
{
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private int _requestCount;
    private string? _modelId;
    private ModelPricing? _pricing;
    private readonly object _lock = new();

    // Default fallback pricing (GPT-4o-mini style) when model is unknown
    private static readonly ModelPricing DefaultPricing = new()
    {
        ModelId = "unknown",
        InputPricePerMillion = 0.15m,
        OutputPricePerMillion = 0.60m,
        Provider = "Unknown",
        DisplayName = "Unknown Model"
    };

    /// <inheritdoc />
    public void SetModel(string modelId)
    {
        lock (_lock)
        {
            _modelId = modelId;
            _pricing = ModelPricingData.FindPricing(modelId);
        }
    }

    /// <inheritdoc />
    public void Record(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        lock (_lock)
        {
            _totalInputTokens += usage.InputTokens;
            _totalOutputTokens += usage.OutputTokens;
            _requestCount++;
        }
    }

    /// <inheritdoc />
    public SessionUsage GetSessionUsage()
    {
        lock (_lock)
        {
            var pricing = _pricing ?? DefaultPricing;
            var cost = pricing.CalculateCost((int)_totalInputTokens, (int)_totalOutputTokens);

            return new SessionUsage
            {
                TotalInputTokens = _totalInputTokens,
                TotalOutputTokens = _totalOutputTokens,
                RequestCount = _requestCount,
                EstimatedCostUsd = cost,
                ModelId = _modelId,
                Pricing = _pricing
            };
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_lock)
        {
            _totalInputTokens = 0;
            _totalOutputTokens = 0;
            _requestCount = 0;
            // Keep model and pricing settings
        }
    }
}
