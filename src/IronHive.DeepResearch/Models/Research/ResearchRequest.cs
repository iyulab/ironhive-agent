namespace IronHive.DeepResearch.Models.Research;

/// <summary>
/// 리서치 요청
/// </summary>
public record ResearchRequest
{
    /// <summary>
    /// 사용자 질의
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// 리서치 깊이
    /// </summary>
    public ResearchDepth Depth { get; init; } = ResearchDepth.Standard;

    /// <summary>
    /// 출력 형식
    /// </summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Markdown;

    /// <summary>
    /// 출력 언어
    /// </summary>
    public string Language { get; init; } = "ko";

    /// <summary>
    /// 최대 반복 횟수
    /// </summary>
    public int MaxIterations { get; init; } = 5;

    /// <summary>
    /// 반복당 최대 소스 수
    /// </summary>
    public int MaxSourcesPerIteration { get; init; } = 10;

    /// <summary>
    /// 비용 한도 (USD)
    /// </summary>
    public decimal? MaxBudget { get; init; }

    /// <summary>
    /// 검색 프로바이더 우선순위
    /// </summary>
    public IReadOnlyList<string>? PreferredProviders { get; init; }

    /// <summary>
    /// 학술 검색 포함 여부
    /// </summary>
    public bool IncludeAcademic { get; init; } = false;

    /// <summary>
    /// 뉴스 검색 포함 여부
    /// </summary>
    public bool IncludeNews { get; init; } = false;

    /// <summary>
    /// 포함할 도메인 목록
    /// </summary>
    public IReadOnlyList<string>? IncludeDomains { get; init; }

    /// <summary>
    /// 제외할 도메인 목록
    /// </summary>
    public IReadOnlyList<string>? ExcludeDomains { get; init; }
}

/// <summary>
/// 리서치 깊이
/// </summary>
public enum ResearchDepth
{
    /// <summary>
    /// Quick: 1-2분, 3회 이내 반복
    /// </summary>
    Quick,

    /// <summary>
    /// Standard: 3-5분, 5회 이내 반복
    /// </summary>
    Standard,

    /// <summary>
    /// Comprehensive: 10-15분, 10회 이내 반복
    /// </summary>
    Comprehensive
}

/// <summary>
/// 출력 형식
/// </summary>
public enum OutputFormat
{
    Markdown,
    Html,
    Pdf,
    Json
}
