using AwesomeAssertions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WebFlux.Core.Interfaces;
using WebFlux.Core.Options;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Content;

public class WebFluxIntegratedContentExtractorTests
{
    // The extractor used to prefer the keyed "Intelligent" crawler. That crawler made no request and
    // returned placeholder text as a successful page, so the placeholder was extracted as the page.
    // A crawler registered under that key must never be the one that answers.
    [Fact]
    public async Task ExtractAsync_FetchesThroughTheHttpCrawler_NotACrawlerRegisteredAsIntelligent()
    {
        var fabricating = Substitute.For<ICrawler>();
        fabricating.CrawlAsync(Arg.Any<string>(), Arg.Any<CrawlOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new CrawlResult
            {
                Url = "https://example.com/",
                IsSuccess = true,
                HtmlContent = "Basic Intelligent crawl result for https://example.com/"
            });

        var http = Substitute.For<ICrawler>();
        http.CrawlAsync(Arg.Any<string>(), Arg.Any<CrawlOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new CrawlResult
            {
                Url = "https://example.com/",
                IsSuccess = false,
                ErrorMessage = "unreachable in this test"
            });

        var provider = new ServiceCollection()
            .AddKeyedSingleton("Intelligent", fabricating)
            .AddKeyedSingleton("BreadthFirst", http)
            .BuildServiceProvider();

        using var extractor = new WebFluxIntegratedContentExtractor(
            provider,
            new DeepResearchOptions { MaxParallelExtractions = 1 },
            NullLogger<WebFluxIntegratedContentExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            "https://example.com/", cancellationToken: TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse("the HTTP crawler failed, and nothing else may answer for it");
        result.ErrorMessage.Should().Be("unreachable in this test");
        await http.Received(1).CrawlAsync(
            "https://example.com/", Arg.Any<CrawlOptions?>(), Arg.Any<CancellationToken>());
        await fabricating.DidNotReceive().CrawlAsync(
            Arg.Any<string>(), Arg.Any<CrawlOptions?>(), Arg.Any<CancellationToken>());
    }
}
