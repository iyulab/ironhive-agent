namespace IronHive.Agent.Providers;

/// <summary>
/// Composite embedding provider with automatic fallback.
/// Tries providers in order until one succeeds.
/// </summary>
public sealed class FallbackEmbeddingProvider : FallbackProviderBase<IEmbeddingProvider>, IEmbeddingProvider
{
    public FallbackEmbeddingProvider(params IEmbeddingProvider[] providers) : base(providers)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => ActiveProvider?.ProviderName ?? "fallback";

    /// <inheritdoc />
    public int Dimensions => ActiveProvider?.Dimensions ?? 0;

    /// <inheritdoc />
    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await ActiveProvider!.EmbedAsync(text, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await ActiveProvider!.EmbedBatchAsync(texts, cancellationToken);
    }

    /// <summary>
    /// Gets the currently active provider.
    /// </summary>
    public new IEmbeddingProvider? ActiveProvider => base.ActiveProvider;

    /// <inheritdoc />
    protected override bool IsProviderAvailable(IEmbeddingProvider provider) => provider.IsAvailable;

    /// <inheritdoc />
    protected override async ValueTask<bool> TryInitializeProviderAsync(IEmbeddingProvider provider, CancellationToken cancellationToken)
    {
        // Try a simple operation to verify the provider works
        await provider.EmbedAsync("test", cancellationToken);
        return true;
    }
}
