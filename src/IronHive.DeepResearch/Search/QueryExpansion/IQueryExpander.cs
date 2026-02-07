using IronHive.DeepResearch.Models.Planning;

namespace IronHive.DeepResearch.Search.QueryExpansion;

/// <summary>
/// 쿼리 확장 인터페이스
/// </summary>
public interface IQueryExpander
{
    /// <summary>
    /// 쿼리를 하위 질문으로 분해 (Self-Ask 패턴)
    /// </summary>
    Task<IReadOnlyList<SubQuestion>> DecomposeQueryAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 쿼리에서 리서치 관점 발견 (STORM 패턴)
    /// </summary>
    Task<IReadOnlyList<ResearchPerspective>> DiscoverPerspectivesAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 하위 질문과 관점을 기반으로 검색 쿼리 확장
    /// </summary>
    Task<IReadOnlyList<ExpandedQuery>> ExpandQueriesAsync(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions,
        IReadOnlyList<ResearchPerspective> perspectives,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default);
}
