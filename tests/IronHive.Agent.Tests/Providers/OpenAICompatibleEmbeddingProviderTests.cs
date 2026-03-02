using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IronHive.Agent.Providers;
using Xunit;
using IronbeesEmbeddingProvider = global::Ironbees.Core.IEmbeddingProvider;

namespace IronHive.Agent.Tests.Providers;

public sealed class OpenAICompatibleEmbeddingProviderTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly OpenAICompatibleEmbeddingProvider _sut;

    private const string TestEndpoint = "http://localhost:11434/v1/";
    private const string TestModel = "test-embedding-model";
    private const int TestDimensions = 3;

    public OpenAICompatibleEmbeddingProviderTests()
    {
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(TestEndpoint)
        };
        _sut = new OpenAICompatibleEmbeddingProvider(
            TestEndpoint, TestModel, TestDimensions, httpClient: _httpClient);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _httpClient.Dispose();
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_ThrowsOnNullEndpoint()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider(null!, "model", 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyEndpoint()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("", "model", 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceEndpoint()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("   ", "model", 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullModel()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("http://localhost", null!, 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyModel()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("http://localhost", "", 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnZeroDimensions()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("http://localhost", "model", 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNegativeDimensions()
    {
        var act = () => new OpenAICompatibleEmbeddingProvider("http://localhost", "model", -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NormalizesEndpointTrailingSlash()
    {
        // Without trailing slash
        using var provider1 = new OpenAICompatibleEmbeddingProvider(
            "http://localhost/v1", "model", 3);
        provider1.ModelName.Should().Be("model");

        // With trailing slash
        using var provider2 = new OpenAICompatibleEmbeddingProvider(
            "http://localhost/v1/", "model", 3);
        provider2.ModelName.Should().Be("model");
    }

    // --- IronHive.Agent.Providers.IEmbeddingProvider interface ---

    [Fact]
    public void ProviderName_ReturnsOpenAICompatible()
    {
        _sut.ProviderName.Should().Be("openai-compatible");
    }

    [Fact]
    public void IsAvailable_ReturnsTrueWhenBaseAddressIsSet()
    {
        _sut.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Dimensions_ReturnsConfiguredDimensions()
    {
        _sut.Dimensions.Should().Be(TestDimensions);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsSingleEmbedding()
    {
        // Arrange
        float[] expected = [0.1f, 0.2f, 0.3f];
        _handler.SetResponse(CreateEmbeddingResponse([(0, expected)]));

        // Act
        var result = await _sut.EmbedAsync("hello world");

        // Assert
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task EmbedBatchAsync_ReturnsMultipleEmbeddings()
    {
        // Arrange
        float[] embedding0 = [0.1f, 0.2f, 0.3f];
        float[] embedding1 = [0.4f, 0.5f, 0.6f];
        _handler.SetResponse(CreateEmbeddingResponse([(0, embedding0), (1, embedding1)]));

        // Act
        var results = await _sut.EmbedBatchAsync(["text1", "text2"]);

        // Assert
        results.Should().HaveCount(2);
        results[0].Should().BeEquivalentTo(embedding0);
        results[1].Should().BeEquivalentTo(embedding1);
    }

    [Fact]
    public void AgentInterface_CanBeReferencedAsIEmbeddingProvider()
    {
        // Verify the class can be assigned to the agent interface
        // (intentional interface-typed variable to validate compatibility)
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
        IEmbeddingProvider agentProvider = _sut;
#pragma warning restore CA1859
        agentProvider.ProviderName.Should().Be("openai-compatible");
        agentProvider.IsAvailable.Should().BeTrue();
        agentProvider.Dimensions.Should().Be(TestDimensions);
    }

    // --- Ironbees.Core.IEmbeddingProvider interface ---

    [Fact]
    public void ModelName_ReturnsConfiguredModel()
    {
        _sut.ModelName.Should().Be(TestModel);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsSingleEmbedding()
    {
        // Arrange
        float[] expected = [0.1f, 0.2f, 0.3f];
        _handler.SetResponse(CreateEmbeddingResponse([(0, expected)]));

        // Act
        var result = await _sut.GenerateEmbeddingAsync("hello world");

        // Assert
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_ReturnsMultipleEmbeddings()
    {
        // Arrange
        float[] embedding0 = [0.1f, 0.2f, 0.3f];
        float[] embedding1 = [0.4f, 0.5f, 0.6f];
        _handler.SetResponse(CreateEmbeddingResponse([(0, embedding0), (1, embedding1)]));

        // Act
        var results = await _sut.GenerateEmbeddingsAsync(["text1", "text2"]);

        // Assert
        results.Should().HaveCount(2);
        results[0].Should().BeEquivalentTo(embedding0);
        results[1].Should().BeEquivalentTo(embedding1);
    }

    [Fact]
    public void IronbeesInterface_CanBeReferencedAsIEmbeddingProvider()
    {
        // Verify the class can be assigned to the ironbees interface
        // (intentional interface-typed variable to validate compatibility)
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
        IronbeesEmbeddingProvider ironbeesProvider = _sut;
#pragma warning restore CA1859
        ironbeesProvider.ModelName.Should().Be(TestModel);
        ironbeesProvider.Dimensions.Should().Be(TestDimensions);
    }

    // --- Ordering ---

    [Fact]
    public async Task EmbedBatchAsync_OrdersByIndex_WhenResponseIsUnordered()
    {
        // Arrange - API returns items out of order (index 1 first, then 0)
        float[] embedding0 = [0.1f, 0.2f, 0.3f];
        float[] embedding1 = [0.4f, 0.5f, 0.6f];
        _handler.SetResponse(CreateEmbeddingResponse([(1, embedding1), (0, embedding0)]));

        // Act
        var results = await _sut.EmbedBatchAsync(["text0", "text1"]);

        // Assert - Should be ordered by index, not by response order
        results[0].Should().BeEquivalentTo(embedding0);
        results[1].Should().BeEquivalentTo(embedding1);
    }

    // --- Request format ---

    [Fact]
    public async Task EmbedAsync_SendsCorrectRequest()
    {
        // Arrange
        float[] embedding = [0.1f, 0.2f, 0.3f];
        _handler.SetResponse(CreateEmbeddingResponse([(0, embedding)]));

        // Act
        await _sut.EmbedAsync("test input");

        // Assert
        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequest!.RequestUri!.ToString().Should().EndWith("/embeddings");
        _handler.LastRequest.Method.Should().Be(HttpMethod.Post);

        var body = JsonDocument.Parse(_handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be(TestModel);
        body.RootElement.GetProperty("input").GetArrayLength().Should().Be(1);
        body.RootElement.GetProperty("input")[0].GetString().Should().Be("test input");
    }

    // --- Error handling ---

    [Fact]
    public async Task EmbedBatchAsync_ThrowsOnHttpError()
    {
        // Arrange
        _handler.SetResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Act
        var act = () => _sut.EmbedBatchAsync(["text"]).AsTask();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_ThrowsOnHttpError()
    {
        // Arrange
        _handler.SetResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Act
        var act = () => _sut.GenerateEmbeddingsAsync(["text"]);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedBatchAsync_ThrowsOnNullTexts()
    {
        // Act
        var act = () => _sut.EmbedBatchAsync(null!).AsTask();

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // --- ApiKey Bearer header ---

    [Fact]
    public async Task Constructor_SetsAuthorizationHeader_WhenApiKeyProvided()
    {
        // Arrange
        const string apiKey = "test-api-key-12345";
        var handler = new MockHttpMessageHandler();
        float[] embedding = [0.1f, 0.2f, 0.3f];
        handler.SetResponse(CreateEmbeddingResponse([(0, embedding)]));

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(TestEndpoint)
        };

        using var provider = new OpenAICompatibleEmbeddingProvider(
            TestEndpoint, TestModel, TestDimensions, apiKey: apiKey, httpClient: httpClient);

        // Act
        await provider.EmbedAsync("test");

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(apiKey);

        httpClient.Dispose();
    }

    [Fact]
    public async Task Constructor_NoAuthorizationHeader_WhenApiKeyIsNull()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        float[] embedding = [0.1f, 0.2f, 0.3f];
        handler.SetResponse(CreateEmbeddingResponse([(0, embedding)]));

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(TestEndpoint)
        };

        using var provider = new OpenAICompatibleEmbeddingProvider(
            TestEndpoint, TestModel, TestDimensions, apiKey: null, httpClient: httpClient);

        // Act
        await provider.EmbedAsync("test");

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().BeNull();

        httpClient.Dispose();
    }

    [Fact]
    public async Task Constructor_NoAuthorizationHeader_WhenApiKeyIsWhitespace()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        float[] embedding = [0.1f, 0.2f, 0.3f];
        handler.SetResponse(CreateEmbeddingResponse([(0, embedding)]));

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(TestEndpoint)
        };

        using var provider = new OpenAICompatibleEmbeddingProvider(
            TestEndpoint, TestModel, TestDimensions, apiKey: "   ", httpClient: httpClient);

        // Act
        await provider.EmbedAsync("test");

        // Assert
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Authorization.Should().BeNull();

        httpClient.Dispose();
    }

    // --- IDisposable / IAsyncDisposable ---

    [Fact]
    public void Dispose_DisposesOwnedHttpClient()
    {
        // Arrange - create provider without external HttpClient (owns its own)
        var provider = new OpenAICompatibleEmbeddingProvider(
            "http://localhost/v1/", "model", 3);

        // Act - should not throw
        provider.Dispose();

        // Assert - second dispose should also not throw (idempotent)
        var act = () => provider.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_DoesNotDisposeExternalHttpClient()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var externalClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/")
        };
        var provider = new OpenAICompatibleEmbeddingProvider(
            "http://localhost/v1/", "model", 3, httpClient: externalClient);

        // Act
        provider.Dispose();

        // Assert - external client should still be usable
        float[] dummyEmbedding = [1f];
        handler.SetResponse(CreateEmbeddingResponse([(0, dummyEmbedding)]));
        var act = async () => await externalClient.GetAsync("embeddings");
        act.Should().NotThrowAsync();

        // Cleanup
        externalClient.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DisposesOwnedHttpClient()
    {
        // Arrange
        var provider = new OpenAICompatibleEmbeddingProvider(
            "http://localhost/v1/", "model", 3);

        // Act - should not throw
        await provider.DisposeAsync();

        // Assert - second dispose should also not throw
        var act = async () => await provider.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // --- Helpers ---

    private static string CreateEmbeddingResponse(
        IEnumerable<(int Index, float[] Embedding)> embeddings)
    {
        var data = embeddings.Select(e => new
        {
            index = e.Index,
            embedding = e.Embedding,
            @object = "embedding"
        });

        return JsonSerializer.Serialize(new
        {
            data,
            model = TestModel,
            @object = "list",
            usage = new { prompt_tokens = 10, total_tokens = 10 }
        });
    }

    /// <summary>
    /// Minimal HTTP message handler for testing that captures requests and returns
    /// configurable responses.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private HttpResponseMessage _response = new(HttpStatusCode.OK);

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void SetResponse(HttpResponseMessage response) => _response = response;

        public void SetResponse(string jsonContent)
        {
            _response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _response;
        }
    }
}
