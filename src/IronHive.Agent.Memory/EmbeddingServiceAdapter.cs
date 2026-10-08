using IronHive.Agent.Providers;
using MemoryIndexer.Interfaces;

namespace IronHive.Agent.Memory;

/// <summary>
/// Adapts an agent <see cref="IEmbeddingProvider"/> to MemoryIndexer's <see cref="IEmbeddingService"/>: stored memories
/// are embedded on the document side, memory searches on the query side.
/// </summary>
public class EmbeddingServiceAdapter : IEmbeddingService
{
    private readonly IEmbeddingProvider _provider;

    public EmbeddingServiceAdapter(IEmbeddingProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public int Dimensions => _provider.Dimensions;

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _provider.EmbedAsync(text, cancellationToken);
        return new ReadOnlyMemory<float>(embedding);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        var embeddings = await _provider.EmbedBatchAsync(texts.ToList(), cancellationToken);

        return embeddings
            .Select(e => new ReadOnlyMemory<float>(e))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> GenerateQueryEmbeddingAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _provider.EmbedQueryAsync(query, cancellationToken);
        return new ReadOnlyMemory<float>(embedding);
    }
}
