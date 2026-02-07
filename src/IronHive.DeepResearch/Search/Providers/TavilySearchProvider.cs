using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Search.Caching;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Search.Providers;

/// <summary>
/// Tavily Search API 프로바이더
/// https://docs.tavily.com/
/// </summary>
public class TavilySearchProvider : ISearchProvider, IDisposable
{
    private bool _disposed;
    private readonly HttpClient _httpClient;
    private readonly ISearchResultCache _cache;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<TavilySearchProvider> _logger;

    private const string BaseUrl = "https://api.tavily.com";

    public string ProviderId => "tavily";

    public SearchCapabilities Capabilities =>
        SearchCapabilities.WebSearch |
        SearchCapabilities.NewsSearch |
        SearchCapabilities.ContentExtraction;

    public TavilySearchProvider(
        HttpClient httpClient,
        ISearchResultCache cache,
        DeepResearchOptions options,
        ILogger<TavilySearchProvider> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options;
        _logger = logger;

        // API 키 설정
        if (_options.SearchApiKeys.TryGetValue("tavily", out var apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<SearchResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        // 1. 캐시 확인
        var cacheKey = _cache.GenerateKey(query);
        if (_cache.TryGet(cacheKey, out var cached) && cached != null)
        {
            _logger.LogInformation("Returning cached result for query: {Query}", query.Query);
            return cached;
        }

        // 2. API 요청 생성
        var request = new TavilySearchRequest
        {
            Query = query.Query,
            SearchDepth = query.Depth == QueryDepth.Deep ? "advanced" : "basic",
            IncludeAnswer = true,
            IncludeRawContent = query.IncludeContent,
            MaxResults = query.MaxResults,
            IncludeDomains = query.IncludeDomains?.ToList(),
            ExcludeDomains = query.ExcludeDomains?.ToList()
        };

        _logger.LogInformation("Executing Tavily search: {Query}, Depth: {Depth}",
            query.Query, request.SearchDepth);

        // 3. API 호출
        var response = await _httpClient.PostAsJsonAsync(
            $"{BaseUrl}/search",
            request,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Tavily API error: {StatusCode} - {Error}",
                response.StatusCode, error);
            throw new HttpRequestException($"Tavily API error: {response.StatusCode} - {error}");
        }

        var tavilyResponse = await response.Content
            .ReadFromJsonAsync<TavilySearchResponse>(cancellationToken);

        if (tavilyResponse == null)
        {
            throw new InvalidOperationException("Failed to deserialize Tavily response");
        }

        // 4. 결과 매핑
        var result = MapToSearchResult(tavilyResponse, query);

        // 5. 캐시 저장
        _cache.Set(cacheKey, result, TimeSpan.FromHours(1));

        _logger.LogInformation("Tavily search completed: {ResultCount} results",
            result.Sources.Count);

        return result;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchBatchAsync(
        IEnumerable<SearchQuery> queries,
        CancellationToken cancellationToken = default)
    {
        var queryList = queries.ToList();
        _logger.LogInformation("Executing batch search: {Count} queries", queryList.Count);

        // 병렬 실행 (최대 N개 동시)
        using var semaphore = new SemaphoreSlim(_options.MaxParallelSearches);
        var tasks = queryList.Select(async query =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await SearchAsync(query, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Search failed for query: {Query}", query.Query);
                // 실패한 쿼리는 빈 결과 반환
                return new SearchResult
                {
                    Query = query,
                    Provider = ProviderId,
                    Sources = [],
                    SearchedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    private SearchResult MapToSearchResult(TavilySearchResponse response, SearchQuery query)
    {
        return new SearchResult
        {
            Query = query,
            Provider = ProviderId,
            Answer = response.Answer,
            Sources = response.Results?.Select(r => new SearchSource
            {
                Url = r.Url,
                Title = r.Title,
                Snippet = r.Content,
                RawContent = r.RawContent,
                Score = r.Score,
                PublishedDate = ParseDate(r.PublishedDate)
            }).ToList() ?? [],
            SearchedAt = DateTimeOffset.UtcNow
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // HttpClient is injected via DI — not owned by this class
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private static DateTimeOffset? ParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
            return null;

        if (DateTimeOffset.TryParse(dateStr, out var date))
            return date;

        return null;
    }
}

#region Tavily API Models

internal class TavilySearchRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("search_depth")]
    public string SearchDepth { get; init; } = "basic";

    [JsonPropertyName("include_answer")]
    public bool IncludeAnswer { get; init; } = true;

    [JsonPropertyName("include_raw_content")]
    public bool IncludeRawContent { get; init; } = false;

    [JsonPropertyName("max_results")]
    public int MaxResults { get; init; } = 10;

    [JsonPropertyName("include_domains")]
    public List<string>? IncludeDomains { get; init; }

    [JsonPropertyName("exclude_domains")]
    public List<string>? ExcludeDomains { get; init; }
}

internal class TavilySearchResponse
{
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("answer")]
    public string? Answer { get; init; }

    [JsonPropertyName("results")]
    public List<TavilyResult>? Results { get; init; }

    [JsonPropertyName("response_time")]
    public double ResponseTime { get; init; }
}

internal class TavilyResult
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("raw_content")]
    public string? RawContent { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("published_date")]
    public string? PublishedDate { get; init; }
}

#endregion
