namespace IronHive.Agent.Providers;

/// <summary>
/// Provider interface for text embedding operations.
/// </summary>
/// <remarks>
/// <para>
/// Two roles reach an embedder: the text that is <b>compared against</b> — a tool description, a stored memory, a
/// document (<see cref="EmbedAsync"/> and <see cref="EmbedBatchAsync"/>) — and the <b>query</b> compared against it
/// (<see cref="EmbedQueryAsync"/>). Every search in this library embeds its query through the query method.
/// </para>
/// <para>
/// A symmetric model embeds both the same way and needs nothing: the query method defaults to <see cref="EmbedAsync"/>.
/// An asymmetric model (E5 <c>query: </c>/<c>passage: </c>, Nomic, BGE's query instruction) implements it with its query
/// convention, and applies its document convention in <see cref="EmbedAsync"/>. A provider that wraps another one
/// forwards all three.
/// </para>
/// </remarks>
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
    /// Generates an embedding for a single text that queries are compared against (a document, a tool description,
    /// a stored memory).
    /// </summary>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple texts in batch, in the same role as <see cref="EmbedAsync"/>.
    /// </summary>
    ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an embedding for a search query, to be compared against vectors from <see cref="EmbedAsync"/>.
    /// Defaults to <see cref="EmbedAsync"/> (a symmetric model); an asymmetric model overrides it.
    /// </summary>
    ValueTask<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) =>
        EmbedAsync(query, cancellationToken);
}
