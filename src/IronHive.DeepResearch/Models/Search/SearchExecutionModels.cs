namespace IronHive.DeepResearch.Models.Search;

/// <summary>
/// 검색 배치 요청
/// </summary>
public record SearchBatch
{
    /// <summary>
    /// 배치 ID
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 실행할 검색 쿼리들
    /// </summary>
    public required IReadOnlyList<SearchQuery> Queries { get; init; }

    /// <summary>
    /// 배치 우선순위 (낮을수록 높은 우선순위)
    /// </summary>
    public int Priority { get; init; } = 1;

    /// <summary>
    /// 배치 생성 시간
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 검색 실행 결과
/// </summary>
public record SearchExecutionResult
{
    /// <summary>
    /// 성공한 검색 결과들
    /// </summary>
    public required IReadOnlyList<SearchResult> SuccessfulResults { get; init; }

    /// <summary>
    /// 실패한 쿼리들
    /// </summary>
    public required IReadOnlyList<FailedSearch> FailedSearches { get; init; }

    /// <summary>
    /// 총 실행된 쿼리 수
    /// </summary>
    public int TotalQueriesExecuted { get; init; }

    /// <summary>
    /// 수집된 고유 소스 수
    /// </summary>
    public int UniqueSourcesCollected { get; init; }

    /// <summary>
    /// 실행 시작 시간
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 실행 종료 시간
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// 총 실행 시간
    /// </summary>
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// 실패한 검색 정보
/// </summary>
public record FailedSearch
{
    /// <summary>
    /// 실패한 쿼리
    /// </summary>
    public required SearchQuery Query { get; init; }

    /// <summary>
    /// 에러 메시지
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// 에러 유형
    /// </summary>
    public SearchErrorType ErrorType { get; init; }

    /// <summary>
    /// 재시도 횟수
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// 재시도 가능 여부
    /// </summary>
    public bool IsRetryable { get; init; }
}

/// <summary>
/// 검색 에러 유형
/// </summary>
public enum SearchErrorType
{
    /// <summary>
    /// 알 수 없는 에러
    /// </summary>
    Unknown,

    /// <summary>
    /// 타임아웃
    /// </summary>
    Timeout,

    /// <summary>
    /// Rate limit 초과
    /// </summary>
    RateLimited,

    /// <summary>
    /// 인증 실패
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// 잘못된 요청
    /// </summary>
    BadRequest,

    /// <summary>
    /// 서버 에러
    /// </summary>
    ServerError,

    /// <summary>
    /// 네트워크 에러
    /// </summary>
    NetworkError,

    /// <summary>
    /// 취소됨
    /// </summary>
    Cancelled
}

/// <summary>
/// 검색 실행 옵션
/// </summary>
public record SearchExecutionOptions
{
    /// <summary>
    /// 최대 병렬 검색 수
    /// </summary>
    public int MaxParallelSearches { get; init; } = 5;

    /// <summary>
    /// 쿼리당 최대 재시도 횟수
    /// </summary>
    public int MaxRetriesPerQuery { get; init; } = 2;

    /// <summary>
    /// 재시도 간격
    /// </summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 지수 백오프 사용
    /// </summary>
    public bool UseExponentialBackoff { get; init; } = true;

    /// <summary>
    /// Rate limit 대기 최대 시간
    /// </summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 쿼리당 타임아웃
    /// </summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 중복 URL 제거
    /// </summary>
    public bool DeduplicateUrls { get; init; } = true;

    /// <summary>
    /// 실패 시 계속 진행
    /// </summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>
    /// 선호 프로바이더 ID (null이면 자동 선택)
    /// </summary>
    public string? PreferredProviderId { get; init; }
}

/// <summary>
/// 검색 배치 진행 상태
/// </summary>
public record SearchBatchProgress
{
    /// <summary>
    /// 총 쿼리 수
    /// </summary>
    public int TotalQueries { get; init; }

    /// <summary>
    /// 완료된 쿼리 수
    /// </summary>
    public int CompletedQueries { get; init; }

    /// <summary>
    /// 성공한 쿼리 수
    /// </summary>
    public int SuccessfulQueries { get; init; }

    /// <summary>
    /// 실패한 쿼리 수
    /// </summary>
    public int FailedQueries { get; init; }

    /// <summary>
    /// 현재 진행 중인 쿼리 수
    /// </summary>
    public int InProgressQueries { get; init; }

    /// <summary>
    /// 수집된 소스 수
    /// </summary>
    public int CollectedSources { get; init; }

    /// <summary>
    /// 진행률 (0-1)
    /// </summary>
    public double ProgressRatio => TotalQueries > 0 ? (double)CompletedQueries / TotalQueries : 0;
}
