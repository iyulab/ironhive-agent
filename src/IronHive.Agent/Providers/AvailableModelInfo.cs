namespace IronHive.Agent.Providers;

/// <summary>
/// Information about an available model.
/// </summary>
public record AvailableModelInfo
{
    /// <summary>
    /// Model identifier (e.g., "gpt-4o", "claude-sonnet-4-20250514").
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    /// Provider name (e.g., "openai", "anthropic", "gpustack").
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Maximum context window size in tokens.
    /// </summary>
    public int? ContextWindow { get; init; }

    /// <summary>
    /// Input price per million tokens (USD).
    /// </summary>
    public decimal? InputPricePerMillion { get; init; }

    /// <summary>
    /// Output price per million tokens (USD).
    /// </summary>
    public decimal? OutputPricePerMillion { get; init; }

    /// <summary>
    /// Source of the model information.
    /// </summary>
    public ModelSource Source { get; init; } = ModelSource.Api;

    /// <summary>
    /// Whether this is the default model for the provider.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Local file path for cached models.
    /// </summary>
    public string? LocalPath { get; init; }

    /// <summary>
    /// Model file size in bytes for cached models.
    /// </summary>
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Source of model information.
/// </summary>
public enum ModelSource
{
    /// <summary>
    /// Model information retrieved from API.
    /// </summary>
    Api,

    /// <summary>
    /// Model cached locally.
    /// </summary>
    Cached,

    /// <summary>
    /// Static model list from pricing data.
    /// </summary>
    Static
}
