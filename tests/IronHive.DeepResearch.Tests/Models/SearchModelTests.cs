using FluentAssertions;
using IronHive.DeepResearch.Models.Search;
using Xunit;

namespace IronHive.DeepResearch.Tests.Models;

public class SearchQueryTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var query = new SearchQuery { Query = "test" };

        query.Type.Should().Be(SearchType.Web);
        query.Depth.Should().Be(QueryDepth.Basic);
        query.MaxResults.Should().Be(10);
        query.IncludeContent.Should().BeFalse();
        query.IncludeDomains.Should().BeNull();
        query.ExcludeDomains.Should().BeNull();
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var query = new SearchQuery
        {
            Query = "AI research",
            Type = SearchType.Academic,
            Depth = QueryDepth.Deep,
            MaxResults = 20,
            IncludeContent = true,
            IncludeDomains = ["arxiv.org", "scholar.google.com"],
            ExcludeDomains = ["example.com"]
        };

        query.Query.Should().Be("AI research");
        query.Type.Should().Be(SearchType.Academic);
        query.Depth.Should().Be(QueryDepth.Deep);
        query.MaxResults.Should().Be(20);
        query.IncludeContent.Should().BeTrue();
        query.IncludeDomains.Should().HaveCount(2);
        query.ExcludeDomains.Should().ContainSingle();
    }
}

public class SearchResultTests
{
    [Fact]
    public void ShouldInitialize_WithRequiredFields()
    {
        var now = DateTimeOffset.UtcNow;
        var query = new SearchQuery { Query = "test" };
        var result = new SearchResult
        {
            Query = query,
            Provider = "tavily",
            Sources = [],
            SearchedAt = now
        };

        result.Query.Should().BeSameAs(query);
        result.Provider.Should().Be("tavily");
        result.Answer.Should().BeNull();
        result.Sources.Should().BeEmpty();
        result.SearchedAt.Should().Be(now);
    }

    [Fact]
    public void ShouldInclude_Answer_WhenProvided()
    {
        var result = new SearchResult
        {
            Query = new SearchQuery { Query = "What is AI?" },
            Provider = "tavily",
            Answer = "AI is artificial intelligence.",
            Sources = [],
            SearchedAt = DateTimeOffset.UtcNow
        };

        result.Answer.Should().Be("AI is artificial intelligence.");
    }
}

public class SearchSourceTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var source = new SearchSource
        {
            Url = "https://example.com",
            Title = "Example"
        };

        source.Snippet.Should().BeNull();
        source.RawContent.Should().BeNull();
        source.Score.Should().Be(0);
        source.PublishedDate.Should().BeNull();
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var published = DateTimeOffset.Parse("2025-01-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var source = new SearchSource
        {
            Url = "https://example.com/article",
            Title = "An Article",
            Snippet = "Summary text...",
            RawContent = "Full content...",
            Score = 0.95,
            PublishedDate = published
        };

        source.Score.Should().Be(0.95);
        source.PublishedDate.Should().Be(published);
    }
}

public class SearchTypeEnumTests
{
    [Fact]
    public void ShouldHaveFourValues()
    {
        Enum.GetValues<SearchType>().Should().HaveCount(4);
    }

    [Theory]
    [InlineData(SearchType.Web, 0)]
    [InlineData(SearchType.News, 1)]
    [InlineData(SearchType.Academic, 2)]
    [InlineData(SearchType.Image, 3)]
    public void ShouldHaveExpectedIntValues(SearchType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }
}

public class QueryDepthEnumTests
{
    [Fact]
    public void ShouldHaveTwoValues()
    {
        Enum.GetValues<QueryDepth>().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(QueryDepth.Basic, 0)]
    [InlineData(QueryDepth.Deep, 1)]
    public void ShouldHaveExpectedIntValues(QueryDepth depth, int expected)
    {
        ((int)depth).Should().Be(expected);
    }
}
