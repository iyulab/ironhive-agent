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
public partial class SearchCoordinatorAgent
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

        LogSearchExecutionStarting(_logger, queries.Count, options.MaxParallelSearches);

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
            LogNoNewQueriesToExecute(_logger);
            return CreateEmptyResult();
        }

        LogNewQueriesExecuting(_logger, newQueries.Count, plan.InitialQueries.Count - newQueries.Count);

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

        LogFollowUpSearchExecuting(_logger, followUpQueries.Count);

        var result = await ExecuteSearchesAsync(
            followUpQueries, options, progress, cancellationToken);

        // 상태 업데이트
        UpdateState(state, result);

        return result;
    }

    private async Task<SearchExecutionResult> ExecuteSearchQueriesAsync(
        List<SearchQuery> queries,
        SearchExecutionOptions options,
        IProgress<SearchBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var successfulResults = new List<SearchResult>();
        var failedSearches = new List<FailedSearch>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var progressGate = new object();

        // 병렬 실행 제어
        using var semaphore = new SemaphoreSlim(options.MaxParallelSearches);
        var completedCount = 0;
        var inProgressCount = 0;
        var progressReporter = new OrderedProgressReporter<SearchBatchProgress>(progress);

        // 카운터 증감과 그 시점의 상태 스냅샷·큐 적재를 하나의 락으로 직렬화해, 병렬로
        // 시작·완료되는 쿼리들의 진행률 콜백이 항상 호출된 순서대로 전달되도록 보장한다
        // (OrderedProgressReporter는 적재 순서 보존만 책임진다). successfulResults/
        // failedSearches/seenUrls 변경도 같은 락으로 묶어 진행률 스냅샷과 일관되게 유지한다.
        void ReportProgress(int inProgressDelta, int completedDelta)
        {
            lock (progressGate)
            {
                inProgressCount += inProgressDelta;
                completedCount += completedDelta;

                progressReporter.Report(new SearchBatchProgress
                {
                    TotalQueries = queries.Count,
                    CompletedQueries = completedCount,
                    SuccessfulQueries = successfulResults.Count,
                    FailedQueries = failedSearches.Count,
                    InProgressQueries = inProgressCount,
                    CollectedSources = successfulResults.Sum(r => r.Sources.Count)
                });
            }
        }

        // 우선순위별로 정렬하여 실행
        var sortedQueries = queries.ToList();

        var tasks = sortedQueries.Select(async query =>
        {
            await semaphore.WaitAsync(cancellationToken);
            ReportProgress(inProgressDelta: 1, completedDelta: 0);

            try
            {
                var result = await ExecuteSingleQueryWithRetryAsync(
                    query, options, cancellationToken);

                if (result.Success)
                {
                    lock (progressGate)
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
                    lock (progressGate)
                    {
                        failedSearches.Add(result.Failure!);
                    }
                }
            }
            finally
            {
                semaphore.Release();
                ReportProgress(inProgressDelta: -1, completedDelta: 1);
            }
        });

        await Task.WhenAll(tasks);
        await progressReporter.CompleteAsync();

        var completedAt = DateTimeOffset.UtcNow;

        var totalSources = successfulResults.Sum(r => r.Sources.Count);
        LogSearchExecutionCompleted(_logger, successfulResults.Count, failedSearches.Count,
            totalSources, (completedAt - startedAt).TotalMilliseconds);

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

                LogQuerySucceeded(_logger, query.Query, result.Sources.Count);

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
                LogQueryTimeout(_logger, query.Query, retryCount + 1, options.MaxRetriesPerQuery + 1);
            }
            catch (HttpRequestException ex) when (IsRateLimited(ex))
            {
                lastException = ex;
                LogRateLimitDetected(_logger, query.Query);

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
                LogServerError(_logger, query.Query, ex.Message);
            }
            catch (Exception ex)
            {
                lastException = ex;
                LogQueryFailed(_logger, ex, query.Query);

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

    private static void UpdateState(ResearchState state, SearchExecutionResult result)
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

    /// <summary>
    /// 단일 쿼리 실행 결과 (내부용)
    /// </summary>
    private record QueryExecutionResult
    {
        public bool Success { get; init; }
        public SearchResult? Result { get; init; }
        public FailedSearch? Failure { get; init; }
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "Search execution starting: {QueryCount} queries, max parallel {MaxParallel}")]
    private static partial void LogSearchExecutionStarting(ILogger logger, int queryCount, int maxParallel);

    [LoggerMessage(Level = LogLevel.Information, Message = "No new queries to execute")]
    private static partial void LogNoNewQueriesToExecute(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing {NewCount} new queries (skipping {ExistingCount} existing)")]
    private static partial void LogNewQueriesExecuting(ILogger logger, int newCount, int existingCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Follow-up search executing: {QueryCount} queries")]
    private static partial void LogFollowUpSearchExecuting(ILogger logger, int queryCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Search execution completed: success {SuccessCount}, failed {FailCount}, sources {SourceCount}, duration {Duration}ms")]
    private static partial void LogSearchExecutionCompleted(ILogger logger, int successCount, int failCount, int sourceCount, double duration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query succeeded: {Query}, sources {Count}")]
    private static partial void LogQuerySucceeded(ILogger logger, string query, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query timeout: {Query} (attempt {Retry}/{MaxRetry})")]
    private static partial void LogQueryTimeout(ILogger logger, string query, int retry, int maxRetry);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit detected: {Query}, retrying after wait")]
    private static partial void LogRateLimitDetected(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server error: {Query}, {Message}")]
    private static partial void LogServerError(ILogger logger, string query, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query failed: {Query}")]
    private static partial void LogQueryFailed(ILogger logger, Exception? exception, string query);

    #endregion
}
