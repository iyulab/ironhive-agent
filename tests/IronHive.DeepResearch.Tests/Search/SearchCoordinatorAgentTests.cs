using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using IronHive.DeepResearch.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Search;

public class SearchCoordinatorAgentTests
{
    private readonly SearchCoordinatorAgent _agent;
    private readonly MockSearchProvider _mockProvider;
    private readonly DeepResearchOptions _options;

    public SearchCoordinatorAgentTests()
    {
        _mockProvider = new MockSearchProvider();
        _options = new DeepResearchOptions
        {
            MaxParallelSearches = 3,
            MaxRetries = 2,
            HttpTimeout = TimeSpan.FromSeconds(10)
        };

        var factory = new SearchProviderFactory(
            [_mockProvider],
            _options,
            NullLogger<SearchProviderFactory>.Instance);

        _agent = new SearchCoordinatorAgent(
            factory,
            _options,
            NullLogger<SearchCoordinatorAgent>.Instance);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_SingleQuery_ReturnsResult()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "test query", Intent = "test", Priority = 1 }
        };

        _mockProvider.SetupSearchResult("test query", CreateSearchResult("test query", 3));

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries);

        // Assert
        result.SuccessfulResults.Should().HaveCount(1);
        result.FailedSearches.Should().BeEmpty();
        result.TotalQueriesExecuted.Should().Be(1);
        result.UniqueSourcesCollected.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_MultipleQueries_ExecutesAll()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "query1", Intent = "test", Priority = 1 },
            new() { Query = "query2", Intent = "test", Priority = 2 },
            new() { Query = "query3", Intent = "test", Priority = 3 }
        };

        _mockProvider.SetupSearchResult("query1", CreateSearchResult("query1", 2));
        _mockProvider.SetupSearchResult("query2", CreateSearchResult("query2", 3));
        _mockProvider.SetupSearchResult("query3", CreateSearchResult("query3", 1));

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries);

        // Assert
        result.SuccessfulResults.Should().HaveCount(3);
        result.TotalQueriesExecuted.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_WithFailure_RecordsFailedSearch()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "good query", Intent = "test", Priority = 1 },
            new() { Query = "bad query", Intent = "test", Priority = 2 }
        };

        _mockProvider.SetupSearchResult("good query", CreateSearchResult("good query", 2));
        _mockProvider.SetupException("bad query", new HttpRequestException("API Error"));

        var options = new SearchExecutionOptions
        {
            MaxRetriesPerQuery = 0, // 재시도 없이 즉시 실패
            ContinueOnError = true
        };

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries, options);

        // Assert
        result.SuccessfulResults.Should().HaveCount(1);
        result.FailedSearches.Should().HaveCount(1);
        result.FailedSearches[0].Query.Query.Should().Be("bad query");
    }

    [Fact]
    public async Task ExecuteSearchesAsync_DeduplicatesUrls()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "query1", Intent = "test", Priority = 1 },
            new() { Query = "query2", Intent = "test", Priority = 2 }
        };

        // 동일한 URL을 포함하는 결과
        var result1 = new SearchResult
        {
            Query = new SearchQuery { Query = "query1" },
            Provider = "mock",
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = new List<SearchSource>
            {
                new() { Url = "https://example.com/1", Title = "Title 1" },
                new() { Url = "https://example.com/2", Title = "Title 2" }
            }
        };

        var result2 = new SearchResult
        {
            Query = new SearchQuery { Query = "query2" },
            Provider = "mock",
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = new List<SearchSource>
            {
                new() { Url = "https://example.com/2", Title = "Title 2 Dup" }, // 중복
                new() { Url = "https://example.com/3", Title = "Title 3" }
            }
        };

        _mockProvider.SetupSearchResult("query1", result1);
        _mockProvider.SetupSearchResult("query2", result2);

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries);

        // Assert
        result.UniqueSourcesCollected.Should().Be(3); // 중복 제거됨
    }

    [Fact]
    public async Task ExecuteSearchesAsync_ReportsProgress()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "query1", Intent = "test", Priority = 1 },
            new() { Query = "query2", Intent = "test", Priority = 2 }
        };

        _mockProvider.SetupSearchResult("query1", CreateSearchResult("query1", 1));
        _mockProvider.SetupSearchResult("query2", CreateSearchResult("query2", 1));

        var progressReports = new List<SearchBatchProgress>();
        var progress = new Progress<SearchBatchProgress>(p => progressReports.Add(p));

        // Act
        await _agent.ExecuteSearchesAsync(queries, progress: progress);

        // Assert (약간의 지연 후 확인)
        await Task.Delay(100);
        progressReports.Should().NotBeEmpty();
        progressReports.Last().CompletedQueries.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteFromStateAsync_ExcludesAlreadyExecutedQueries()
    {
        // Arrange
        var state = CreateTestState();
        state.ExecutedQueries.Add(new SearchQuery { Query = "already executed" });

        var plan = new QueryPlanResult
        {
            InitialQueries = new List<ExpandedQuery>
            {
                new() { Query = "already executed", Intent = "test", Priority = 1 },
                new() { Query = "new query", Intent = "test", Priority = 2 }
            },
            Perspectives = [],
            SubQuestions = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        _mockProvider.SetupSearchResult("new query", CreateSearchResult("new query", 2));

        // Act
        var result = await _agent.ExecuteFromStateAsync(state, plan);

        // Assert
        result.TotalQueriesExecuted.Should().Be(1);
        result.SuccessfulResults.Should().HaveCount(1);
        result.SuccessfulResults[0].Query.Query.Should().Be("new query");
    }

    [Fact]
    public async Task ExecuteFromStateAsync_UpdatesState()
    {
        // Arrange
        var state = CreateTestState();
        var plan = new QueryPlanResult
        {
            InitialQueries = new List<ExpandedQuery>
            {
                new() { Query = "test query", Intent = "test", Priority = 1 }
            },
            Perspectives = [],
            SubQuestions = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        _mockProvider.SetupSearchResult("test query", CreateSearchResult("test query", 2));

        // Act
        await _agent.ExecuteFromStateAsync(state, plan);

        // Assert
        state.ExecutedQueries.Should().HaveCount(1);
        state.SearchResults.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_RespectsCancellation()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "query1", Intent = "test", Priority = 1 }
        };

        _mockProvider.SetupDelay("query1", TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries, cancellationToken: cts.Token);

        // Assert - 취소된 쿼리는 실패로 기록됨
        result.FailedSearches.Should().HaveCount(1);
        result.FailedSearches[0].ErrorType.Should().Be(SearchErrorType.Cancelled);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_HandlesRateLimit()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "rate limited", Intent = "test", Priority = 1 }
        };

        _mockProvider.SetupRateLimitThenSuccess("rate limited", CreateSearchResult("rate limited", 1));

        var options = new SearchExecutionOptions
        {
            MaxRetriesPerQuery = 2,
            RetryDelay = TimeSpan.FromMilliseconds(100)
        };

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries, options);

        // Assert
        result.SuccessfulResults.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteFollowUpSearchesAsync_ExecutesNewQueries()
    {
        // Arrange
        var state = CreateTestState();
        var followUpQueries = new List<ExpandedQuery>
        {
            new() { Query = "follow up query", Intent = "gap filling", Priority = 1 }
        };

        _mockProvider.SetupSearchResult("follow up query", CreateSearchResult("follow up query", 2));

        // Act
        var result = await _agent.ExecuteFollowUpSearchesAsync(state, followUpQueries);

        // Assert
        result.SuccessfulResults.Should().HaveCount(1);
        state.ExecutedQueries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteSearchesAsync_UsesCorrectSearchType()
    {
        // Arrange
        var queries = new List<ExpandedQuery>
        {
            new() { Query = "news query", Intent = "news", Priority = 1, SearchType = QuerySearchType.News }
        };

        _mockProvider.SetupSearchResultForType("news query", SearchType.News, CreateSearchResult("news query", 1));

        // Act
        var result = await _agent.ExecuteSearchesAsync(queries);

        // Assert
        result.SuccessfulResults.Should().HaveCount(1);
        _mockProvider.LastSearchType.Should().Be(SearchType.News);
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

    private static SearchResult CreateSearchResult(string query, int sourceCount)
    {
        var sources = Enumerable.Range(1, sourceCount)
            .Select(i => new SearchSource
            {
                Url = $"https://example.com/{query.Replace(" ", "-")}/{i}",
                Title = $"Result {i} for {query}"
            })
            .ToList();

        return new SearchResult
        {
            Query = new SearchQuery { Query = query },
            Provider = "mock",
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = sources
        };
    }
}

/// <summary>
/// 테스트용 Mock SearchProvider
/// </summary>
internal class MockSearchProvider : ISearchProvider
{
    private readonly Dictionary<string, SearchResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _rateLimitCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SearchResult> _rateLimitSuccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SearchType Type, SearchResult Result)> _typeResults = new(StringComparer.OrdinalIgnoreCase);

    public SearchType? LastSearchType { get; private set; }

    public string ProviderId => "mock";

    public SearchCapabilities Capabilities =>
        SearchCapabilities.WebSearch | SearchCapabilities.NewsSearch;

    public void SetupSearchResult(string query, SearchResult result)
    {
        _results[query] = result;
    }

    public void SetupSearchResultForType(string query, SearchType type, SearchResult result)
    {
        _typeResults[query] = (type, result);
    }

    public void SetupException(string query, Exception exception)
    {
        _exceptions[query] = exception;
    }

    public void SetupDelay(string query, TimeSpan delay)
    {
        _delays[query] = delay;
    }

    public void SetupRateLimitThenSuccess(string query, SearchResult successResult)
    {
        _rateLimitCounts[query] = 0;
        _rateLimitSuccess[query] = successResult;
    }

    public async Task<SearchResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        LastSearchType = query.Type;

        // 딜레이 처리
        if (_delays.TryGetValue(query.Query, out var delay))
        {
            await Task.Delay(delay, cancellationToken);
        }

        // Rate limit 시뮬레이션
        if (_rateLimitCounts.TryGetValue(query.Query, out var count))
        {
            _rateLimitCounts[query.Query] = count + 1;
            if (count == 0)
            {
                throw new HttpRequestException("Rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
            }
            return _rateLimitSuccess[query.Query];
        }

        // 예외 처리
        if (_exceptions.TryGetValue(query.Query, out var exception))
        {
            throw exception;
        }

        // 타입별 결과
        if (_typeResults.TryGetValue(query.Query, out var typeResult))
        {
            return typeResult.Result;
        }

        // 일반 결과
        if (_results.TryGetValue(query.Query, out var result))
        {
            return result;
        }

        return new SearchResult
        {
            Query = query,
            Provider = ProviderId,
            SearchedAt = DateTimeOffset.UtcNow,
            Sources = []
        };
    }

    public async Task<IReadOnlyList<SearchResult>> SearchBatchAsync(
        IEnumerable<SearchQuery> queries,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        foreach (var query in queries)
        {
            results.Add(await SearchAsync(query, cancellationToken));
        }
        return results;
    }
}
