using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebFlux.Core.Interfaces;
using WebFlux.Core.Options;
using DeepResearchContentExtractor = IronHive.DeepResearch.Abstractions.IContentExtractor;
using DeepResearchExtractionOptions = IronHive.DeepResearch.Abstractions.ContentExtractionOptions;
using WebFluxExtractedContent = WebFlux.Core.Models.ExtractedContent;

namespace IronHive.DeepResearch.Content;

/// <summary>
/// WebFlux 패키지 기반 콘텐츠 추출기
/// WebFlux의 ICrawler와 IContentExtractor를 사용하여 고품질 콘텐츠 추출 제공
/// </summary>
public partial class WebFluxIntegratedContentExtractor : DeepResearchContentExtractor, IDisposable
{
    private bool _disposed;
    private readonly IServiceProvider _serviceProvider;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<WebFluxIntegratedContentExtractor> _logger;
    private readonly SemaphoreSlim _semaphore;

    public WebFluxIntegratedContentExtractor(
        IServiceProvider serviceProvider,
        DeepResearchOptions options,
        ILogger<WebFluxIntegratedContentExtractor> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
        _semaphore = new SemaphoreSlim(options.MaxParallelExtractions);
    }

    public async Task<ExtractedContent> ExtractAsync(
        string url,
        DeepResearchExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DeepResearchExtractionOptions();

        LogWebFluxExtractionStarting(_logger, url);

        try
        {
            // URL 유효성 검증
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return CreateFailedResult(url, "유효하지 않은 URL 형식입니다.");
            }

            // 1. WebFlux Crawler로 HTML 가져오기 (Intelligent 크롤러 사용)
            var crawler = GetCrawler();
            var crawlOptions = new CrawlOptions
            {
                Timeout = options.Timeout,
                MaxRetries = 3,
                EnableMetadataExtraction = options.ExtractMetadata
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.Timeout);

            var crawlResult = await crawler.CrawlAsync(url, crawlOptions, cts.Token);

            if (!crawlResult.IsSuccess || string.IsNullOrEmpty(crawlResult.HtmlContent))
            {
                return CreateFailedResult(url, crawlResult.ErrorMessage ?? $"크롤링 실패: HTTP {crawlResult.StatusCode}");
            }

            // 2. WebFlux ContentExtractor로 콘텐츠 추출
            var contentExtractor = GetContentExtractor();
            var webFluxExtracted = await contentExtractor.ExtractFromHtmlAsync(
                crawlResult.HtmlContent,
                crawlResult.FinalUrl ?? url,
                enableMetadataExtraction: options.ExtractMetadata,
                cancellationToken: cts.Token);

            // 3. WebFlux ExtractedContent → DeepResearch ExtractedContent 변환
            var result = MapToDeepResearchContent(webFluxExtracted, url, options);

            LogWebFluxExtractionCompleted(_logger, url, result.Content?.Length ?? 0);

            return result;
        }
        catch (OperationCanceledException)
        {
            LogExtractionTimeout(_logger, url);
            return CreateFailedResult(url, "요청 타임아웃");
        }
        catch (Exception ex)
        {
            LogWebFluxExtractionFailed(_logger, ex, url);
            return CreateFailedResult(url, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ExtractedContent>> ExtractBatchAsync(
        IEnumerable<string> urls,
        DeepResearchExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var urlList = urls.ToList();
        LogWebFluxBatchExtractionStarting(_logger, urlList.Count);

        var tasks = urlList.Select(async url =>
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                return await ExtractAsync(url, options, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        LogWebFluxBatchExtractionCompleted(_logger, successCount, results.Length);

        return results;
    }

    /// <summary>
    /// WebFlux Crawler 가져오기 (Intelligent 우선, 없으면 BreadthFirst)
    /// </summary>
    private ICrawler GetCrawler()
    {
        // 키드 서비스로 등록된 크롤러 시도 (우선순위: Intelligent > BreadthFirst > Default)
        var crawler = _serviceProvider.GetKeyedService<ICrawler>("Intelligent")
            ?? _serviceProvider.GetKeyedService<ICrawler>("BreadthFirst")
            ?? _serviceProvider.GetService<ICrawler>();

        if (crawler == null)
        {
            throw new InvalidOperationException(
                "WebFlux ICrawler가 등록되지 않았습니다. services.AddWebFlux()를 호출하세요.");
        }

        return crawler;
    }

    /// <summary>
    /// WebFlux ContentExtractor 가져오기
    /// </summary>
    private WebFlux.Core.Interfaces.IContentExtractor GetContentExtractor()
    {
        // 키드 서비스로 등록된 콘텐츠 추출기 시도
        var extractor = _serviceProvider.GetKeyedService<WebFlux.Core.Interfaces.IContentExtractor>("Html")
            ?? _serviceProvider.GetKeyedService<WebFlux.Core.Interfaces.IContentExtractor>("Default")
            ?? _serviceProvider.GetService<WebFlux.Core.Interfaces.IContentExtractor>();

        if (extractor == null)
        {
            throw new InvalidOperationException(
                "WebFlux IContentExtractor가 등록되지 않았습니다. services.AddWebFlux()를 호출하세요.");
        }

        return extractor;
    }

    /// <summary>
    /// WebFlux ExtractedContent를 DeepResearch ExtractedContent로 변환
    /// </summary>
    private static ExtractedContent MapToDeepResearchContent(
        WebFluxExtractedContent webFluxContent,
        string originalUrl,
        DeepResearchExtractionOptions options)
    {
        // 본문 콘텐츠 (MainContent 우선, 없으면 Text 사용)
        var content = !string.IsNullOrEmpty(webFluxContent.MainContent)
            ? webFluxContent.MainContent
            : webFluxContent.Text;

        // 최대 길이 제한
        if (content.Length > options.MaxContentLength)
        {
            content = TruncateAtSentenceBoundary(content, options.MaxContentLength);
        }

        // 메타데이터에서 저자/날짜 추출
        string? author = null;
        DateTimeOffset? publishedDate = null;
        string? description = null;

        if (webFluxContent.Metadata != null)
        {
            author = webFluxContent.Metadata.Author;
            publishedDate = webFluxContent.Metadata.PublishedDate;
            description = webFluxContent.Metadata.Description;
        }

        return new ExtractedContent
        {
            Url = webFluxContent.Url ?? originalUrl,
            Title = webFluxContent.Title,
            Description = description,
            Content = content,
            Author = author,
            PublishedDate = publishedDate,
            ContentLength = content?.Length ?? 0,
            ExtractedAt = webFluxContent.ExtractionTimestamp,
            Success = true,
            // WebFlux의 Headings를 Links로 대체 (추후 실제 링크 추출 구현 필요)
            Links = null,
            Images = options.ExtractImages ? webFluxContent.ImageUrls?.ToList() : null
        };
    }

    /// <summary>
    /// 문장 경계에서 텍스트 자르기
    /// </summary>
    private static string TruncateAtSentenceBoundary(string content, int maxLength)
    {
        if (content.Length <= maxLength)
        {
            return content;
        }

        var truncated = content[..maxLength];
        var lastSentenceEnd = truncated.LastIndexOfAny(['.', '!', '?', '\n']);

        if (lastSentenceEnd > maxLength * 0.7)
        {
            return truncated[..(lastSentenceEnd + 1)];
        }

        return truncated;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private static ExtractedContent CreateFailedResult(string url, string error)
    {
        return new ExtractedContent
        {
            Url = url,
            Success = false,
            ErrorMessage = error,
            ExtractedAt = DateTimeOffset.UtcNow
        };
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "WebFlux 콘텐츠 추출 시작: {Url}")]
    private static partial void LogWebFluxExtractionStarting(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebFlux 콘텐츠 추출 완료: {Url}, Length: {Length}")]
    private static partial void LogWebFluxExtractionCompleted(ILogger logger, string url, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "콘텐츠 추출 타임아웃: {Url}")]
    private static partial void LogExtractionTimeout(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "WebFlux 콘텐츠 추출 실패: {Url}")]
    private static partial void LogWebFluxExtractionFailed(ILogger logger, Exception? exception, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebFlux 배치 콘텐츠 추출 시작: {Count}개 URL")]
    private static partial void LogWebFluxBatchExtractionStarting(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebFlux 배치 콘텐츠 추출 완료: {Success}/{Total} 성공")]
    private static partial void LogWebFluxBatchExtractionCompleted(ILogger logger, int success, int total);

    #endregion
}
