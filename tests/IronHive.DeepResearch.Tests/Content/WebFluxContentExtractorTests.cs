using System.Net;
using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Content;

public class WebFluxContentExtractorTests
{
    private readonly ContentProcessor _processor;
    private readonly DeepResearchOptions _options;

    public WebFluxContentExtractorTests()
    {
        _processor = new ContentProcessor(NullLogger<ContentProcessor>.Instance);
        _options = new DeepResearchOptions
        {
            MaxParallelExtractions = 5
        };
    }

    [Fact]
    public async Task ExtractAsync_ValidHtml_ReturnsContent()
    {
        // Arrange
        var html = """
            <html>
            <head><title>Test Page</title></head>
            <body><p>This is the main content.</p></body>
            </html>
            """;

        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            }));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        // Act
        var result = await extractor.ExtractAsync("https://example.com/page");

        // Assert
        result.Success.Should().BeTrue();
        result.Title.Should().Be("Test Page");
        result.Content.Should().Contain("main content");
        result.Url.Should().Be("https://example.com/page");
    }

    [Fact]
    public async Task ExtractAsync_InvalidUrl_ReturnsFailure()
    {
        // Arrange
        var httpClient = new HttpClient();
        var extractor = CreateExtractor(httpClient);

        // Act
        var result = await extractor.ExtractAsync("not-a-valid-url");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("유효하지 않은 URL");
    }

    [Fact]
    public async Task ExtractAsync_HttpError_ReturnsFailure()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        // Act
        var result = await extractor.ExtractAsync("https://example.com/notfound");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTP 요청 실패");
    }

    [Fact]
    public async Task ExtractAsync_UnsupportedContentType_ReturnsFailure()
    {
        // Arrange
        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            }));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        // Act
        var result = await extractor.ExtractAsync("https://example.com/api");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("지원하지 않는 콘텐츠 타입");
    }

    [Fact]
    public async Task ExtractAsync_WithOptions_UsesProvidedOptions()
    {
        // Arrange
        var html = """
            <html>
            <head><title>Test</title></head>
            <body>
                <a href="/link1">Link 1</a>
                <img src="/image1.png">
            </body>
            </html>
            """;

        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            }));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        var options = new ContentExtractionOptions
        {
            ExtractLinks = true,
            ExtractImages = true
        };

        // Act
        var result = await extractor.ExtractAsync("https://example.com/page", options);

        // Assert
        result.Success.Should().BeTrue();
        result.Links.Should().NotBeNullOrEmpty();
        result.Images.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractBatchAsync_MultipleUrls_ReturnsAllResults()
    {
        // Arrange
        var html = "<html><head><title>Test</title></head><body>Content</body></html>";
        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            }));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        var urls = new[]
        {
            "https://example.com/page1",
            "https://example.com/page2",
            "https://example.com/page3"
        };

        // Act
        var results = await extractor.ExtractBatchAsync(urls);

        // Assert
        results.Should().HaveCount(3);
        results.All(r => r.Success).Should().BeTrue();
    }

    [Fact]
    public async Task ExtractBatchAsync_PartialFailure_ReturnsAllResults()
    {
        // Arrange
        var callCount = 0;
        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
        {
            callCount++;
            if (callCount == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Content</body></html>",
                    System.Text.Encoding.UTF8, "text/html")
            });
        });

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        var urls = new[]
        {
            "https://example.com/page1",
            "https://example.com/page2",
            "https://example.com/page3"
        };

        // Act
        var results = await extractor.ExtractBatchAsync(urls);

        // Assert
        results.Should().HaveCount(3);
        results.Count(r => r.Success).Should().Be(2);
        results.Count(r => !r.Success).Should().Be(1);
    }

    [Fact]
    public async Task ExtractAsync_Timeout_ReturnsFailure()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(async ct =>
        {
            await Task.Delay(2000, ct); // 짧은 지연
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        var options = new ContentExtractionOptions
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };

        // Act
        var result = await extractor.ExtractAsync("https://example.com/slow", options);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("타임아웃");
    }

    [Fact]
    public async Task ExtractAsync_SetsExtractedAt()
    {
        // Arrange
        var html = "<html><body>Content</body></html>";
        var handler = new MockHttpMessageHandler((HttpRequestMessage request) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            }));

        var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await extractor.ExtractAsync("https://example.com/page");

        // Assert
        result.ExtractedAt.Should().BeOnOrAfter(before);
        result.ExtractedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    private WebFluxContentExtractor CreateExtractor(HttpClient httpClient)
    {
        return new WebFluxContentExtractor(
            httpClient,
            _processor,
            _options,
            NullLogger<WebFluxContentExtractor>.Instance);
    }
}

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _handler;
    private readonly Func<CancellationToken, Task<HttpResponseMessage>>? _handlerWithToken;

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public MockHttpMessageHandler(Func<CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handlerWithToken = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_handlerWithToken != null)
        {
            return await _handlerWithToken(cancellationToken);
        }
        return await _handler!(request);
    }
}
