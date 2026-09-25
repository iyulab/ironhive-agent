using IronbeesEmbeddingProvider = global::Ironbees.Core.IEmbeddingProvider;
using IEmbeddingProvider = IronHive.Agent.Providers.IEmbeddingProvider;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Presents an IronHive.Agent <see cref="IEmbeddingProvider"/> (e.g. <c>OpenAICompatibleEmbeddingProvider</c>) as the
/// Ironbees embedding provider <see cref="IronbeesOptions.EmbeddingProvider"/> takes, so one configured provider serves
/// both. The wrapped provider is not disposed by the adapter.
/// </summary>
/// <param name="inner">The provider that computes the embeddings.</param>
/// <param name="modelName">The model name Ironbees reports and keys its caches on.</param>
public sealed class IronbeesEmbeddingProviderAdapter(IEmbeddingProvider inner, string modelName) : IronbeesEmbeddingProvider
{
    private readonly IEmbeddingProvider _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public string ModelName { get; } = string.IsNullOrWhiteSpace(modelName)
        ? throw new ArgumentException("A model name is required.", nameof(modelName))
        : modelName;

    /// <inheritdoc />
    public int Dimensions => _inner.Dimensions;

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => await _inner.EmbedAsync(text, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => await _inner.EmbedBatchAsync(texts, cancellationToken);
}
