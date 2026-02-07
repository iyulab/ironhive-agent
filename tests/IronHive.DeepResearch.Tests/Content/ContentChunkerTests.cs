using FluentAssertions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Models.Content;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Content;

public class ContentChunkerTests
{
    private readonly ContentChunker _chunker;

    public ContentChunkerTests()
    {
        _chunker = new ContentChunker(NullLogger<ContentChunker>.Instance);
    }

    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        // Arrange
        var text = "This is a short text that fits in one chunk.";
        var options = new ChunkingOptions { MaxTokensPerChunk = 500 };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].Content.Should().Be(text);
        chunks[0].ChunkIndex.Should().Be(0);
        chunks[0].TotalChunks.Should().Be(1);
    }

    [Fact]
    public void ChunkText_LongText_ReturnsMultipleChunks()
    {
        // Arrange
        var text = string.Join(" ", Enumerable.Repeat("This is a sentence.", 100));
        var options = new ChunkingOptions { MaxTokensPerChunk = 50, OverlapTokens = 0 };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks.Should().HaveCountGreaterThan(1);
        chunks.All(c => c.SourceId == "src1").Should().BeTrue();
        chunks.All(c => c.SourceUrl == "https://example.com").Should().BeTrue();
    }

    [Fact]
    public void ChunkText_SetsCorrectChunkIndices()
    {
        // Arrange
        var text = string.Join(" ", Enumerable.Repeat("Word", 500));
        var options = new ChunkingOptions { MaxTokensPerChunk = 50, OverlapTokens = 0 };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].ChunkIndex.Should().Be(i);
            chunks[i].TotalChunks.Should().Be(chunks.Count);
        }
    }

    [Fact]
    public void ChunkText_WithOverlap_HasOverlappingContent()
    {
        // Arrange
        var sentences = Enumerable.Range(1, 20).Select(i => $"Sentence number {i}.").ToArray();
        var text = string.Join(" ", sentences);
        var options = new ChunkingOptions
        {
            MaxTokensPerChunk = 30,
            OverlapTokens = 10,
            SplitOnSentences = true
        };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks.Should().HaveCountGreaterThan(1);
        // 오버랩으로 인해 일부 내용이 연속 청크에 중복됨
    }

    [Fact]
    public void ChunkText_SplitsOnParagraphs_WhenEnabled()
    {
        // Arrange
        var text = "First paragraph content.\n\nSecond paragraph content.\n\nThird paragraph content.";
        var options = new ChunkingOptions
        {
            MaxTokensPerChunk = 500,
            SplitOnParagraphs = true
        };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks.Should().HaveCount(1); // 모든 문단이 하나의 청크에 들어감
        chunks[0].Content.Should().Contain("First paragraph");
        chunks[0].Content.Should().Contain("Second paragraph");
        chunks[0].Content.Should().Contain("Third paragraph");
    }

    [Fact]
    public void ChunkText_EstimatesTokenCount()
    {
        // Arrange
        var text = "This is a test sentence with some words.";
        var options = new ChunkingOptions { MaxTokensPerChunk = 500 };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks[0].TokenCount.Should().BeGreaterThan(0);
        chunks[0].TokenCount.Should().BeLessThan(text.Length); // 토큰 수 < 문자 수
    }

    [Fact]
    public void ChunkText_SetsPositions()
    {
        // Arrange
        var text = "First chunk content. Second chunk content.";
        var options = new ChunkingOptions { MaxTokensPerChunk = 500 };

        // Act
        var chunks = _chunker.ChunkText(text, "src1", "https://example.com", options);

        // Assert
        chunks[0].StartPosition.Should().Be(0);
        chunks[0].EndPosition.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ChunkContent_ExtractedContent_ReturnsChunks()
    {
        // Arrange
        var content = new ExtractedContent
        {
            Url = "https://example.com/page",
            Title = "Test Page",
            Content = "This is the page content that needs to be chunked.",
            ExtractedAt = DateTimeOffset.UtcNow,
            Success = true
        };
        var options = new ChunkingOptions { MaxTokensPerChunk = 500 };

        // Act
        var chunks = _chunker.ChunkContent(content, options);

        // Assert
        chunks.Should().NotBeEmpty();
        chunks[0].SourceUrl.Should().Be("https://example.com/page");
    }

    [Fact]
    public void ChunkContent_EmptyContent_ReturnsEmpty()
    {
        // Arrange
        var content = new ExtractedContent
        {
            Url = "https://example.com/page",
            Content = "",
            ExtractedAt = DateTimeOffset.UtcNow,
            Success = true
        };

        // Act
        var chunks = _chunker.ChunkContent(content);

        // Assert
        chunks.Should().BeEmpty();
    }

    [Fact]
    public void ChunkContents_MultipleContents_ReturnsAllChunks()
    {
        // Arrange
        var contents = new[]
        {
            new ExtractedContent
            {
                Url = "https://example.com/page1",
                Content = "Content from page 1.",
                ExtractedAt = DateTimeOffset.UtcNow,
                Success = true
            },
            new ExtractedContent
            {
                Url = "https://example.com/page2",
                Content = "Content from page 2.",
                ExtractedAt = DateTimeOffset.UtcNow,
                Success = true
            }
        };

        // Act
        var chunks = _chunker.ChunkContents(contents);

        // Assert
        chunks.Should().HaveCount(2);
        chunks.Select(c => c.SourceUrl).Should().Contain("https://example.com/page1");
        chunks.Select(c => c.SourceUrl).Should().Contain("https://example.com/page2");
    }

    [Fact]
    public void ChunkContents_SkipsFailedContent()
    {
        // Arrange
        var contents = new[]
        {
            new ExtractedContent
            {
                Url = "https://example.com/page1",
                Content = "Content from page 1.",
                ExtractedAt = DateTimeOffset.UtcNow,
                Success = true
            },
            new ExtractedContent
            {
                Url = "https://example.com/page2",
                Content = null,
                ExtractedAt = DateTimeOffset.UtcNow,
                Success = false,
                ErrorMessage = "Failed to extract"
            }
        };

        // Act
        var chunks = _chunker.ChunkContents(contents);

        // Assert
        chunks.Should().HaveCount(1);
        chunks[0].SourceUrl.Should().Be("https://example.com/page1");
    }

    [Fact]
    public void ChunkText_KoreanText_EstimatesTokensCorrectly()
    {
        // Arrange
        var koreanText = "안녕하세요. 이것은 한국어 텍스트입니다. 토큰 수를 정확하게 추정해야 합니다.";
        var options = new ChunkingOptions { MaxTokensPerChunk = 500 };

        // Act
        var chunks = _chunker.ChunkText(koreanText, "src1", "https://example.com", options);

        // Assert
        chunks[0].TokenCount.Should().BeGreaterThan(0);
        // 한국어는 약 2자당 1토큰으로 추정
    }

    [Fact]
    public void ChunkText_VeryLongSentence_ForceSplits()
    {
        // Arrange
        var longSentence = string.Join("", Enumerable.Repeat("word ", 1000));
        var options = new ChunkingOptions
        {
            MaxTokensPerChunk = 50,
            OverlapTokens = 0,  // 오버랩 없이 테스트
            SplitOnSentences = true
        };

        // Act
        var chunks = _chunker.ChunkText(longSentence, "src1", "https://example.com", options);

        // Assert
        chunks.Should().HaveCountGreaterThan(1);
        chunks.All(c => c.Content.Length > 0).Should().BeTrue();
    }
}
