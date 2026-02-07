namespace IronHive.DeepResearch.Options;

/// <summary>
/// 딥리서치 설정 옵션
/// </summary>
public class DeepResearchOptions
{
    /// <summary>
    /// 기본 검색 프로바이더
    /// </summary>
    public string DefaultSearchProvider { get; set; } = "tavily";

    /// <summary>
    /// 검색 API 키들
    /// </summary>
    public Dictionary<string, string> SearchApiKeys { get; set; } = new();

    /// <summary>
    /// 체크포인트 저장 경로
    /// </summary>
    public string? CheckpointBasePath { get; set; }

    /// <summary>
    /// 기본 최대 반복 횟수
    /// </summary>
    public int DefaultMaxIterations { get; set; } = 5;

    /// <summary>
    /// 반복당 기본 최대 소스 수
    /// </summary>
    public int DefaultMaxSourcesPerIteration { get; set; } = 10;

    /// <summary>
    /// 기본 비용 한도 (USD)
    /// </summary>
    public decimal DefaultMaxBudget { get; set; } = 1.0m;

    /// <summary>
    /// 충분성 임계값 (0-1, 기본 0.8)
    /// </summary>
    public decimal SufficiencyThreshold { get; set; } = 0.8m;

    /// <summary>
    /// 병렬 검색 최대 수
    /// </summary>
    public int MaxParallelSearches { get; set; } = 5;

    /// <summary>
    /// 병렬 콘텐츠 추출 최대 수
    /// </summary>
    public int MaxParallelExtractions { get; set; } = 10;

    /// <summary>
    /// 분석용 경량 모델 사용 여부
    /// </summary>
    public bool UseSmallModelForAnalysis { get; set; } = true;

    /// <summary>
    /// 분석용 모델 ID
    /// </summary>
    public string? AnalysisModelId { get; set; }

    /// <summary>
    /// 합성용 모델 ID
    /// </summary>
    public string? SynthesisModelId { get; set; }

    /// <summary>
    /// 세션 만료 시간
    /// </summary>
    public TimeSpan SessionExpiration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// HTTP 요청 타임아웃
    /// </summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 재시도 횟수
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// WebFlux 패키지 기반 콘텐츠 추출 사용 여부
    /// true: WebFlux의 ICrawler + IContentExtractor 사용 (고급 기능)
    /// false: 기본 HttpClient + ContentProcessor 사용 (경량)
    /// </summary>
    public bool UseWebFluxPackage { get; set; } = false;

    /// <summary>
    /// 보고서 생성 전 필요한 최소 소스 수
    /// 이 수 이상의 소스를 얻을 때까지 검색을 계속 시도
    /// </summary>
    public int MinSourcesBeforeReport { get; set; } = 1;

    /// <summary>
    /// 검색 결과 없을 때 재시도 대기 시간 (봇 보호 우회용)
    /// </summary>
    public TimeSpan RetryDelayOnNoResults { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 소스 없이 최대 재시도 횟수 (반복당)
    /// </summary>
    public int MaxSearchRetriesPerIteration { get; set; } = 3;
}
