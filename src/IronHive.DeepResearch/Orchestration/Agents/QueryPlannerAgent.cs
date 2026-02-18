using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Orchestration.State;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// 쿼리 계획 에이전트 (Self-Ask + STORM 패턴)
/// </summary>
public partial class QueryPlannerAgent
{
    private readonly IQueryExpander _queryExpander;
    private readonly ILogger<QueryPlannerAgent> _logger;

    public QueryPlannerAgent(
        IQueryExpander queryExpander,
        ILogger<QueryPlannerAgent> logger)
    {
        _queryExpander = queryExpander;
        _logger = logger;
    }

    /// <summary>
    /// 리서치 쿼리 계획 생성
    /// </summary>
    public virtual async Task<QueryPlanResult> PlanAsync(
        ResearchState state,
        CancellationToken cancellationToken = default)
    {
        LogQueryPlanStarting(_logger, state.Request.Query);

        var options = CreateExpansionOptions(state.Request);

        // 1. Self-Ask: 질문 분해
        LogDecomposingQuery(_logger);
        var subQuestions = await _queryExpander.DecomposeQueryAsync(
            state.Request.Query, options, cancellationToken);

        LogSubQuestionsGenerated(_logger, subQuestions.Count);

        // 2. STORM: 관점 발견
        LogDiscoveringPerspectives(_logger);
        var perspectives = await _queryExpander.DiscoverPerspectivesAsync(
            state.Request.Query, options, cancellationToken);

        LogPerspectivesDiscovered(_logger, perspectives.Count);

        // 3. 쿼리 확장 (관점 x 하위질문)
        LogExpandingQueries(_logger);
        var expandedQueries = await _queryExpander.ExpandQueriesAsync(
            state.Request.Query,
            subQuestions,
            perspectives,
            options,
            cancellationToken);

        LogExpandedQueriesGenerated(_logger, expandedQueries.Count);

        // 4. 중복 제거 및 우선순위 정렬
        var prioritizedQueries = DeduplicateAndPrioritize(expandedQueries);

        LogQueryPlanCompleted(_logger, subQuestions.Count, perspectives.Count, prioritizedQueries.Count);

        return new QueryPlanResult
        {
            InitialQueries = prioritizedQueries,
            Perspectives = perspectives,
            SubQuestions = subQuestions,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// 추가 쿼리 생성 (정보 갭 기반)
    /// </summary>
    public async Task<IReadOnlyList<ExpandedQuery>> GenerateFollowUpQueriesAsync(
        ResearchState state,
        CancellationToken cancellationToken = default)
    {
        if (state.IdentifiedGaps.Count == 0)
        {
            LogNoGapsIdentified(_logger);
            return [];
        }

        LogGeneratingFollowUpQueries(_logger, state.IdentifiedGaps.Count);

        var options = CreateExpansionOptions(state.Request);

        // 갭에서 제안된 쿼리 추출
        var gapQueries = state.IdentifiedGaps
            .Where(g => !string.IsNullOrWhiteSpace(g.SuggestedQuery))
            .OrderByDescending(g => g.Priority)
            .Select(g => new SubQuestion
            {
                Id = $"gap_{Guid.NewGuid():N}",
                Question = g.SuggestedQuery,
                Purpose = g.Description,
                Priority = g.Priority == Models.Analysis.GapPriority.High ? 1 :
                          g.Priority == Models.Analysis.GapPriority.Medium ? 2 : 3
            })
            .Take(options.MaxSubQuestions)
            .ToList();

        if (gapQueries.Count == 0)
        {
            return [];
        }

        // 기존 관점 활용
        var perspectives = state.ResearchAngles
            .Select((angle, i) => new ResearchPerspective
            {
                Id = $"angle_{i}",
                Name = angle,
                Description = angle
            })
            .ToList();

        if (perspectives.Count == 0)
        {
            perspectives = [new ResearchPerspective
            {
                Id = "default",
                Name = "일반적 관점",
                Description = "추가 정보 수집"
            }];
        }

        // 쿼리 확장
        var expandedQueries = await _queryExpander.ExpandQueriesAsync(
            state.Request.Query,
            gapQueries,
            perspectives,
            options,
            cancellationToken);

        // 이미 실행된 쿼리 제외
        var executedQueryTexts = state.ExecutedQueries
            .Select(q => q.Query.ToLowerInvariant())
            .ToHashSet();

        var newQueries = expandedQueries
            .Where(q => !executedQueryTexts.Contains(q.Query.ToLowerInvariant()))
            .ToList();

        LogFollowUpQueriesGenerated(_logger, newQueries.Count);

        return newQueries;
    }

    private static QueryExpansionOptions CreateExpansionOptions(ResearchRequest request)
    {
        var (maxSub, maxPersp, maxQueries) = request.Depth switch
        {
            ResearchDepth.Quick => (5, 3, 8),
            ResearchDepth.Standard => (8, 4, 12),
            ResearchDepth.Comprehensive => (12, 6, 18),
            _ => (8, 4, 12)
        };

        return new QueryExpansionOptions
        {
            MaxSubQuestions = maxSub,
            MaxPerspectives = maxPersp,
            MaxExpandedQueries = maxQueries,
            IncludeAcademic = request.IncludeAcademic,
            IncludeNews = request.IncludeNews,
            Language = request.Language
        };
    }

    private static List<ExpandedQuery> DeduplicateAndPrioritize(
        IReadOnlyList<ExpandedQuery> queries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExpandedQuery>();

        foreach (var query in queries.OrderBy(q => q.Priority))
        {
            // 정규화된 쿼리로 중복 체크
            var normalized = NormalizeQuery(query.Query);
            if (seen.Add(normalized))
            {
                result.Add(query);
            }
        }

        return result;
    }

    private static string NormalizeQuery(string query)
    {
        // 간단한 정규화: 소문자 + 연속 공백 제거
        return string.Join(' ', query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "쿼리 계획 시작: {Query}")]
    private static partial void LogQueryPlanStarting(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "질문 분해 시작 (Self-Ask 패턴)")]
    private static partial void LogDecomposingQuery(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "하위 질문 {Count}개 생성됨")]
    private static partial void LogSubQuestionsGenerated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "관점 발견 시작 (STORM 패턴)")]
    private static partial void LogDiscoveringPerspectives(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "리서치 관점 {Count}개 발견됨")]
    private static partial void LogPerspectivesDiscovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "쿼리 확장 시작")]
    private static partial void LogExpandingQueries(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "확장된 쿼리 {Count}개 생성됨")]
    private static partial void LogExpandedQueriesGenerated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "쿼리 계획 완료: 하위질문 {SubQCount}개, 관점 {PerspCount}개, 쿼리 {QueryCount}개")]
    private static partial void LogQueryPlanCompleted(ILogger logger, int subQCount, int perspCount, int queryCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "식별된 정보 갭 없음")]
    private static partial void LogNoGapsIdentified(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "정보 갭 기반 후속 쿼리 생성: {GapCount}개 갭")]
    private static partial void LogGeneratingFollowUpQueries(ILogger logger, int gapCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "후속 쿼리 {Count}개 생성됨 (중복 제외 후)")]
    private static partial void LogFollowUpQueriesGenerated(ILogger logger, int count);

    #endregion
}
