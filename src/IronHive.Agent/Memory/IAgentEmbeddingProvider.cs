namespace IronHive.Agent.Memory;

/// <summary>
/// Interface for embedding providers used by the agent layer.
/// </summary>
public interface IAgentEmbeddingProvider
{
    /// <summary>
    /// Gets the dimension of the embedding vectors.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Generates an embedding for the given text.
    /// </summary>
    /// <param name="text">The text to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The embedding vector</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple texts.
    /// </summary>
    /// <param name="texts">The texts to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The embedding vectors</returns>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
}
