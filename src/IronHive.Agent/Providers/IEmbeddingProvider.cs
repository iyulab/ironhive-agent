namespace IronHive.Agent.Providers;

/// <summary>
/// Provider interface for text embedding operations.
/// </summary>
public interface IEmbeddingProvider : IAsyncDisposable
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
    /// Gets the embedding dimension.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Generates an embedding for a single text.
    /// </summary>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple texts in batch.
    /// </summary>
    ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
