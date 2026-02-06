namespace IronHive.Agent.Providers;

/// <summary>
/// Provider interface for semantic reranking operations.
/// </summary>
public interface IRerankProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider name (e.g., "gpustack", "lmsupply").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets whether this provider is available and configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Reranks documents by relevance to a query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="documents">The documents to rerank.</param>
    /// <param name="topK">Maximum number of results to return. Null returns all documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Documents sorted by relevance score (highest first).</returns>
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IEnumerable<string> documents,
        int? topK = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a reranking operation.
/// </summary>
public record RerankResult
{
    /// <summary>
    /// Original index of the document in the input list.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// The document text.
    /// </summary>
    public required string Document { get; init; }

    /// <summary>
    /// Relevance score (0.0 to 1.0).
    /// </summary>
    public required float Score { get; init; }
}
