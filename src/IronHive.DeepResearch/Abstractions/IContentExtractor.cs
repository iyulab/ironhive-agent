using IronHive.DeepResearch.Models.Content;

namespace IronHive.DeepResearch.Abstractions;

/// <summary>
/// 콘텐츠 추출기 인터페이스
/// </summary>
public interface IContentExtractor
{
    /// <summary>
    /// 단일 URL에서 콘텐츠 추출
    /// </summary>
    Task<ExtractedContent> ExtractAsync(
        string url,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 여러 URL에서 배치 콘텐츠 추출
    /// </summary>
    Task<IReadOnlyList<ExtractedContent>> ExtractBatchAsync(
        IEnumerable<string> urls,
        ContentExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 콘텐츠 추출 옵션
/// </summary>
public record ContentExtractionOptions
{
    /// <summary>
    /// 최대 콘텐츠 길이 (문자 수)
    /// </summary>
    public int MaxContentLength { get; init; } = 50000;

    /// <summary>
    /// 링크 추출 여부
    /// </summary>
    public bool ExtractLinks { get; init; } = false;

    /// <summary>
    /// 이미지 URL 추출 여부
    /// </summary>
    public bool ExtractImages { get; init; } = false;

    /// <summary>
    /// 메타데이터 추출 여부 (author, date 등)
    /// </summary>
    public bool ExtractMetadata { get; init; } = true;

    /// <summary>
    /// 요청 타임아웃
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
