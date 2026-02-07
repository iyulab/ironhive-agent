using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Content;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Content;

public class ContentProcessorTests
{
    private readonly ContentProcessor _processor;

    public ContentProcessorTests()
    {
        _processor = new ContentProcessor(NullLogger<ContentProcessor>.Instance);
    }

    [Fact]
    public void Process_ExtractsTitle_FromTitleTag()
    {
        // Arrange
        var html = "<html><head><title>Test Page Title</title></head><body>Content</body></html>";
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Title.Should().Be("Test Page Title");
    }

    [Fact]
    public void Process_ExtractsTitle_FromOgTitle()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <meta property="og:title" content="Open Graph Title">
            </head>
            <body>Content</body>
            </html>
            """;
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Title.Should().Be("Open Graph Title");
    }

    [Fact]
    public void Process_ExtractsDescription_FromMetaTag()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <meta name="description" content="This is the page description">
            </head>
            <body>Content</body>
            </html>
            """;
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Description.Should().Be("This is the page description");
    }

    [Fact]
    public void Process_ExtractsAuthor_WhenMetadataEnabled()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <meta name="author" content="John Doe">
            </head>
            <body>Content</body>
            </html>
            """;
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions { ExtractMetadata = true };

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Author.Should().Be("John Doe");
    }

    [Fact]
    public void Process_RemovesScripts()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <p>Before script</p>
                <script>alert('hello');</script>
                <p>After script</p>
            </body>
            </html>
            """;
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Content.Should().NotContain("alert");
        result.Content.Should().Contain("Before script");
        result.Content.Should().Contain("After script");
    }

    [Fact]
    public void Process_RemovesStyles()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <style>.class { color: red; }</style>
            </head>
            <body>
                <p>Content text</p>
            </body>
            </html>
            """;
        var uri = new Uri("https://example.com/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Content.Should().NotContain("color: red");
        result.Content.Should().Contain("Content text");
    }

    [Fact]
    public void Process_ExtractsLinks_WhenEnabled()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="/page1">Link 1</a>
                <a href="https://example.com/page2">Link 2</a>
                <a href="mailto:test@example.com">Email</a>
            </body>
            </html>
            """;
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions { ExtractLinks = true };

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Links.Should().NotBeNull();
        result.Links.Should().Contain("https://example.com/page1");
        result.Links.Should().Contain("https://example.com/page2");
        result.Links.Should().NotContain(l => l.StartsWith("mailto:"));
    }

    [Fact]
    public void Process_ExtractsImages_WhenEnabled()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <img src="/image1.png" alt="Image 1">
                <img src="https://cdn.example.com/image2.jpg" alt="Image 2">
            </body>
            </html>
            """;
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions { ExtractImages = true };

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Images.Should().NotBeNull();
        result.Images.Should().Contain("https://example.com/image1.png");
        result.Images.Should().Contain("https://cdn.example.com/image2.jpg");
    }

    [Fact]
    public void Process_TruncatesContent_WhenExceedsMaxLength()
    {
        // Arrange
        var longContent = new string('A', 10000);
        var html = $"<html><body><p>{longContent}</p></body></html>";
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions { MaxContentLength = 1000 };

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Content!.Length.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void Process_DecodesHtmlEntities()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <p>This &amp; that &lt;tag&gt; &quot;quoted&quot;</p>
            </body>
            </html>
            """;
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Content.Should().Contain("This & that");
        result.Content.Should().Contain("<tag>");
        result.Content.Should().Contain("\"quoted\"");
    }

    [Fact]
    public void Process_SetsCorrectUrl()
    {
        // Arrange
        var html = "<html><body>Content</body></html>";
        var uri = new Uri("https://example.com/test/page");
        var options = new ContentExtractionOptions();

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.Url.Should().Be("https://example.com/test/page");
    }

    [Fact]
    public void Process_SetsExtractedAt()
    {
        // Arrange
        var html = "<html><body>Content</body></html>";
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions();
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.ExtractedAt.Should().BeOnOrAfter(before);
        result.ExtractedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Process_ExtractsPublishedDate_FromArticleTag()
    {
        // Arrange
        var html = """
            <html>
            <head>
                <meta property="article:published_time" content="2026-01-15T10:30:00Z">
            </head>
            <body>Content</body>
            </html>
            """;
        var uri = new Uri("https://example.com/");
        var options = new ContentExtractionOptions { ExtractMetadata = true };

        // Act
        var result = _processor.Process(html, uri, options);

        // Assert
        result.PublishedDate.Should().NotBeNull();
        result.PublishedDate!.Value.Year.Should().Be(2026);
        result.PublishedDate.Value.Month.Should().Be(1);
        result.PublishedDate.Value.Day.Should().Be(15);
    }
}
