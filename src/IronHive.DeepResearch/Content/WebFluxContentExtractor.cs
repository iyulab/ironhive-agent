using System.Net;
using System.Text.RegularExpressions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Options;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Content;

/// <summary>
/// WebFlux 기반 콘텐츠 추출기
/// </summary>
public partial class WebFluxContentExtractor : IContentExtractor, IDisposable
{
    private bool _disposed;
    private readonly HttpClient _httpClient;
    private readonly ContentProcessor _processor;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<WebFluxContentExtractor> _logger;
    private readonly SemaphoreSlim _semaphore;

    public WebFluxContentExtractor(
        HttpClient httpClient,
        ContentProcessor processor,
        DeepResearchOptions options,
        ILogger<WebFluxContentExtractor> logger)
    {
        _httpClient = httpClient;
        _processor = processor;
        _options = options;
        _logger = logger;
        _semaphore = new SemaphoreSlim(options.MaxParallelExtractions);

        // User-Agent 설정
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; IronHive-Flux-DeepResearch/1.0)");
        }
    }

    public async Task<ExtractedContent> ExtractAsync(
        string url,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ContentExtractionOptions();

        _logger.LogInformation("콘텐츠 추출 시작: {Url}", url);

        try
        {
            // URL 유효성 검증
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return CreateFailedResult(url, "유효하지 않은 URL 형식입니다.");
            }

            // HTTP 요청
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.Timeout);

            var response = await _httpClient.GetAsync(uri, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return CreateFailedResult(url, $"HTTP 요청 실패: {response.StatusCode}");
            }

            // Content-Type 확인
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("text/html") && !contentType.Contains("text/plain"))
            {
                return CreateFailedResult(url, $"지원하지 않는 콘텐츠 타입: {contentType}");
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);

            // 콘텐츠 처리
            var processed = _processor.Process(html, uri, options);

            _logger.LogInformation("콘텐츠 추출 완료: {Url}, Length: {Length}",
                url, processed.Content?.Length ?? 0);

            return processed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("콘텐츠 추출 타임아웃: {Url}", url);
            return CreateFailedResult(url, "요청 타임아웃");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP 요청 오류: {Url}", url);
            return CreateFailedResult(url, $"HTTP 요청 오류: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "콘텐츠 추출 실패: {Url}", url);
            return CreateFailedResult(url, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ExtractedContent>> ExtractBatchAsync(
        IEnumerable<string> urls,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var urlList = urls.ToList();
        _logger.LogInformation("배치 콘텐츠 추출 시작: {Count}개 URL", urlList.Count);

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
        _logger.LogInformation("배치 콘텐츠 추출 완료: {Success}/{Total} 성공",
            successCount, results.Length);

        return results;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            // HttpClient is injected via DI — not owned by this class
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
}
