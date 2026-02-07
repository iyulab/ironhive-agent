namespace IronHive.DeepResearch.Models.Content;

/// <summary>
/// 추출된 콘텐츠
/// </summary>
public record ExtractedContent
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? PublishedDate { get; init; }
    public required DateTimeOffset ExtractedAt { get; init; }
    public int ContentLength { get; init; }
    public bool Success { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string>? Links { get; init; }
    public IReadOnlyList<string>? Images { get; init; }
}

/// <summary>
/// 콘텐츠 청크
/// </summary>
public record ContentChunk
{
    public required string SourceId { get; init; }
    public required string SourceUrl { get; init; }
    public required string Content { get; init; }
    public required int ChunkIndex { get; init; }
    public int TotalChunks { get; init; }
    public int TokenCount { get; init; }
    public int StartPosition { get; init; }
    public int EndPosition { get; init; }
}

/// <summary>
/// 소스 문서
/// </summary>
public record SourceDocument
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? PublishedDate { get; init; }
    public required DateTimeOffset ExtractedAt { get; init; }
    public required string Provider { get; init; }
    public double RelevanceScore { get; init; }
    public double TrustScore { get; init; }
    public IReadOnlyList<ContentChunk>? Chunks { get; init; }
}

/// <summary>
/// 청킹 옵션
/// </summary>
public record ChunkingOptions
{
    /// <summary>
    /// 청크당 최대 토큰 수
    /// </summary>
    public int MaxTokensPerChunk { get; init; } = 500;

    /// <summary>
    /// 청크 간 오버랩 토큰 수
    /// </summary>
    public int OverlapTokens { get; init; } = 50;

    /// <summary>
    /// 문장 경계에서 분할 여부
    /// </summary>
    public bool SplitOnSentences { get; init; } = true;

    /// <summary>
    /// 문단 경계에서 분할 여부
    /// </summary>
    public bool SplitOnParagraphs { get; init; } = true;
}
