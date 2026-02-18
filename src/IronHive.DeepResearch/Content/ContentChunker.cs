using System.Text.RegularExpressions;
using IronHive.DeepResearch.Models.Content;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Content;

/// <summary>
/// 콘텐츠 청킹 처리기
/// </summary>
public partial class ContentChunker
{
    private readonly ILogger<ContentChunker> _logger;

    // 간단한 토큰 추정: 평균 4자 = 1토큰 (영어 기준)
    // 한국어의 경우 2-3자 = 1토큰
    private const double CharsPerToken = 4.0;

    public ContentChunker(ILogger<ContentChunker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// ExtractedContent를 청크로 분할
    /// </summary>
    public IReadOnlyList<ContentChunk> ChunkContent(
        ExtractedContent content,
        ChunkingOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(content.Content))
        {
            return [];
        }

        options ??= new ChunkingOptions();
        var sourceId = GenerateSourceId(content.Url);

        return ChunkText(content.Content, sourceId, content.Url, options);
    }

    /// <summary>
    /// 여러 ExtractedContent를 청크로 분할
    /// </summary>
    public IReadOnlyList<ContentChunk> ChunkContents(
        IEnumerable<ExtractedContent> contents,
        ChunkingOptions? options = null)
    {
        var allChunks = new List<ContentChunk>();

        foreach (var content in contents.Where(c => c.Success && !string.IsNullOrWhiteSpace(c.Content)))
        {
            var chunks = ChunkContent(content, options);
            allChunks.AddRange(chunks);
        }

        return allChunks;
    }

    /// <summary>
    /// 텍스트를 청크로 분할
    /// </summary>
    public IReadOnlyList<ContentChunk> ChunkText(
        string text,
        string sourceId,
        string sourceUrl,
        ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();

        var maxCharsPerChunk = (int)(options.MaxTokensPerChunk * CharsPerToken);
        var overlapChars = (int)(options.OverlapTokens * CharsPerToken);

        var chunks = new List<ContentChunk>();

        // 문단 또는 문장 단위로 먼저 분할
        var segments = options.SplitOnParagraphs
            ? SplitIntoParagraphs(text)
            : options.SplitOnSentences
                ? SplitIntoSentences(text)
                : [text];

        var currentChunk = new List<string>();
        var currentLength = 0;
        var chunkIndex = 0;
        var startPosition = 0;

        foreach (var segment in segments)
        {
            var segmentLength = segment.Length;

            // 세그먼트가 최대 청크 크기보다 큰 경우 강제 분할
            if (segmentLength > maxCharsPerChunk)
            {
                // 현재 청크 저장
                if (currentChunk.Count > 0)
                {
                    var chunkText = string.Join(" ", currentChunk);
                    chunks.Add(CreateChunk(chunkText, sourceId, sourceUrl, chunkIndex++, startPosition));
                    startPosition += chunkText.Length;
                    currentChunk.Clear();
                    currentLength = 0;
                }

                // 긴 세그먼트를 강제 분할
                var forceSplitChunks = ForceSplitText(segment, maxCharsPerChunk, overlapChars);
                foreach (var forcedChunk in forceSplitChunks)
                {
                    chunks.Add(CreateChunk(forcedChunk, sourceId, sourceUrl, chunkIndex++, startPosition));
                    startPosition += forcedChunk.Length;
                }
                continue;
            }

            // 현재 청크에 추가하면 최대 크기를 초과하는 경우
            if (currentLength + segmentLength > maxCharsPerChunk && currentChunk.Count > 0)
            {
                var chunkText = string.Join(" ", currentChunk);
                chunks.Add(CreateChunk(chunkText, sourceId, sourceUrl, chunkIndex++, startPosition));
                startPosition += chunkText.Length;

                // 오버랩 처리
                if (overlapChars > 0 && currentChunk.Count > 0)
                {
                    var overlapText = GetOverlapText(currentChunk, overlapChars);
                    currentChunk.Clear();
                    if (!string.IsNullOrEmpty(overlapText))
                    {
                        currentChunk.Add(overlapText);
                        currentLength = overlapText.Length;
                    }
                    else
                    {
                        currentLength = 0;
                    }
                }
                else
                {
                    currentChunk.Clear();
                    currentLength = 0;
                }
            }

            currentChunk.Add(segment);
            currentLength += segmentLength + 1; // +1 for space
        }

        // 마지막 청크 저장
        if (currentChunk.Count > 0)
        {
            var chunkText = string.Join(" ", currentChunk);
            chunks.Add(CreateChunk(chunkText, sourceId, sourceUrl, chunkIndex++, startPosition));
        }

        // 전체 청크 수 업데이트
        var totalChunks = chunks.Count;
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i] = chunks[i] with { TotalChunks = totalChunks };
        }

        LogTextChunkingCompleted(_logger, chunks.Count, sourceId);

        return chunks;
    }

    private static ContentChunk CreateChunk(
        string content,
        string sourceId,
        string sourceUrl,
        int chunkIndex,
        int startPosition)
    {
        return new ContentChunk
        {
            SourceId = sourceId,
            SourceUrl = sourceUrl,
            Content = content.Trim(),
            ChunkIndex = chunkIndex,
            TokenCount = EstimateTokenCount(content),
            StartPosition = startPosition,
            EndPosition = startPosition + content.Length
        };
    }

    private static List<string> SplitIntoParagraphs(string text)
    {
        return ParagraphSplitRegex()
            .Split(text)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
    }

    private static List<string> SplitIntoSentences(string text)
    {
        return SentenceSplitRegex()
            .Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
    }

    private static List<string> ForceSplitText(string text, int maxLength, int overlapLength)
    {
        var chunks = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - position);
            var chunk = text.Substring(position, length);

            // 단어 경계에서 자르기 시도
            if (position + length < text.Length)
            {
                var lastSpace = chunk.LastIndexOf(' ');
                if (lastSpace > maxLength * 0.7)
                {
                    chunk = chunk[..lastSpace];
                    length = lastSpace;
                }
            }

            chunks.Add(chunk.Trim());
            position += length - overlapLength;

            // 무한 루프 방지
            if (length <= overlapLength)
            {
                position += length;
            }
        }

        return chunks;
    }

    private static string GetOverlapText(List<string> segments, int targetChars)
    {
        var result = new List<string>();
        var currentLength = 0;

        for (var i = segments.Count - 1; i >= 0 && currentLength < targetChars; i--)
        {
            result.Insert(0, segments[i]);
            currentLength += segments[i].Length + 1;
        }

        return string.Join(" ", result);
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        // 간단한 추정: 영어 4자 = 1토큰, 한국어 2자 = 1토큰
        // 혼합 텍스트의 경우 평균값 사용
        var koreanChars = text.Count(c => c >= 0xAC00 && c <= 0xD7A3);
        var totalChars = text.Length;

        var koreanTokens = koreanChars / 2.0;
        var otherTokens = (totalChars - koreanChars) / 4.0;

        return (int)Math.Ceiling(koreanTokens + otherTokens);
    }

    private static string GenerateSourceId(string url)
    {
        // URL에서 고유 ID 생성
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    [GeneratedRegex(@"\n\s*\n", RegexOptions.Compiled)]
    private static partial Regex ParagraphSplitRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.Compiled)]
    private static partial Regex SentenceSplitRegex();

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Debug, Message = "텍스트 청킹 완료: {ChunkCount}개 청크 생성, 소스: {SourceId}")]
    private static partial void LogTextChunkingCompleted(ILogger logger, int chunkCount, string sourceId);

    #endregion
}
