namespace IronHive.DeepResearch.Models.Analysis;

/// <summary>
/// 발견 사항
/// </summary>
public record Finding
{
    public required string Id { get; init; }
    public required string Claim { get; init; }
    public required string SourceId { get; init; }
    public string? EvidenceQuote { get; init; }
    public decimal VerificationScore { get; init; }
    public bool IsVerified { get; init; }
    public required int IterationDiscovered { get; init; }
    public required DateTimeOffset DiscoveredAt { get; init; }
}

/// <summary>
/// 정보 갭
/// </summary>
public record InformationGap
{
    public required string Description { get; init; }
    public required GapPriority Priority { get; init; }
    public required string SuggestedQuery { get; init; }
    public required DateTimeOffset IdentifiedAt { get; init; }
}

/// <summary>
/// 갭 우선순위
/// </summary>
public enum GapPriority
{
    Low,
    Medium,
    High
}

/// <summary>
/// 충분성 점수
/// </summary>
public record SufficiencyScore
{
    /// <summary>
    /// 종합 점수 (0-1)
    /// </summary>
    public decimal OverallScore { get; init; }

    /// <summary>
    /// 커버리지 점수 (하위 질문 답변율)
    /// </summary>
    public decimal CoverageScore { get; init; }

    /// <summary>
    /// 소스 다양성 점수
    /// </summary>
    public decimal SourceDiversityScore { get; init; }

    /// <summary>
    /// 품질 점수 (LLM 평가)
    /// </summary>
    public decimal QualityScore { get; init; }

    /// <summary>
    /// 정보 신선도 점수
    /// </summary>
    public decimal FreshnessScore { get; init; }

    /// <summary>
    /// 이번 반복에서 발견된 신규 항목 수
    /// </summary>
    public int NewFindingsCount { get; init; }

    /// <summary>
    /// 평가 시간
    /// </summary>
    public DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>
    /// 충분하다고 판정하는 종합 점수 하한 (0-1). 평가기가 <see cref="Options.DeepResearchOptions.SufficiencyThreshold"/>
    /// (또는 <see cref="AnalysisOptions.SufficiencyThreshold"/>)로 채운다.
    /// </summary>
    public decimal Threshold { get; init; } = 0.8m;

    /// <summary>
    /// 충분 여부 — <see cref="OverallScore"/> 가 <see cref="Threshold"/> 이상
    /// </summary>
    public bool IsSufficient => OverallScore >= Threshold;
}
