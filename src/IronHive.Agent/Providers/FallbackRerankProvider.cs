namespace IronHive.Agent.Providers;

/// <summary>
/// Composite rerank provider with automatic fallback.
/// Tries providers in order until one succeeds.
/// </summary>
public sealed class FallbackRerankProvider : FallbackProviderBase<IRerankProvider>, IRerankProvider
{
    public FallbackRerankProvider(params IRerankProvider[] providers) : base(providers)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => ActiveProvider?.ProviderName ?? "fallback";

    /// <inheritdoc />
    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IEnumerable<string> documents,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await ActiveProvider!.RerankAsync(query, documents, topK, cancellationToken);
    }

    /// <summary>
    /// Gets the currently active provider.
    /// </summary>
    public new IRerankProvider? ActiveProvider => base.ActiveProvider;

    /// <inheritdoc />
    protected override bool IsProviderAvailable(IRerankProvider provider) => provider.IsAvailable;

    /// <inheritdoc />
    protected override async ValueTask<bool> TryInitializeProviderAsync(IRerankProvider provider, CancellationToken cancellationToken)
    {
        // Try a simple operation to verify the provider works
        await provider.RerankAsync("test", ["doc1", "doc2"], 1, cancellationToken);
        return true;
    }
}
