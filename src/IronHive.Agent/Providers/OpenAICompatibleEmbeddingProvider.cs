using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IronbeesEmbeddingProvider = global::Ironbees.Core.IEmbeddingProvider;

namespace IronHive.Agent.Providers;

/// <summary>
/// OpenAI-compatible embedding provider that implements both the ironhive-agent
/// <see cref="IEmbeddingProvider"/> and the ironbees <see cref="IronbeesEmbeddingProvider"/>
/// interfaces. Works with any OpenAI-compatible API endpoint (GPUStack, vLLM, Ollama, etc.).
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider
    : IEmbeddingProvider, IronbeesEmbeddingProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly bool _ownClient;
    private bool _disposed;

    /// <summary>
    /// Creates a new instance of the OpenAI-compatible embedding provider.
    /// </summary>
    /// <param name="endpoint">The base URL of the OpenAI-compatible API (e.g., http://172.19.10.10/v1).</param>
    /// <param name="model">The embedding model name (e.g., qwen3-embedding-0.6b).</param>
    /// <param name="dimensions">The dimensionality of the embedding vectors.</param>
    /// <param name="apiKey">Optional API key for Bearer token authentication.</param>
    /// <param name="httpClient">
    /// Optional pre-configured HttpClient. When provided, the caller is responsible for its lifetime.
    /// When null, an internal HttpClient is created and disposed with this instance.
    /// </param>
    public OpenAICompatibleEmbeddingProvider(
        string endpoint,
        string model,
        int dimensions,
        string? apiKey = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        _model = model;
        Dimensions = dimensions;
        _ownClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    // --- IronHive.Agent.Providers.IEmbeddingProvider ---

    /// <inheritdoc />
    public string ProviderName => "openai-compatible";

    /// <inheritdoc />
    public bool IsAvailable => _httpClient.BaseAddress is not null;

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public async ValueTask<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchCoreAsync([text], cancellationToken);
        return results[0];
    }

    /// <inheritdoc />
    public async ValueTask<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        return await EmbedBatchCoreAsync(texts, cancellationToken);
    }

    // --- Ironbees.Core.IEmbeddingProvider ---

    /// <inheritdoc />
    public string ModelName => _model;

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchCoreAsync([text], cancellationToken);
        return results[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        return await EmbedBatchCoreAsync(texts, cancellationToken);
    }

    // --- IAsyncDisposable + IDisposable ---

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownClient)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }

    // --- Core implementation ---

    private async Task<float[][]> EmbedBatchCoreAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var request = new EmbeddingRequest(_model, texts);
        var response = await _httpClient.PostAsJsonAsync("embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
        return result!.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToArray();
    }

    // --- DTOs for OpenAI embeddings API ---

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] List<EmbeddingData> Data);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
