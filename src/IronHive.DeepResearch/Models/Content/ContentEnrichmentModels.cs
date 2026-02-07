namespace IronHive.DeepResearch.Models.Content;

/// <summary>
/// 콘텐츠 강화 결과
/// </summary>
public record ContentEnrichmentResult
{
    /// <summary>
    /// 성공적으로 생성된 소스 문서들
    /// </summary>
    public required IReadOnlyList<SourceDocument> Documents { get; init; }

    /// <summary>
    /// 실패한 URL들
    /// </summary>
    public required IReadOnlyList<FailedExtraction> FailedExtractions { get; init; }

    /// <summary>
    /// 총 처리된 URL 수
    /// </summary>
    public int TotalUrlsProcessed { get; init; }

    /// <summary>
    /// 생성된 청크 수
    /// </summary>
    public int TotalChunksCreated { get; init; }

    /// <summary>
    /// 시작 시간
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 완료 시간
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// 소요 시간
    /// </summary>
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// 실패한 추출 정보
/// </summary>
public record FailedExtraction
{
    /// <summary>
    /// 실패한 URL
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// 에러 메시지
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// 에러 유형
    /// </summary>
    public ExtractionErrorType ErrorType { get; init; }
}

/// <summary>
/// 추출 에러 유형
/// </summary>
public enum ExtractionErrorType
{
    /// <summary>
    /// 알 수 없는 에러
    /// </summary>
    Unknown,

    /// <summary>
    /// 네트워크 에러
    /// </summary>
    NetworkError,

    /// <summary>
    /// 타임아웃
    /// </summary>
    Timeout,

    /// <summary>
    /// 접근 거부
    /// </summary>
    AccessDenied,

    /// <summary>
    /// 콘텐츠 없음
    /// </summary>
    NoContent,

    /// <summary>
    /// 파싱 에러
    /// </summary>
    ParseError,

    /// <summary>
    /// 지원하지 않는 콘텐츠 유형
    /// </summary>
    UnsupportedContentType
}

/// <summary>
/// 콘텐츠 강화 옵션
/// </summary>
public record ContentEnrichmentOptions
{
    /// <summary>
    /// 최대 병렬 추출 수
    /// </summary>
    public int MaxParallelExtractions { get; init; } = 10;

    /// <summary>
    /// URL당 타임아웃
    /// </summary>
    public TimeSpan ExtractionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 청킹 활성화 여부
    /// </summary>
    public bool EnableChunking { get; init; } = true;

    /// <summary>
    /// 청킹 옵션
    /// </summary>
    public ChunkingOptions ChunkingOptions { get; init; } = new();

    /// <summary>
    /// 최대 콘텐츠 길이 (문자 수)
    /// </summary>
    public int MaxContentLength { get; init; } = 50000;

    /// <summary>
    /// 검색 결과의 RawContent 사용 여부
    /// </summary>
    public bool UseRawContentWhenAvailable { get; init; } = true;

    /// <summary>
    /// 메타데이터 추출 여부
    /// </summary>
    public bool ExtractMetadata { get; init; } = true;

    /// <summary>
    /// 링크 추출 여부
    /// </summary>
    public bool ExtractLinks { get; init; } = false;

    /// <summary>
    /// 실패 시 계속 진행
    /// </summary>
    public bool ContinueOnError { get; init; } = true;
}

/// <summary>
/// 콘텐츠 강화 진행 상태
/// </summary>
public record ContentEnrichmentProgress
{
    /// <summary>
    /// 총 URL 수
    /// </summary>
    public int TotalUrls { get; init; }

    /// <summary>
    /// 완료된 URL 수
    /// </summary>
    public int CompletedUrls { get; init; }

    /// <summary>
    /// 성공한 URL 수
    /// </summary>
    public int SuccessfulUrls { get; init; }

    /// <summary>
    /// 실패한 URL 수
    /// </summary>
    public int FailedUrls { get; init; }

    /// <summary>
    /// 생성된 청크 수
    /// </summary>
    public int ChunksCreated { get; init; }

    /// <summary>
    /// 진행률 (0-1)
    /// </summary>
    public double ProgressRatio => TotalUrls > 0 ? (double)CompletedUrls / TotalUrls : 0;
}
