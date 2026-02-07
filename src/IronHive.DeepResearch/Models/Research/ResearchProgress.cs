namespace IronHive.DeepResearch.Models.Research;

/// <summary>
/// 리서치 진행 상황 (스트리밍용)
/// </summary>
public record ResearchProgress
{
    public required ProgressType Type { get; init; }
    public required int CurrentIteration { get; init; }
    public required int MaxIterations { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    // Type별 데이터 (하나만 설정됨)
    public PlanProgress? Plan { get; init; }
    public SearchProgress? Search { get; init; }
    public ContentProgress? Content { get; init; }
    public AnalysisProgress? Analysis { get; init; }
    public string? ReportChunk { get; init; }
    public ResearchResult? Result { get; init; }
    public ResearchError? Error { get; init; }
}

/// <summary>
/// 진행 유형
/// </summary>
public enum ProgressType
{
    Started,
    PlanGenerated,
    SearchStarted,
    SearchCompleted,
    ContentExtractionStarted,
    ContentExtracted,
    AnalysisStarted,
    AnalysisCompleted,
    SufficiencyEvaluated,
    IterationCompleted,
    ReportGenerationStarted,
    ReportSection,
    ReportChunk,
    Completed,
    Failed,
    Checkpoint
}

/// <summary>
/// 계획 진행 상황
/// </summary>
public record PlanProgress
{
    public required IReadOnlyList<string> GeneratedQueries { get; init; }
    public required IReadOnlyList<string> ResearchAngles { get; init; }
}

/// <summary>
/// 검색 진행 상황
/// </summary>
public record SearchProgress
{
    public required string Query { get; init; }
    public required string Provider { get; init; }
    public required int ResultCount { get; init; }
}

/// <summary>
/// 콘텐츠 추출 진행 상황
/// </summary>
public record ContentProgress
{
    public required string Url { get; init; }
    public required int ContentLength { get; init; }
    public required bool Success { get; init; }
}

/// <summary>
/// 분석 진행 상황
/// </summary>
public record AnalysisProgress
{
    public required int FindingsCount { get; init; }
    public required int GapsIdentified { get; init; }
    public required SufficiencyScore Score { get; init; }
}
