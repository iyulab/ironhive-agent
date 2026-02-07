using System.Net;
using System.Text.RegularExpressions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Content;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Content;

/// <summary>
/// HTML 콘텐츠 처리기 (정제 및 텍스트 추출)
/// </summary>
public partial class ContentProcessor
{
    private readonly ILogger<ContentProcessor> _logger;

    public ContentProcessor(ILogger<ContentProcessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// HTML을 정제하여 ExtractedContent 생성
    /// </summary>
    public ExtractedContent Process(string html, Uri baseUri, ContentExtractionOptions options)
    {
        try
        {
            // 제목 추출
            var title = ExtractTitle(html);

            // 메타 설명 추출
            var description = ExtractMetaDescription(html);

            // 저자 추출
            string? author = null;
            DateTimeOffset? publishedDate = null;

            if (options.ExtractMetadata)
            {
                author = ExtractAuthor(html);
                publishedDate = ExtractPublishedDate(html);
            }

            // 본문 텍스트 추출
            var content = ExtractMainContent(html, options.MaxContentLength);

            // 링크 추출
            List<string>? links = null;
            if (options.ExtractLinks)
            {
                links = ExtractLinks(html, baseUri);
            }

            // 이미지 추출
            List<string>? images = null;
            if (options.ExtractImages)
            {
                images = ExtractImages(html, baseUri);
            }

            return new ExtractedContent
            {
                Url = baseUri.ToString(),
                Title = title,
                Description = description,
                Content = content,
                Author = author,
                PublishedDate = publishedDate,
                ContentLength = content?.Length ?? 0,
                ExtractedAt = DateTimeOffset.UtcNow,
                Success = true,
                Links = links,
                Images = images
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "콘텐츠 처리 실패: {Url}", baseUri);
            return new ExtractedContent
            {
                Url = baseUri.ToString(),
                Success = false,
                ErrorMessage = ex.Message,
                ExtractedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private static string? ExtractTitle(string html)
    {
        // <title> 태그에서 추출
        var match = TitleRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        // og:title 메타 태그에서 추출
        match = OgTitleRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        return null;
    }

    private static string? ExtractMetaDescription(string html)
    {
        // meta description
        var match = MetaDescriptionRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        // og:description
        match = OgDescriptionRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        return null;
    }

    private static string? ExtractAuthor(string html)
    {
        // meta author
        var match = MetaAuthorRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        // article:author
        match = ArticleAuthorRegex().Match(html);
        if (match.Success)
        {
            return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }

        return null;
    }

    private static DateTimeOffset? ExtractPublishedDate(string html)
    {
        // article:published_time
        var match = ArticlePublishedTimeRegex().Match(html);
        if (match.Success && DateTimeOffset.TryParse(match.Groups[1].Value, out var date))
        {
            return date;
        }

        // datePublished (schema.org)
        match = DatePublishedRegex().Match(html);
        if (match.Success && DateTimeOffset.TryParse(match.Groups[1].Value, out date))
        {
            return date;
        }

        return null;
    }

    private static string ExtractMainContent(string html, int maxLength)
    {
        var content = html;

        // 스크립트 제거
        content = ScriptRegex().Replace(content, " ");

        // 스타일 제거
        content = StyleRegex().Replace(content, " ");

        // 주석 제거
        content = CommentRegex().Replace(content, " ");

        // 네비게이션, 헤더, 푸터, 사이드바 제거 (일반적인 패턴)
        content = NavRegex().Replace(content, " ");
        content = HeaderRegex().Replace(content, " ");
        content = FooterRegex().Replace(content, " ");
        content = AsideRegex().Replace(content, " ");

        // 모든 HTML 태그 제거
        content = HtmlTagRegex().Replace(content, " ");

        // HTML 엔티티 디코딩
        content = WebUtility.HtmlDecode(content);

        // 연속 공백 정리
        content = WhitespaceRegex().Replace(content, " ");

        // 트리밍
        content = content.Trim();

        // 최대 길이 제한
        if (content.Length > maxLength)
        {
            // 문장 경계에서 자르기 시도
            var truncated = content[..maxLength];
            var lastSentenceEnd = truncated.LastIndexOfAny(['.', '!', '?', '\n']);
            if (lastSentenceEnd > maxLength * 0.7)
            {
                content = truncated[..(lastSentenceEnd + 1)];
            }
            else
            {
                content = truncated;
            }
        }

        return content;
    }

    private static List<string> ExtractLinks(string html, Uri baseUri)
    {
        var links = new List<string>();
        var matches = HrefRegex().Matches(html);

        foreach (Match match in matches)
        {
            var href = match.Groups[1].Value;
            if (Uri.TryCreate(baseUri, href, out var absoluteUri) &&
                (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https"))
            {
                links.Add(absoluteUri.ToString());
            }
        }

        return links.Distinct().Take(100).ToList();
    }

    private static List<string> ExtractImages(string html, Uri baseUri)
    {
        var images = new List<string>();
        var matches = ImgSrcRegex().Matches(html);

        foreach (Match match in matches)
        {
            var src = match.Groups[1].Value;
            if (Uri.TryCreate(baseUri, src, out var absoluteUri) &&
                (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https"))
            {
                images.Add(absoluteUri.ToString());
            }
        }

        return images.Distinct().Take(50).ToList();
    }

    // 정규식 패턴 (컴파일된 형태로 성능 최적화)
    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<meta[^>]+name=[""']description[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescriptionRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:description[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgDescriptionRegex();

    [GeneratedRegex(@"<meta[^>]+name=[""']author[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaAuthorRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']article:author[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleAuthorRegex();

    [GeneratedRegex(@"<meta[^>]+property=[""']article:published_time[""'][^>]+content=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ArticlePublishedTimeRegex();

    [GeneratedRegex(@"""datePublished""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex DatePublishedRegex();

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.IgnoreCase)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<nav[^>]*>[\s\S]*?</nav>", RegexOptions.IgnoreCase)]
    private static partial Regex NavRegex();

    [GeneratedRegex(@"<header[^>]*>[\s\S]*?</header>", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"<footer[^>]*>[\s\S]*?</footer>", RegexOptions.IgnoreCase)]
    private static partial Regex FooterRegex();

    [GeneratedRegex(@"<aside[^>]*>[\s\S]*?</aside>", RegexOptions.IgnoreCase)]
    private static partial Regex AsideRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();
}
