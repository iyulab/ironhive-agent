using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.State;
using IronHive.DeepResearch.Search;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// 검색 실행 조율 에이전트
/// </summary>
public class SearchCoordinatorAgent
{
    private readonly SearchProviderFactory _providerFactory;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<SearchCoordinatorAgent> _logger;

    public SearchCoordinatorAgent(
        SearchProviderFactory providerFactory,
        DeepResearchOptions options,
        ILogger<SearchCoordinatorAgent> logger)
    {
        _providerFactory = providerFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 검색 쿼리 배치 실행
    /// </summary>
    public virtual async Task<SearchExecutionResult> ExecuteSearchesAsync(
        IReadOnlyList<ExpandedQuery> queries,
        SearchExecutionOptions? options = null,
        IProgress<SearchBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CreateDefaultOptions();
        var startedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("검색 실행 시작: {QueryCount}개 쿼리, 최대 병렬 {MaxParallel}개",
            queries.Count, options.MaxParallelSearches);

        var searchQueries = queries
            .Select(ConvertToSearchQuery)
            .ToList();

        return await ExecuteSearchQueriesAsync(
            searchQueries, options, progress, cancellationToken);
    }

    /// <summary>
    /// ResearchState에서 검색 실행
    /// </summary>
    public async Task<SearchExecutionResult> ExecuteFromStateAsync(
        ResearchState state,
        QueryPlanResult plan,
        SearchExecutionOptions? options = null,
        IProgress<SearchBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CreateDefaultOptions();

        // 이미 실행된 쿼리 제외
        var executedQueryTexts = state.ExecutedQueries
            .Select(q => NormalizeQuery(q.Query))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newQueries = plan.InitialQueries
            .Where(q => !executedQueryTexts.Contains(NormalizeQuery(q.Query)))
            .ToList();

        if (newQueries.Count == 0)
        {
            _logger.LogInformation("실행할 새로운 쿼리 없음");
            return CreateEmptyResult();
        }

        _logger.LogInformation("새로운 쿼리 {NewCount}개 실행 (기존 {ExistingCount}개 제외)",
            newQueries.Count, plan.InitialQueries.Count - newQueries.Count);

        var result = await ExecuteSearchesAsync(
            newQueries, options, progress, cancellationToken);

        // 상태 업데이트
        UpdateState(state, result);

        return result;
    }

    /// <summary>
    /// 후속 검색 실행 (정보 갭 기반)
    /// </summary>
    public async Task<SearchExecutionResult> ExecuteFollowUpSearchesAsync(
        ResearchState state,
        IReadOnlyList<ExpandedQuery> followUpQueries,
        SearchExecutionOptions? options = null,
        IProgress<SearchBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (followUpQueries.Count == 0)
        {
            return CreateEmptyResult();
        }

        options ??= CreateDefaultOptions();

        _logger.LogInformation("후속 검색 실행: {QueryCount}개 쿼리", followUpQueries.Count);

        var result = await ExecuteSearchesAsync(
            followUpQueries, options, progress, cancellationToken);

        // 상태 업데이트
        UpdateState(state, result);

        return result;
    }

    private async Task<SearchExecutionResult> ExecuteSearchQueriesAsync(
        IReadOnlyList<SearchQuery> queries,
        SearchExecutionOptions options,
        IProgress<SearchBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var successfulResults = new List<SearchResult>();
        var failedSearches = new List<FailedSearch>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 병렬 실행 제어
        using var semaphore = new SemaphoreSlim(options.MaxParallelSearches);
        var completedCount = 0;
        var inProgressCount = 0;

        void ReportProgress()
        {
            progress?.Report(new SearchBatchProgress
            {
                TotalQueries = queries.Count,
                CompletedQueries = completedCount,
                SuccessfulQueries = successfulResults.Count,
                FailedQueries = failedSearches.Count,
                InProgressQueries = inProgressCount,
                CollectedSources = successfulResults.Sum(r => r.Sources.Count)
            });
        }

        // 우선순위별로 정렬하여 실행
        var sortedQueries = queries.ToList();

        var tasks = sortedQueries.Select(async query =>
        {
            await semaphore.WaitAsync(cancellationToken);
            Interlocked.Increment(ref inProgressCount);
            ReportProgress();

            try
            {
                var result = await ExecuteSingleQueryWithRetryAsync(
                    query, options, cancellationToken);

                if (result.Success)
                {
                    lock (successfulResults)
                    {
                        // 중복 URL 제거
                        if (options.DeduplicateUrls)
                        {
                            var newSources = result.Result!.Sources
                                .Where(s => seenUrls.Add(s.Url))
                                .ToList();

                            if (newSources.Count != result.Result.Sources.Count)
                            {
                                result = new QueryExecutionResult
                                {
                                    Success = true,
                                    Result = result.Result with
                                    {
                                        Sources = newSources
                                    }
                                };
                            }
                        }

                        successfulResults.Add(result.Result!);
                    }
                }
                else
                {
                    lock (failedSearches)
                    {
                        failedSearches.Add(result.Failure!);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref inProgressCount);
                Interlocked.Increment(ref completedCount);
                semaphore.Release();
                ReportProgress();
            }
        });

        await Task.WhenAll(tasks);

        var completedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "검색 실행 완료: 성공 {SuccessCount}개, 실패 {FailCount}개, 소스 {SourceCount}개, 소요 시간 {Duration}ms",
            successfulResults.Count, failedSearches.Count,
            successfulResults.Sum(r => r.Sources.Count),
            (completedAt - startedAt).TotalMilliseconds);

        return new SearchExecutionResult
        {
            SuccessfulResults = successfulResults,
            FailedSearches = failedSearches,
            TotalQueriesExecuted = queries.Count,
            UniqueSourcesCollected = seenUrls.Count,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    private async Task<QueryExecutionResult> ExecuteSingleQueryWithRetryAsync(
        SearchQuery query,
        SearchExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        Exception? lastException = null;

        while (retryCount <= options.MaxRetriesPerQuery)
        {
            try
            {
                var provider = SelectProvider(query, options);

                using var timeoutCts = new CancellationTokenSource(options.QueryTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token);

                var result = await provider.SearchAsync(query, linkedCts.Token);

                _logger.LogDebug("쿼리 성공: {Query}, 소스 {Count}개",
                    TruncateQuery(query.Query), result.Sources.Count);

                return new QueryExecutionResult { Success = true, Result = result };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 외부 취소 - 재시도하지 않음
                return CreateFailedResult(query, "Operation cancelled", SearchErrorType.Cancelled, retryCount);
            }
            catch (OperationCanceledException)
            {
                // 타임아웃
                lastException = new TimeoutException($"Query timed out after {options.QueryTimeout.TotalSeconds}s");
                _logger.LogWarning("쿼리 타임아웃: {Query} (시도 {Retry}/{MaxRetry})",
                    TruncateQuery(query.Query), retryCount + 1, options.MaxRetriesPerQuery + 1);
            }
            catch (HttpRequestException ex) when (IsRateLimited(ex))
            {
                lastException = ex;
                _logger.LogWarning("Rate limit 감지: {Query}, 대기 후 재시도",
                    TruncateQuery(query.Query));

                // Rate limit 대기
                var waitTime = CalculateRateLimitWait(retryCount, options);
                if (waitTime > options.MaxRateLimitWait)
                {
                    return CreateFailedResult(query, "Rate limit exceeded", SearchErrorType.RateLimited, retryCount);
                }

                await Task.Delay(waitTime, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsServerError(ex))
            {
                lastException = ex;
                _logger.LogWarning("서버 에러: {Query}, {Message}",
                    TruncateQuery(query.Query), ex.Message);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "쿼리 실패: {Query}", TruncateQuery(query.Query));

                // 재시도 불가능한 에러
                if (!IsRetryableError(ex))
                {
                    return CreateFailedResult(query, ex.Message, ClassifyError(ex), retryCount, false);
                }
            }

            retryCount++;

            if (retryCount <= options.MaxRetriesPerQuery)
            {
                var delay = CalculateRetryDelay(retryCount, options);
                await Task.Delay(delay, cancellationToken);
            }
        }

        return CreateFailedResult(
            query,
            lastException?.Message ?? "Unknown error",
            ClassifyError(lastException),
            retryCount);
    }

    private ISearchProvider SelectProvider(SearchQuery query, SearchExecutionOptions options)
    {
        if (!string.IsNullOrEmpty(options.PreferredProviderId) &&
            _providerFactory.HasProvider(options.PreferredProviderId))
        {
            return _providerFactory.GetProvider(options.PreferredProviderId);
        }

        return _providerFactory.SelectProviderForSearchType(query.Type);
    }

    private SearchQuery ConvertToSearchQuery(ExpandedQuery expanded)
    {
        return new SearchQuery
        {
            Query = expanded.Query,
            Type = expanded.SearchType switch
            {
                QuerySearchType.News => SearchType.News,
                QuerySearchType.Academic => SearchType.Academic,
                _ => SearchType.Web
            },
            Depth = expanded.Priority <= 1 ? QueryDepth.Deep : QueryDepth.Basic,
            MaxResults = 10,
            IncludeContent = true
        };
    }

    private void UpdateState(ResearchState state, SearchExecutionResult result)
    {
        // 실행된 쿼리 추가
        foreach (var searchResult in result.SuccessfulResults)
        {
            state.ExecutedQueries.Add(searchResult.Query);
            state.SearchResults.Add(searchResult);
        }

        // 에러 기록
        foreach (var failed in result.FailedSearches)
        {
            state.Errors.Add(new Models.Research.ResearchError
            {
                Type = Models.Research.ResearchErrorType.SearchProviderError,
                Message = $"검색 실패: {failed.ErrorMessage}",
                OccurredAt = DateTimeOffset.UtcNow,
                Details = $"Query: {failed.Query.Query}, Type: {failed.ErrorType}"
            });
        }
    }

    private SearchExecutionOptions CreateDefaultOptions()
    {
        return new SearchExecutionOptions
        {
            MaxParallelSearches = _options.MaxParallelSearches,
            MaxRetriesPerQuery = _options.MaxRetries,
            QueryTimeout = _options.HttpTimeout
        };
    }

    private static SearchExecutionResult CreateEmptyResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new SearchExecutionResult
        {
            SuccessfulResults = [],
            FailedSearches = [],
            TotalQueriesExecuted = 0,
            UniqueSourcesCollected = 0,
            StartedAt = now,
            CompletedAt = now
        };
    }

    private static QueryExecutionResult CreateFailedResult(
        SearchQuery query,
        string message,
        SearchErrorType errorType,
        int retryCount,
        bool isRetryable = true)
    {
        return new QueryExecutionResult
        {
            Success = false,
            Failure = new FailedSearch
            {
                Query = query,
                ErrorMessage = message,
                ErrorType = errorType,
                RetryCount = retryCount,
                IsRetryable = isRetryable
            }
        };
    }

    private static bool IsRateLimited(HttpRequestException ex)
    {
        return ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
    }

    private static bool IsServerError(HttpRequestException ex)
    {
        return ex.StatusCode >= System.Net.HttpStatusCode.InternalServerError;
    }

    private static bool IsRetryableError(Exception? ex)
    {
        return ex switch
        {
            HttpRequestException httpEx => httpEx.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => true,
                System.Net.HttpStatusCode.ServiceUnavailable => true,
                System.Net.HttpStatusCode.GatewayTimeout => true,
                System.Net.HttpStatusCode.BadGateway => true,
                >= System.Net.HttpStatusCode.InternalServerError => true,
                _ => false
            },
            TimeoutException => true,
            TaskCanceledException => true,
            _ => false
        };
    }

    private static SearchErrorType ClassifyError(Exception? ex)
    {
        return ex switch
        {
            TimeoutException => SearchErrorType.Timeout,
            TaskCanceledException => SearchErrorType.Timeout,
            HttpRequestException httpEx => httpEx.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => SearchErrorType.RateLimited,
                System.Net.HttpStatusCode.Unauthorized => SearchErrorType.AuthenticationFailed,
                System.Net.HttpStatusCode.Forbidden => SearchErrorType.AuthenticationFailed,
                System.Net.HttpStatusCode.BadRequest => SearchErrorType.BadRequest,
                >= System.Net.HttpStatusCode.InternalServerError => SearchErrorType.ServerError,
                _ => SearchErrorType.NetworkError
            },
            _ => SearchErrorType.Unknown
        };
    }

    private static TimeSpan CalculateRetryDelay(int retryCount, SearchExecutionOptions options)
    {
        if (!options.UseExponentialBackoff)
        {
            return options.RetryDelay;
        }

        // 지수 백오프: 1s, 2s, 4s, 8s...
        var multiplier = Math.Pow(2, retryCount - 1);
        return TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * multiplier);
    }

    private static TimeSpan CalculateRateLimitWait(int retryCount, SearchExecutionOptions options)
    {
        // Rate limit은 더 긴 대기 시간
        var baseWait = TimeSpan.FromSeconds(5);
        var multiplier = Math.Pow(2, retryCount);
        return TimeSpan.FromMilliseconds(baseWait.TotalMilliseconds * multiplier);
    }

    private static string NormalizeQuery(string query)
    {
        return string.Join(' ', query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TruncateQuery(string query, int maxLength = 50)
    {
        return query.Length <= maxLength ? query : query[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// 단일 쿼리 실행 결과 (내부용)
    /// </summary>
    private record QueryExecutionResult
    {
        public bool Success { get; init; }
        public SearchResult? Result { get; init; }
        public FailedSearch? Failure { get; init; }
    }
}
