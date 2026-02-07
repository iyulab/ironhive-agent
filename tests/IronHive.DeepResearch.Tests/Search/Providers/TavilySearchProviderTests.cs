using System.Net;
using System.Text.Json;
using FluentAssertions;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Search.Caching;
using IronHive.DeepResearch.Search.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Search.Providers;

public class TavilySearchProviderTests
{
    private readonly Mock<ISearchResultCache> _mockCache;
    private readonly DeepResearchOptions _options;

    public TavilySearchProviderTests()
    {
        _mockCache = new Mock<ISearchResultCache>();
        _mockCache.Setup(c => c.GenerateKey(It.IsAny<SearchQuery>()))
            .Returns<SearchQuery>(q => $"test-key-{q.Query.GetHashCode()}");
        _mockCache.Setup(c => c.TryGet(It.IsAny<string>(), out It.Ref<SearchResult?>.IsAny))
            .Returns(false);

        _options = new DeepResearchOptions
        {
            DefaultSearchProvider = "tavily",
            SearchApiKeys = new Dictionary<string, string>
            {
                ["tavily"] = "test-api-key"
            }
        };
    }

    [Fact]
    public void ProviderId_ReturnsTavily()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(CreateSuccessResponse());
        var provider = CreateProvider(httpClient);

        // Assert
        provider.ProviderId.Should().Be("tavily");
    }

    [Fact]
    public void Capabilities_IncludesExpectedFlags()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(CreateSuccessResponse());
        var provider = CreateProvider(httpClient);

        // Assert
        provider.Capabilities.Should().HaveFlag(
            IronHive.DeepResearch.Abstractions.SearchCapabilities.WebSearch);
        provider.Capabilities.Should().HaveFlag(
            IronHive.DeepResearch.Abstractions.SearchCapabilities.NewsSearch);
        provider.Capabilities.Should().HaveFlag(
            IronHive.DeepResearch.Abstractions.SearchCapabilities.ContentExtraction);
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_ReturnsResults()
    {
        // Arrange
        var response = CreateSuccessResponse();
        var httpClient = CreateMockHttpClient(response);
        var provider = CreateProvider(httpClient);

        var query = new SearchQuery
        {
            Query = "test query",
            MaxResults = 5
        };

        // Act
        var result = await provider.SearchAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Provider.Should().Be("tavily");
        result.Query.Should().Be(query);
        result.Sources.Should().HaveCount(2);
        result.Answer.Should().Be("This is a test answer");
    }

    [Fact]
    public async Task SearchAsync_CacheHit_ReturnsCachedResult()
    {
        // Arrange
        var cachedResult = new SearchResult
        {
            Query = new SearchQuery { Query = "test" },
            Provider = "tavily",
            Sources = [],
            SearchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Answer = "cached answer"
        };

        _mockCache.Setup(c => c.TryGet(It.IsAny<string>(), out cachedResult))
            .Returns(true);

        var httpClient = CreateMockHttpClient(CreateSuccessResponse());
        var provider = CreateProvider(httpClient);

        var query = new SearchQuery { Query = "test" };

        // Act
        var result = await provider.SearchAsync(query);

        // Assert
        result.Should().Be(cachedResult);
        result.Answer.Should().Be("cached answer");
    }

    [Fact]
    public async Task SearchAsync_SetsCache_AfterSuccessfulSearch()
    {
        // Arrange
        var response = CreateSuccessResponse();
        var httpClient = CreateMockHttpClient(response);
        var provider = CreateProvider(httpClient);

        var query = new SearchQuery { Query = "test" };

        // Act
        await provider.SearchAsync(query);

        // Assert
        _mockCache.Verify(c => c.Set(
            It.IsAny<string>(),
            It.IsAny<SearchResult>(),
            It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_DeepQuery_SetsAdvancedDepth()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateSuccessResponse())
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com") };
        var provider = CreateProvider(httpClient);

        var query = new SearchQuery
        {
            Query = "test",
            Depth = QueryDepth.Deep
        };

        // Act
        await provider.SearchAsync(query);

        // Assert
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("\"search_depth\":\"advanced\"");
    }

    [Fact]
    public async Task SearchAsync_ApiError_ThrowsHttpRequestException()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\": \"Invalid API key\"}")
            }));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com") };
        var provider = CreateProvider(httpClient);

        var query = new SearchQuery { Query = "test" };

        // Act
        var act = () => provider.SearchAsync(query);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*Unauthorized*");
    }

    [Fact]
    public async Task SearchBatchAsync_MultipleQueries_ReturnsAllResults()
    {
        // Arrange
        var response = CreateSuccessResponse();
        var httpClient = CreateMockHttpClient(response);
        var provider = CreateProvider(httpClient);

        var queries = new[]
        {
            new SearchQuery { Query = "query 1" },
            new SearchQuery { Query = "query 2" },
            new SearchQuery { Query = "query 3" }
        };

        // Act
        var results = await provider.SearchBatchAsync(queries);

        // Assert
        results.Should().HaveCount(3);
        results.All(r => r.Provider == "tavily").Should().BeTrue();
    }

    [Fact]
    public async Task SearchBatchAsync_PartialFailure_ReturnsEmptyResultForFailed()
    {
        // Arrange
        var callCount = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\": \"Server error\"}")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateSuccessResponse())
            });
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com") };
        var provider = CreateProvider(httpClient);

        var queries = new[]
        {
            new SearchQuery { Query = "query 1" },
            new SearchQuery { Query = "query 2" },
            new SearchQuery { Query = "query 3" }
        };

        // Act
        var results = await provider.SearchBatchAsync(queries);

        // Assert
        results.Should().HaveCount(3);
        results.Count(r => r.Sources.Count > 0).Should().Be(2);
        results.Count(r => r.Sources.Count == 0).Should().Be(1);
    }

    private TavilySearchProvider CreateProvider(HttpClient httpClient)
    {
        return new TavilySearchProvider(
            httpClient,
            _mockCache.Object,
            _options,
            NullLogger<TavilySearchProvider>.Instance);
    }

    private static HttpClient CreateMockHttpClient(string responseJson)
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            }));

        return new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com") };
    }

    private static string CreateSuccessResponse()
    {
        return JsonSerializer.Serialize(new
        {
            query = "test query",
            answer = "This is a test answer",
            results = new[]
            {
                new
                {
                    url = "https://example.com/1",
                    title = "Result 1",
                    content = "Content snippet 1",
                    score = 0.95
                },
                new
                {
                    url = "https://example.com/2",
                    title = "Result 2",
                    content = "Content snippet 2",
                    score = 0.85
                }
            },
            response_time = 0.5
        });
    }
}

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return _handler(request);
    }
}
