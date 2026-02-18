using FluentAssertions;
using IronHive.DeepResearch.Models.Planning;
using Xunit;

namespace IronHive.DeepResearch.Tests.Models;

public class QueryPlanResultTests
{
    [Fact]
    public void ShouldInitialize_WithRequiredFields()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new QueryPlanResult
        {
            InitialQueries = [],
            Perspectives = [],
            SubQuestions = [],
            CreatedAt = now
        };

        result.InitialQueries.Should().BeEmpty();
        result.Perspectives.Should().BeEmpty();
        result.SubQuestions.Should().BeEmpty();
        result.CreatedAt.Should().Be(now);
    }

    [Fact]
    public void ShouldHold_ComplexPlan()
    {
        var result = new QueryPlanResult
        {
            InitialQueries = [
                new ExpandedQuery { Query = "AI trends 2025", Intent = "Overview", Priority = 1 },
                new ExpandedQuery { Query = "AI research papers", Intent = "Academic", Priority = 2, SearchType = QuerySearchType.Academic }
            ],
            Perspectives = [
                new ResearchPerspective { Id = "p-1", Name = "Technical", Description = "Technical analysis", KeyTopics = ["ML", "NLP"] }
            ],
            SubQuestions = [
                new SubQuestion { Id = "sq-1", Question = "What are latest trends?", Purpose = "Overview", Priority = 1 }
            ],
            CreatedAt = DateTimeOffset.UtcNow
        };

        result.InitialQueries.Should().HaveCount(2);
        result.Perspectives.Should().ContainSingle();
        result.SubQuestions.Should().ContainSingle();
    }
}

public class ExpandedQueryTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var query = new ExpandedQuery
        {
            Query = "test",
            Intent = "explore",
            Priority = 1
        };

        query.SearchType.Should().Be(QuerySearchType.Web);
        query.PerspectiveId.Should().BeNull();
        query.SubQuestionId.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Priority_ShouldAcceptValidValues(int priority)
    {
        var query = new ExpandedQuery
        {
            Query = "test",
            Intent = "explore",
            Priority = priority
        };

        query.Priority.Should().Be(priority);
    }
}

public class ResearchPerspectiveTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var perspective = new ResearchPerspective
        {
            Id = "p-1",
            Name = "Economic",
            Description = "Economic impact analysis"
        };

        perspective.KeyTopics.Should().BeEmpty();
    }

    [Fact]
    public void ShouldInitialize_WithKeyTopics()
    {
        var perspective = new ResearchPerspective
        {
            Id = "p-1",
            Name = "Technical",
            Description = "Technical deep dive",
            KeyTopics = ["Machine Learning", "Neural Networks", "Transformers"]
        };

        perspective.KeyTopics.Should().HaveCount(3);
    }
}

public class SubQuestionTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var question = new SubQuestion
        {
            Id = "sq-1",
            Question = "What is the impact?",
            Purpose = "Understand effect",
            Priority = 1
        };

        question.DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void ShouldTrack_Dependencies()
    {
        var question = new SubQuestion
        {
            Id = "sq-3",
            Question = "What is the combined effect?",
            Purpose = "Synthesis",
            Priority = 3,
            DependsOn = ["sq-1", "sq-2"]
        };

        question.DependsOn.Should().HaveCount(2);
        question.DependsOn.Should().Contain("sq-1");
    }
}

public class QueryExpansionOptionsTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var options = new QueryExpansionOptions();

        options.MaxSubQuestions.Should().Be(10);
        options.MaxPerspectives.Should().Be(5);
        options.MaxExpandedQueries.Should().Be(15);
        options.IncludeAcademic.Should().BeFalse();
        options.IncludeNews.Should().BeFalse();
        options.Language.Should().Be("ko");
    }

    [Fact]
    public void ShouldOverride_Defaults()
    {
        var options = new QueryExpansionOptions
        {
            MaxSubQuestions = 20,
            MaxPerspectives = 10,
            MaxExpandedQueries = 30,
            IncludeAcademic = true,
            IncludeNews = true,
            Language = "en"
        };

        options.MaxSubQuestions.Should().Be(20);
        options.Language.Should().Be("en");
    }
}

public class QuerySearchTypeEnumTests
{
    [Fact]
    public void ShouldHaveThreeValues()
    {
        Enum.GetValues<QuerySearchType>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(QuerySearchType.Web, 0)]
    [InlineData(QuerySearchType.News, 1)]
    [InlineData(QuerySearchType.Academic, 2)]
    public void ShouldHaveExpectedIntValues(QuerySearchType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }
}
