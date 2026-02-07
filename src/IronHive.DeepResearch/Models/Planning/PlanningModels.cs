namespace IronHive.DeepResearch.Models.Planning;

/// <summary>
/// 쿼리 계획 결과
/// </summary>
public record QueryPlanResult
{
    /// <summary>
    /// 초기 검색 쿼리 목록
    /// </summary>
    public required IReadOnlyList<ExpandedQuery> InitialQueries { get; init; }

    /// <summary>
    /// 발견된 리서치 관점 (STORM 패턴)
    /// </summary>
    public required IReadOnlyList<ResearchPerspective> Perspectives { get; init; }

    /// <summary>
    /// 분해된 하위 질문
    /// </summary>
    public required IReadOnlyList<SubQuestion> SubQuestions { get; init; }

    /// <summary>
    /// 계획 생성 시간
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// 확장된 쿼리
/// </summary>
public record ExpandedQuery
{
    /// <summary>
    /// 검색 쿼리 문자열
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// 쿼리 의도
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>
    /// 우선순위 (1=최고, 3=낮음)
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// 검색 유형
    /// </summary>
    public QuerySearchType SearchType { get; init; } = QuerySearchType.Web;

    /// <summary>
    /// 연관된 관점 ID
    /// </summary>
    public string? PerspectiveId { get; init; }

    /// <summary>
    /// 연관된 하위 질문 ID
    /// </summary>
    public string? SubQuestionId { get; init; }
}

/// <summary>
/// 쿼리 검색 유형
/// </summary>
public enum QuerySearchType
{
    Web,
    News,
    Academic
}

/// <summary>
/// 리서치 관점 (STORM 패턴)
/// </summary>
public record ResearchPerspective
{
    /// <summary>
    /// 관점 ID
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 관점 이름 (예: "기술적 관점", "경제적 관점")
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 관점 설명
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// 이 관점에서 탐색할 핵심 주제
    /// </summary>
    public IReadOnlyList<string> KeyTopics { get; init; } = [];
}

/// <summary>
/// 분해된 하위 질문 (Self-Ask 패턴)
/// </summary>
public record SubQuestion
{
    /// <summary>
    /// 하위 질문 ID
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 질문 내용
    /// </summary>
    public required string Question { get; init; }

    /// <summary>
    /// 질문의 목적
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// 우선순위
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// 의존하는 다른 하위 질문 ID
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// 쿼리 확장 옵션
/// </summary>
public record QueryExpansionOptions
{
    /// <summary>
    /// 최대 하위 질문 수
    /// </summary>
    public int MaxSubQuestions { get; init; } = 10;

    /// <summary>
    /// 최대 관점 수
    /// </summary>
    public int MaxPerspectives { get; init; } = 5;

    /// <summary>
    /// 최대 확장 쿼리 수
    /// </summary>
    public int MaxExpandedQueries { get; init; } = 15;

    /// <summary>
    /// 학술 검색 포함 여부
    /// </summary>
    public bool IncludeAcademic { get; init; } = false;

    /// <summary>
    /// 뉴스 검색 포함 여부
    /// </summary>
    public bool IncludeNews { get; init; } = false;

    /// <summary>
    /// 출력 언어
    /// </summary>
    public string Language { get; init; } = "ko";
}
