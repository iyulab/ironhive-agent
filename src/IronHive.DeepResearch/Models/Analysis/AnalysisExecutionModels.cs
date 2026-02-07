namespace IronHive.DeepResearch.Models.Analysis;

/// <summary>
/// 분석 실행 결과
/// </summary>
public record AnalysisResult
{
    /// <summary>
    /// 추출된 Findings
    /// </summary>
    public required IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>
    /// 식별된 정보 갭
    /// </summary>
    public required IReadOnlyList<InformationGap> Gaps { get; init; }

    /// <summary>
    /// 충분성 점수
    /// </summary>
    public required SufficiencyScore SufficiencyScore { get; init; }

    /// <summary>
    /// 분석된 소스 수
    /// </summary>
    public int SourcesAnalyzed { get; init; }

    /// <summary>
    /// 분석 시작 시간
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 분석 완료 시간
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// 소요 시간
    /// </summary>
    public TimeSpan Duration => CompletedAt - StartedAt;

    /// <summary>
    /// 추가 리서치 필요 여부
    /// </summary>
    public bool NeedsMoreResearch => !SufficiencyScore.IsSufficient && Gaps.Count > 0;
}

/// <summary>
/// 분석 옵션
/// </summary>
public record AnalysisOptions
{
    /// <summary>
    /// 소스당 최대 Finding 수
    /// </summary>
    public int MaxFindingsPerSource { get; init; } = 5;

    /// <summary>
    /// 최대 정보 갭 수
    /// </summary>
    public int MaxGaps { get; init; } = 5;

    /// <summary>
    /// 충분성 임계값 (0-1)
    /// </summary>
    public decimal SufficiencyThreshold { get; init; } = 0.8m;

    /// <summary>
    /// Finding 검증 활성화
    /// </summary>
    public bool EnableFindingVerification { get; init; } = true;

    /// <summary>
    /// 분석 언어
    /// </summary>
    public string Language { get; init; } = "ko";

    /// <summary>
    /// 분석에 포함할 최대 소스 수
    /// </summary>
    public int MaxSourcesToAnalyze { get; init; } = 20;
}

/// <summary>
/// Finding 추출 요청 (LLM용)
/// </summary>
internal record FindingExtractionRequest
{
    public required string SourceId { get; init; }
    public required string SourceUrl { get; init; }
    public required string Content { get; init; }
    public required string OriginalQuery { get; init; }
    public IReadOnlyList<string>? SubQuestions { get; init; }
}

/// <summary>
/// Finding 추출 응답 (LLM용)
/// </summary>
internal record FindingExtractionResponse
{
    public List<ExtractedFinding>? Findings { get; init; }
}

/// <summary>
/// 추출된 Finding (LLM 응답용)
/// </summary>
internal record ExtractedFinding
{
    public string Claim { get; init; } = "";
    public string? Evidence { get; init; }
    public decimal Confidence { get; init; } = 0.5m;
    public string? RelatedSubQuestion { get; init; }
}

/// <summary>
/// 갭 분석 응답 (LLM용)
/// </summary>
internal record GapAnalysisResponse
{
    public List<IdentifiedGap>? Gaps { get; init; }
    public decimal CoverageEstimate { get; init; }
    public string? Summary { get; init; }
}

/// <summary>
/// 식별된 갭 (LLM 응답용)
/// </summary>
internal record IdentifiedGap
{
    public string Description { get; init; } = "";
    public string SuggestedQuery { get; init; } = "";
    public string Priority { get; init; } = "medium";
    public string? Reason { get; init; }
}

/// <summary>
/// 충분성 평가 응답 (LLM용)
/// </summary>
internal record SufficiencyEvaluationResponse
{
    public decimal OverallScore { get; init; }
    public decimal CoverageScore { get; init; }
    public decimal QualityScore { get; init; }
    public decimal SourceDiversityScore { get; init; }
    public string? Reasoning { get; init; }
    public List<string>? StrengthAreas { get; init; }
    public List<string>? WeakAreas { get; init; }
}
