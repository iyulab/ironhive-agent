namespace IronHive.DeepResearch.Models.Research;

/// <summary>
/// 리서치 결과
/// </summary>
public record ResearchResult
{
    /// <summary>
    /// 세션 ID
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 원본 쿼리
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// 생성된 보고서 (본문만)
    /// </summary>
    public required string Report { get; init; }

    /// <summary>
    /// 보고서 섹션들 (구조화된 형태)
    /// </summary>
    public required IReadOnlyList<ReportSection> Sections { get; init; }

    /// <summary>
    /// 보고서에서 인용된 소스 (사용된 소스)
    /// </summary>
    public required IReadOnlyList<SourceDocument> CitedSources { get; init; }

    /// <summary>
    /// 수집되었지만 인용되지 않은 소스 (참고용)
    /// </summary>
    public required IReadOnlyList<SourceDocument> UncitedSources { get; init; }

    /// <summary>
    /// 수집된 모든 소스 (CitedSources + UncitedSources)
    /// </summary>
    public IReadOnlyList<SourceDocument> AllSources =>
        CitedSources.Concat(UncitedSources).ToList();

    /// <summary>
    /// 인용 정보 (번호, URL 등)
    /// </summary>
    public required IReadOnlyList<Citation> Citations { get; init; }

    /// <summary>
    /// 리서치 사고 과정 로그
    /// </summary>
    public required IReadOnlyList<ThinkingStep> ThinkingProcess { get; init; }

    /// <summary>
    /// 메타데이터
    /// </summary>
    public required ResearchMetadata Metadata { get; init; }

    /// <summary>
    /// 발생한 오류 목록
    /// </summary>
    public IReadOnlyList<ResearchError> Errors { get; init; } = [];

    /// <summary>
    /// 부분 결과 여부
    /// </summary>
    public bool IsPartial { get; init; } = false;

    // 하위 호환성을 위한 별칭
    [Obsolete("Use CitedSources or AllSources instead")]
    public IReadOnlyList<SourceDocument> Sources => AllSources;
}

/// <summary>
/// 리서치 메타데이터
/// </summary>
public record ResearchMetadata
{
    public required int IterationCount { get; init; }
    public required int TotalQueriesExecuted { get; init; }
    public required int TotalSourcesAnalyzed { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TokenUsage TokenUsage { get; init; }
    public required decimal EstimatedCost { get; init; }
    public required SufficiencyScore FinalSufficiencyScore { get; init; }
}

/// <summary>
/// 토큰 사용량
/// </summary>
public record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// 보고서 섹션
/// </summary>
public record ReportSection
{
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required int Order { get; init; }
    public IReadOnlyList<string> RelatedFindings { get; init; } = [];
}

/// <summary>
/// 인용 정보
/// </summary>
public record Citation
{
    public required int Number { get; init; }
    public required string SourceId { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? PublishedDate { get; init; }
    public required DateTimeOffset AccessedDate { get; init; }
}

/// <summary>
/// 리서치 오류
/// </summary>
public record ResearchError
{
    public required ResearchErrorType Type { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? Details { get; init; }
}

/// <summary>
/// 리서치 오류 유형
/// </summary>
public enum ResearchErrorType
{
    SearchProviderError,
    ContentExtractionError,
    LLMError,
    BudgetExceeded,
    TimeoutExceeded,
    InsufficientSources,
    Unknown
}

/// <summary>
/// 리서치 사고 과정 단계
/// </summary>
public record ThinkingStep
{
    /// <summary>
    /// 단계 유형
    /// </summary>
    public required ThinkingStepType Type { get; init; }

    /// <summary>
    /// 단계 제목
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// 상세 설명
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// 단계 시작 시간
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 소요 시간
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// 관련 데이터 (쿼리, 소스 ID 등)
    /// </summary>
    public IReadOnlyDictionary<string, object>? Data { get; init; }
}

/// <summary>
/// 사고 과정 단계 유형
/// </summary>
public enum ThinkingStepType
{
    /// <summary>
    /// 쿼리 분석 및 계획 수립
    /// </summary>
    Planning,

    /// <summary>
    /// 검색 쿼리 생성
    /// </summary>
    QueryGeneration,

    /// <summary>
    /// 검색 실행
    /// </summary>
    Searching,

    /// <summary>
    /// 콘텐츠 추출 및 분석
    /// </summary>
    ContentExtraction,

    /// <summary>
    /// 정보 충분성 평가
    /// </summary>
    SufficiencyEvaluation,

    /// <summary>
    /// 추가 검색 결정
    /// </summary>
    IterationDecision,

    /// <summary>
    /// 발견 사항 종합
    /// </summary>
    FindingSynthesis,

    /// <summary>
    /// 보고서 구조 설계
    /// </summary>
    OutlineGeneration,

    /// <summary>
    /// 보고서 섹션 작성
    /// </summary>
    SectionWriting,

    /// <summary>
    /// 최종 검토
    /// </summary>
    FinalReview
}
