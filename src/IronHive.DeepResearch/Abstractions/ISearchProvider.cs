namespace IronHive.DeepResearch.Abstractions;

/// <summary>
/// 검색 프로바이더 인터페이스
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// 프로바이더 식별자
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// 지원하는 검색 기능
    /// </summary>
    SearchCapabilities Capabilities { get; }

    /// <summary>
    /// 검색 실행
    /// </summary>
    Task<SearchResult> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 배치 검색 실행
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SearchBatchAsync(
        IEnumerable<SearchQuery> queries,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 검색 기능 플래그
/// </summary>
[Flags]
public enum SearchCapabilities
{
    None = 0,

    /// <summary>
    /// 일반 웹 검색
    /// </summary>
    WebSearch = 1,

    /// <summary>
    /// 뉴스 검색
    /// </summary>
    NewsSearch = 2,

    /// <summary>
    /// 학술 검색
    /// </summary>
    AcademicSearch = 4,

    /// <summary>
    /// 이미지 검색
    /// </summary>
    ImageSearch = 8,

    /// <summary>
    /// 콘텐츠 추출 통합 (Tavily 등)
    /// </summary>
    ContentExtraction = 16,

    /// <summary>
    /// 시맨틱 검색 (Exa 등)
    /// </summary>
    SemanticSearch = 32
}
