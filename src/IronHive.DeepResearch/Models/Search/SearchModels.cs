namespace IronHive.DeepResearch.Models.Search;

/// <summary>
/// 검색 쿼리
/// </summary>
public record SearchQuery
{
    public required string Query { get; init; }
    public SearchType Type { get; init; } = SearchType.Web;
    public QueryDepth Depth { get; init; } = QueryDepth.Basic;
    public int MaxResults { get; init; } = 10;
    public bool IncludeContent { get; init; } = false;
    public IReadOnlyList<string>? IncludeDomains { get; init; }
    public IReadOnlyList<string>? ExcludeDomains { get; init; }
}

/// <summary>
/// 검색 유형
/// </summary>
public enum SearchType
{
    Web,
    News,
    Academic,
    Image
}

/// <summary>
/// 검색 깊이
/// </summary>
public enum QueryDepth
{
    Basic,
    Deep
}

/// <summary>
/// 검색 결과
/// </summary>
public record SearchResult
{
    public required SearchQuery Query { get; init; }
    public required string Provider { get; init; }
    public string? Answer { get; init; }
    public required IReadOnlyList<SearchSource> Sources { get; init; }
    public required DateTimeOffset SearchedAt { get; init; }
}

/// <summary>
/// 검색 소스
/// </summary>
public record SearchSource
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Snippet { get; init; }
    public string? RawContent { get; init; }
    public double Score { get; init; }
    public DateTimeOffset? PublishedDate { get; init; }
}
