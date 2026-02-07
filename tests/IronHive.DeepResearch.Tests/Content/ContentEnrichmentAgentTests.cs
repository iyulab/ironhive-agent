using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Content;

public class ContentEnrichmentAgentTests
{
    private readonly ContentEnrichmentAgent _agent;
    private readonly MockContentExtractor _mockExtractor;
    private readonly ContentChunker _chunker;
    private readonly DeepResearchOptions _options;

    public ContentEnrichmentAgentTests()
    {
        _mockExtractor = new MockContentExtractor();
        _chunker = new ContentChunker(NullLogger<ContentChunker>.Instance);
        _options = new DeepResearchOptions
        {
            MaxParallelExtractions = 5,
            HttpTimeout = TimeSpan.FromSeconds(10)
        };

        _agent = new ContentEnrichmentAgent(
            _mockExtractor,
            _chunker,
            _options,
            NullLogger<ContentEnrichmentAgent>.Instance);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_SingleResult_CreatesDocument()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/page1")
        };

        _mockExtractor.SetupExtraction("https://example.com/page1",
            CreateExtractedContent("https://example.com/page1", "Test content for page 1."));

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().HaveCount(1);
        result.FailedExtractions.Should().BeEmpty();
        result.Documents[0].Url.Should().Be("https://example.com/page1");
        result.Documents[0].Content.Should().Contain("Test content");
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_MultipleResults_ProcessesAll()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResult("https://example.com/page1", "Page 1", 2),
            CreateSearchResult("https://example.com/page2", "Page 2", 1)
        };

        _mockExtractor.SetupExtraction("https://example.com/page1/source1",
            CreateExtractedContent("https://example.com/page1/source1", "Content 1"));
        _mockExtractor.SetupExtraction("https://example.com/page1/source2",
            CreateExtractedContent("https://example.com/page1/source2", "Content 2"));
        _mockExtractor.SetupExtraction("https://example.com/page2/source1",
            CreateExtractedContent("https://example.com/page2/source1", "Content 3"));

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().HaveCount(3);
        result.TotalUrlsProcessed.Should().Be(3);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_DeduplicatesUrls()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/duplicate"),
            CreateSearchResultWithUrl("https://example.com/duplicate") // 중복
        };

        _mockExtractor.SetupExtraction("https://example.com/duplicate",
            CreateExtractedContent("https://example.com/duplicate", "Content"));

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().HaveCount(1);
        result.TotalUrlsProcessed.Should().Be(1);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_WithRawContent_UsesDirectly()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            new()
            {
                Query = new SearchQuery { Query = "test" },
                Provider = "test",
                SearchedAt = DateTimeOffset.UtcNow,
                Sources = new List<SearchSource>
                {
                    new()
                    {
                        Url = "https://example.com/raw",
                        Title = "Raw Content Page",
                        RawContent = "This is raw content from search result."
                    }
                }
            }
        };

        var options = new ContentEnrichmentOptions
        {
            UseRawContentWhenAvailable = true,
            EnableChunking = true
        };

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults, options);

        // Assert
        result.Documents.Should().HaveCount(1);
        result.Documents[0].Content.Should().Be("This is raw content from search result.");
        _mockExtractor.ExtractCallCount.Should().Be(0); // 추출기 호출 안됨
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_WithChunking_CreatesChunks()
    {
        // Arrange
        var longContent = string.Join(" ", Enumerable.Repeat("This is a test sentence.", 100));
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/long")
        };

        _mockExtractor.SetupExtraction("https://example.com/long",
            CreateExtractedContent("https://example.com/long", longContent));

        var options = new ContentEnrichmentOptions
        {
            EnableChunking = true,
            ChunkingOptions = new ChunkingOptions
            {
                MaxTokensPerChunk = 100,
                OverlapTokens = 10
            }
        };

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults, options);

        // Assert
        result.Documents.Should().HaveCount(1);
        result.Documents[0].Chunks.Should().NotBeNullOrEmpty();
        result.TotalChunksCreated.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_ExtractionFailure_RecordsFailure()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/fail")
        };

        _mockExtractor.SetupFailure("https://example.com/fail",
            new HttpRequestException("Connection refused"));

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().BeEmpty();
        result.FailedExtractions.Should().HaveCount(1);
        result.FailedExtractions[0].ErrorType.Should().Be(ExtractionErrorType.NetworkError);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_NoContent_RecordsFailure()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/empty")
        };

        _mockExtractor.SetupExtraction("https://example.com/empty",
            new ExtractedContent
            {
                Url = "https://example.com/empty",
                Content = "",
                Success = true,
                ExtractedAt = DateTimeOffset.UtcNow
            });

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().BeEmpty();
        result.FailedExtractions.Should().HaveCount(1);
        result.FailedExtractions[0].ErrorType.Should().Be(ExtractionErrorType.NoContent);
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_ReportsProgress()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/1"),
            CreateSearchResultWithUrl("https://example.com/2")
        };

        _mockExtractor.SetupExtraction("https://example.com/1",
            CreateExtractedContent("https://example.com/1", "Content 1"));
        _mockExtractor.SetupExtraction("https://example.com/2",
            CreateExtractedContent("https://example.com/2", "Content 2"));

        var progressReports = new List<ContentEnrichmentProgress>();
        var progress = new Progress<ContentEnrichmentProgress>(p => progressReports.Add(p));

        // Act
        await _agent.EnrichSearchResultsAsync(searchResults, progress: progress);

        // Assert
        await Task.Delay(100);
        progressReports.Should().NotBeEmpty();
        progressReports.Last().CompletedUrls.Should().Be(2);
    }

    [Fact]
    public async Task EnrichFromStateAsync_UpdatesState()
    {
        // Arrange
        var state = CreateTestState();
        var searchExecution = new SearchExecutionResult
        {
            SuccessfulResults = new List<SearchResult>
            {
                CreateSearchResultWithUrl("https://example.com/state-test")
            },
            FailedSearches = [],
            TotalQueriesExecuted = 1,
            UniqueSourcesCollected = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };

        _mockExtractor.SetupExtraction("https://example.com/state-test",
            CreateExtractedContent("https://example.com/state-test", "State test content"));

        // Act
        await _agent.EnrichFromStateAsync(state, searchExecution);

        // Assert
        state.CollectedSources.Should().HaveCount(1);
        state.CollectedSources[0].Url.Should().Be("https://example.com/state-test");
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_CalculatesTrustScore()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/trusted")
        };

        _mockExtractor.SetupExtraction("https://example.com/trusted",
            new ExtractedContent
            {
                Url = "https://example.com/trusted",
                Title = "Trusted Page",
                Content = new string('a', 1000), // 적절한 길이
                Author = "John Doe",
                PublishedDate = DateTimeOffset.UtcNow.AddDays(-1),
                ContentLength = 1000,
                Success = true,
                ExtractedAt = DateTimeOffset.UtcNow
            });

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.Documents.Should().HaveCount(1);
        result.Documents[0].TrustScore.Should().BeGreaterThan(0.5); // 기본보다 높아야 함
    }

    [Fact]
    public async Task EnrichSearchResultsAsync_AccessDenied_RecordsCorrectErrorType()
    {
        // Arrange
        var searchResults = new List<SearchResult>
        {
            CreateSearchResultWithUrl("https://example.com/forbidden")
        };

        _mockExtractor.SetupFailure("https://example.com/forbidden",
            new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden));

        // Act
        var result = await _agent.EnrichSearchResultsAsync(searchResults);

        // Assert
        result.FailedExtractions.Should().HaveCount(1);
        result.FailedExtractions[0].ErrorType.Should().Be(ExtractionErrorType.AccessDenied);
    }

    private static ResearchState CreateTestState()
    {
        return new ResearchState
        {
            SessionId = Guid.NewGuid().ToString(),
            Request = new ResearchRequest
            {
                Query = "test",
                Depth = ResearchDepth.Standard,
                Language = "ko"
            },
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    private static SearchResult CreateSearchResult(string baseUrl, string title, int sourceCount)
    {
        var sources = Enumerable.Range(1, sourceCount)
            .Select(i => new SearchSource
            {
                Url = $"{baseUrl}/source{i}",
                Title = $"{title} Source {i}"
            })
            .ToList();

        return new SearchResult
        {
            Query = new SearchQuery { Query = "test" },
            Provider = "test",
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = sources
        };
    }

    private static SearchResult CreateSearchResultWithUrl(string url)
    {
        return new SearchResult
        {
            Query = new SearchQuery { Query = "test" },
            Provider = "test",
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = new List<SearchSource>
            {
                new() { Url = url, Title = "Test Page" }
            }
        };
    }

    private static ExtractedContent CreateExtractedContent(string url, string content)
    {
        return new ExtractedContent
        {
            Url = url,
            Title = "Test Title",
            Content = content,
            ContentLength = content.Length,
            Success = true,
            ExtractedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// 테스트용 Mock ContentExtractor
/// </summary>
internal class MockContentExtractor : IContentExtractor
{
    private readonly Dictionary<string, ExtractedContent> _extractions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

    public int ExtractCallCount { get; private set; }

    public void SetupExtraction(string url, ExtractedContent content)
    {
        _extractions[url] = content;
    }

    public void SetupFailure(string url, Exception exception)
    {
        _failures[url] = exception;
    }

    public Task<ExtractedContent> ExtractAsync(
        string url,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ExtractCallCount++;

        if (_failures.TryGetValue(url, out var exception))
        {
            throw exception;
        }

        if (_extractions.TryGetValue(url, out var content))
        {
            return Task.FromResult(content);
        }

        return Task.FromResult(new ExtractedContent
        {
            Url = url,
            Content = "Default content",
            ContentLength = 15,
            Success = true,
            ExtractedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<IReadOnlyList<ExtractedContent>> ExtractBatchAsync(
        IEnumerable<string> urls,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExtractedContent>();
        foreach (var url in urls)
        {
            results.Add(await ExtractAsync(url, options, cancellationToken));
        }
        return results;
    }
}
