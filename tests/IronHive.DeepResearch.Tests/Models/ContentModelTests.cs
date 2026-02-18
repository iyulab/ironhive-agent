using FluentAssertions;
using IronHive.DeepResearch.Models.Content;
using Xunit;

namespace IronHive.DeepResearch.Tests.Models;

public class ExtractedContentTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var content = new ExtractedContent
        {
            Url = "https://example.com",
            ExtractedAt = DateTimeOffset.UtcNow
        };

        content.Title.Should().BeNull();
        content.Content.Should().BeNull();
        content.Description.Should().BeNull();
        content.Author.Should().BeNull();
        content.PublishedDate.Should().BeNull();
        content.ContentLength.Should().Be(0);
        content.Success.Should().BeTrue();
        content.ErrorMessage.Should().BeNull();
        content.Links.Should().BeNull();
        content.Images.Should().BeNull();
    }

    [Fact]
    public void ShouldInitialize_SuccessfulExtraction()
    {
        var content = new ExtractedContent
        {
            Url = "https://example.com/article",
            Title = "Test Article",
            Content = "Full article content...",
            ContentLength = 1500,
            ExtractedAt = DateTimeOffset.UtcNow,
            Links = ["https://ref1.com", "https://ref2.com"],
            Images = ["img1.png"]
        };

        content.Success.Should().BeTrue();
        content.Links.Should().HaveCount(2);
        content.Images.Should().ContainSingle();
    }

    [Fact]
    public void ShouldInitialize_FailedExtraction()
    {
        var content = new ExtractedContent
        {
            Url = "https://example.com/broken",
            ExtractedAt = DateTimeOffset.UtcNow,
            Success = false,
            ErrorMessage = "Connection timeout"
        };

        content.Success.Should().BeFalse();
        content.ErrorMessage.Should().Be("Connection timeout");
    }
}

public class ContentChunkTests
{
    [Fact]
    public void DefaultValues_ShouldBeZero()
    {
        var chunk = new ContentChunk
        {
            SourceId = "src-1",
            SourceUrl = "https://example.com",
            Content = "Chunk text",
            ChunkIndex = 0
        };

        chunk.TotalChunks.Should().Be(0);
        chunk.TokenCount.Should().Be(0);
        chunk.StartPosition.Should().Be(0);
        chunk.EndPosition.Should().Be(0);
    }

    [Fact]
    public void ShouldInitialize_WithPositionalData()
    {
        var chunk = new ContentChunk
        {
            SourceId = "src-1",
            SourceUrl = "https://example.com",
            Content = "Some text content...",
            ChunkIndex = 2,
            TotalChunks = 5,
            TokenCount = 120,
            StartPosition = 500,
            EndPosition = 1000
        };

        chunk.ChunkIndex.Should().Be(2);
        chunk.TotalChunks.Should().Be(5);
        chunk.StartPosition.Should().Be(500);
        chunk.EndPosition.Should().Be(1000);
    }
}

public class SourceDocumentTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var doc = new SourceDocument
        {
            Id = "doc-1",
            Url = "https://example.com",
            Title = "Test",
            Content = "Content",
            ExtractedAt = DateTimeOffset.UtcNow,
            Provider = "tavily"
        };

        doc.Description.Should().BeNull();
        doc.Author.Should().BeNull();
        doc.PublishedDate.Should().BeNull();
        doc.RelevanceScore.Should().Be(0);
        doc.TrustScore.Should().Be(0);
        doc.Chunks.Should().BeNull();
    }

    [Fact]
    public void ShouldInitialize_WithScoresAndChunks()
    {
        var chunks = new[]
        {
            new ContentChunk
            {
                SourceId = "doc-1", SourceUrl = "https://example.com",
                Content = "Chunk 1", ChunkIndex = 0
            }
        };
        var doc = new SourceDocument
        {
            Id = "doc-1",
            Url = "https://example.com",
            Title = "Research Paper",
            Content = "Full content",
            ExtractedAt = DateTimeOffset.UtcNow,
            Provider = "tavily",
            RelevanceScore = 0.9,
            TrustScore = 0.85,
            Chunks = chunks
        };

        doc.RelevanceScore.Should().Be(0.9);
        doc.TrustScore.Should().Be(0.85);
        doc.Chunks.Should().ContainSingle();
    }
}

public class ChunkingOptionsTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var options = new ChunkingOptions();

        options.MaxTokensPerChunk.Should().Be(500);
        options.OverlapTokens.Should().Be(50);
        options.SplitOnSentences.Should().BeTrue();
        options.SplitOnParagraphs.Should().BeTrue();
    }

    [Fact]
    public void ShouldOverride_Defaults()
    {
        var options = new ChunkingOptions
        {
            MaxTokensPerChunk = 1000,
            OverlapTokens = 100,
            SplitOnSentences = false,
            SplitOnParagraphs = false
        };

        options.MaxTokensPerChunk.Should().Be(1000);
        options.OverlapTokens.Should().Be(100);
        options.SplitOnSentences.Should().BeFalse();
        options.SplitOnParagraphs.Should().BeFalse();
    }
}
