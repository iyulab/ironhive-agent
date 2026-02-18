using IronHive.DeepResearch.Models.Search;

namespace IronHive.DeepResearch.Search.Caching;

/// <summary>
/// 검색 결과 캐시 인터페이스
/// </summary>
public interface ISearchResultCache
{
    /// <summary>
    /// 캐시에서 검색 결과 조회
    /// </summary>
    bool TryGet(string cacheKey, out SearchResult? result);

    /// <summary>
    /// 검색 결과를 캐시에 저장
    /// </summary>
    void SetEntry(string cacheKey, SearchResult result, TimeSpan? expiration = null);

    /// <summary>
    /// 캐시 키 생성
    /// </summary>
    string GenerateKey(SearchQuery query);

    /// <summary>
    /// 캐시 무효화
    /// </summary>
    void Invalidate(string cacheKey);

    /// <summary>
    /// 전체 캐시 클리어
    /// </summary>
    void Clear();
}
