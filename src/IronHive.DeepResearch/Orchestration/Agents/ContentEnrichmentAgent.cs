using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// 콘텐츠 강화 에이전트
/// 검색 결과에서 콘텐츠를 추출하고 청킹하여 SourceDocument로 변환
/// </summary>
public class ContentEnrichmentAgent
{
    private readonly IContentExtractor _contentExtractor;
    private readonly ContentChunker _contentChunker;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<ContentEnrichmentAgent> _logger;

    public ContentEnrichmentAgent(
        IContentExtractor contentExtractor,
        ContentChunker contentChunker,
        DeepResearchOptions options,
        ILogger<ContentEnrichmentAgent> logger)
    {
        _contentExtractor = contentExtractor;
        _contentChunker = contentChunker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 검색 결과를 강화하여 SourceDocument 생성
    /// </summary>
    public virtual async Task<ContentEnrichmentResult> EnrichSearchResultsAsync(
        IReadOnlyList<SearchResult> searchResults,
        ContentEnrichmentOptions? options = null,
        IProgress<ContentEnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CreateDefaultOptions();
        var startedAt = DateTimeOffset.UtcNow;

        // 검색 결과에서 소스 정보 추출
        var sourcesToProcess = ExtractSourcesToProcess(searchResults, options);

        _logger.LogInformation("콘텐츠 강화 시작: {SourceCount}개 소스, 최대 병렬 {MaxParallel}개",
            sourcesToProcess.Count, options.MaxParallelExtractions);

        var documents = new List<SourceDocument>();
        var failedExtractions = new List<FailedExtraction>();
        var totalChunks = 0;

        using var semaphore = new SemaphoreSlim(options.MaxParallelExtractions);
        var completedCount = 0;

        void ReportProgress()
        {
            progress?.Report(new ContentEnrichmentProgress
            {
                TotalUrls = sourcesToProcess.Count,
                CompletedUrls = completedCount,
                SuccessfulUrls = documents.Count,
                FailedUrls = failedExtractions.Count,
                ChunksCreated = totalChunks
            });
        }

        var tasks = sourcesToProcess.Select(async source =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await ProcessSourceAsync(source, options, cancellationToken);

                lock (documents)
                {
                    if (result.Document != null)
                    {
                        documents.Add(result.Document);
                        totalChunks += result.Document.Chunks?.Count ?? 0;
                    }
                    else if (result.Failure != null)
                    {
                        failedExtractions.Add(result.Failure);
                    }
                }
            }
            finally
            {
                Interlocked.Increment(ref completedCount);
                semaphore.Release();
                ReportProgress();
            }
        });

        await Task.WhenAll(tasks);

        var completedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "콘텐츠 강화 완료: 문서 {DocCount}개, 청크 {ChunkCount}개, 실패 {FailCount}개, 소요 시간 {Duration}ms",
            documents.Count, totalChunks, failedExtractions.Count,
            (completedAt - startedAt).TotalMilliseconds);

        return new ContentEnrichmentResult
        {
            Documents = documents,
            FailedExtractions = failedExtractions,
            TotalUrlsProcessed = sourcesToProcess.Count,
            TotalChunksCreated = totalChunks,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// ResearchState에서 콘텐츠 강화 실행
    /// </summary>
    public async Task<ContentEnrichmentResult> EnrichFromStateAsync(
        ResearchState state,
        SearchExecutionResult searchExecution,
        ContentEnrichmentOptions? options = null,
        IProgress<ContentEnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await EnrichSearchResultsAsync(
            searchExecution.SuccessfulResults,
            options,
            progress,
            cancellationToken);

        // 상태 업데이트
        UpdateState(state, result);

        return result;
    }

    private List<SourceInfo> ExtractSourcesToProcess(
        IReadOnlyList<SearchResult> searchResults,
        ContentEnrichmentOptions options)
    {
        var sources = new List<SourceInfo>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in searchResults)
        {
            foreach (var source in result.Sources)
            {
                // 중복 URL 제외
                if (!seenUrls.Add(source.Url))
                    continue;

                sources.Add(new SourceInfo
                {
                    Url = source.Url,
                    Title = source.Title,
                    Snippet = source.Snippet,
                    RawContent = source.RawContent,
                    Score = source.Score,
                    PublishedDate = source.PublishedDate,
                    Provider = result.Provider
                });
            }
        }

        return sources;
    }

    private async Task<ProcessingResult> ProcessSourceAsync(
        SourceInfo source,
        ContentEnrichmentOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            ExtractedContent extracted;

            // RawContent가 있고 사용 옵션이 켜져 있으면 직접 사용
            if (options.UseRawContentWhenAvailable && !string.IsNullOrWhiteSpace(source.RawContent))
            {
                _logger.LogDebug("RawContent 사용: {Url}", TruncateUrl(source.Url));
                extracted = CreateExtractedContentFromRaw(source);
            }
            else
            {
                // 콘텐츠 추출 실행
                _logger.LogDebug("콘텐츠 추출 시작: {Url}", TruncateUrl(source.Url));

                using var timeoutCts = new CancellationTokenSource(options.ExtractionTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token);

                var extractionOptions = new ContentExtractionOptions
                {
                    MaxContentLength = options.MaxContentLength,
                    ExtractMetadata = options.ExtractMetadata,
                    ExtractLinks = options.ExtractLinks,
                    Timeout = options.ExtractionTimeout
                };

                extracted = await _contentExtractor.ExtractAsync(
                    source.Url, extractionOptions, linkedCts.Token);
            }

            if (!extracted.Success)
            {
                return new ProcessingResult
                {
                    Failure = new FailedExtraction
                    {
                        Url = source.Url,
                        ErrorMessage = extracted.ErrorMessage ?? "Extraction failed",
                        ErrorType = ExtractionErrorType.ParseError
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(extracted.Content))
            {
                return new ProcessingResult
                {
                    Failure = new FailedExtraction
                    {
                        Url = source.Url,
                        ErrorMessage = "No content extracted",
                        ErrorType = ExtractionErrorType.NoContent
                    }
                };
            }

            // 청킹
            IReadOnlyList<ContentChunk>? chunks = null;
            if (options.EnableChunking)
            {
                chunks = _contentChunker.ChunkContent(extracted, options.ChunkingOptions);
            }

            // SourceDocument 생성
            var document = CreateSourceDocument(source, extracted, chunks);

            return new ProcessingResult { Document = document };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 외부 취소는 전파
        }
        catch (OperationCanceledException)
        {
            return new ProcessingResult
            {
                Failure = new FailedExtraction
                {
                    Url = source.Url,
                    ErrorMessage = "Extraction timed out",
                    ErrorType = ExtractionErrorType.Timeout
                }
            };
        }
        catch (HttpRequestException ex)
        {
            var errorType = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => ExtractionErrorType.AccessDenied,
                System.Net.HttpStatusCode.Unauthorized => ExtractionErrorType.AccessDenied,
                System.Net.HttpStatusCode.NotFound => ExtractionErrorType.NoContent,
                _ => ExtractionErrorType.NetworkError
            };

            return new ProcessingResult
            {
                Failure = new FailedExtraction
                {
                    Url = source.Url,
                    ErrorMessage = ex.Message,
                    ErrorType = errorType
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "콘텐츠 추출 실패: {Url}", source.Url);

            return new ProcessingResult
            {
                Failure = new FailedExtraction
                {
                    Url = source.Url,
                    ErrorMessage = ex.Message,
                    ErrorType = ExtractionErrorType.Unknown
                }
            };
        }
    }

    private static ExtractedContent CreateExtractedContentFromRaw(SourceInfo source)
    {
        return new ExtractedContent
        {
            Url = source.Url,
            Title = source.Title,
            Content = source.RawContent,
            PublishedDate = source.PublishedDate,
            ContentLength = source.RawContent?.Length ?? 0,
            ExtractedAt = DateTimeOffset.UtcNow,
            Success = true
        };
    }

    private static SourceDocument CreateSourceDocument(
        SourceInfo source,
        ExtractedContent extracted,
        IReadOnlyList<ContentChunk>? chunks)
    {
        var id = GenerateDocumentId(source.Url);

        return new SourceDocument
        {
            Id = id,
            Url = source.Url,
            Title = extracted.Title ?? source.Title ?? "Untitled",
            Content = extracted.Content ?? "",
            Description = extracted.Description ?? source.Snippet,
            Author = extracted.Author,
            PublishedDate = extracted.PublishedDate ?? source.PublishedDate,
            ExtractedAt = extracted.ExtractedAt,
            Provider = source.Provider,
            RelevanceScore = source.Score,
            TrustScore = CalculateTrustScore(source, extracted),
            Chunks = chunks
        };
    }

    private static double CalculateTrustScore(SourceInfo source, ExtractedContent extracted)
    {
        var score = 0.5; // 기본 점수

        // 저자 정보가 있으면 신뢰도 증가
        if (!string.IsNullOrEmpty(extracted.Author))
            score += 0.1;

        // 발행일이 있으면 신뢰도 증가
        if (extracted.PublishedDate.HasValue)
            score += 0.1;

        // 콘텐츠 길이에 따른 점수 (너무 짧거나 길면 감점)
        var contentLength = extracted.ContentLength;
        if (contentLength >= 500 && contentLength <= 20000)
            score += 0.1;
        else if (contentLength < 200)
            score -= 0.1;

        // 검색 점수 반영
        score += source.Score * 0.2;

        return Math.Clamp(score, 0, 1);
    }

    private void UpdateState(ResearchState state, ContentEnrichmentResult result)
    {
        // 소스 문서 추가
        foreach (var document in result.Documents)
        {
            state.CollectedSources.Add(document);
        }

        // 에러 기록
        foreach (var failed in result.FailedExtractions)
        {
            state.Errors.Add(new Models.Research.ResearchError
            {
                Type = Models.Research.ResearchErrorType.ContentExtractionError,
                Message = $"콘텐츠 추출 실패: {failed.ErrorMessage}",
                OccurredAt = DateTimeOffset.UtcNow,
                Details = $"URL: {failed.Url}, Type: {failed.ErrorType}"
            });
        }
    }

    private ContentEnrichmentOptions CreateDefaultOptions()
    {
        return new ContentEnrichmentOptions
        {
            MaxParallelExtractions = _options.MaxParallelExtractions,
            ExtractionTimeout = _options.HttpTimeout,
            EnableChunking = true,
            ChunkingOptions = new ChunkingOptions
            {
                MaxTokensPerChunk = 500,
                OverlapTokens = 50
            }
        };
    }

    private static string GenerateDocumentId(string url)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        return $"doc_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static string TruncateUrl(string url, int maxLength = 60)
    {
        return url.Length <= maxLength ? url : url[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// 소스 정보 (내부용)
    /// </summary>
    private record SourceInfo
    {
        public required string Url { get; init; }
        public string? Title { get; init; }
        public string? Snippet { get; init; }
        public string? RawContent { get; init; }
        public double Score { get; init; }
        public DateTimeOffset? PublishedDate { get; init; }
        public required string Provider { get; init; }
    }

    /// <summary>
    /// 처리 결과 (내부용)
    /// </summary>
    private record ProcessingResult
    {
        public SourceDocument? Document { get; init; }
        public FailedExtraction? Failure { get; init; }
    }
}
