using FluentAssertions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Research;
using Xunit;

namespace IronHive.DeepResearch.Tests.Models;

public class ResearchProgressTests
{
    [Fact]
    public void ShouldInitialize_WithMinimalFields()
    {
        var progress = new ResearchProgress
        {
            Type = ProgressType.Started,
            CurrentIteration = 1,
            MaxIterations = 5,
            Timestamp = DateTimeOffset.UtcNow
        };

        progress.Plan.Should().BeNull();
        progress.Search.Should().BeNull();
        progress.Content.Should().BeNull();
        progress.Analysis.Should().BeNull();
        progress.ReportChunk.Should().BeNull();
        progress.Result.Should().BeNull();
        progress.Error.Should().BeNull();
    }

    [Fact]
    public void ShouldInitialize_WithPlanProgress()
    {
        var progress = new ResearchProgress
        {
            Type = ProgressType.PlanGenerated,
            CurrentIteration = 1,
            MaxIterations = 5,
            Timestamp = DateTimeOffset.UtcNow,
            Plan = new PlanProgress
            {
                GeneratedQueries = ["query1", "query2"],
                ResearchAngles = ["angle1"]
            }
        };

        progress.Plan.Should().NotBeNull();
        progress.Plan!.GeneratedQueries.Should().HaveCount(2);
        progress.Plan.ResearchAngles.Should().ContainSingle();
    }

    [Fact]
    public void ShouldInitialize_WithSearchProgress()
    {
        var progress = new ResearchProgress
        {
            Type = ProgressType.SearchCompleted,
            CurrentIteration = 2,
            MaxIterations = 5,
            Timestamp = DateTimeOffset.UtcNow,
            Search = new SearchProgress
            {
                Query = "AI trends",
                Provider = "tavily",
                ResultCount = 10
            }
        };

        progress.Search!.Query.Should().Be("AI trends");
        progress.Search.ResultCount.Should().Be(10);
    }

    [Fact]
    public void ShouldInitialize_WithContentProgress()
    {
        var progress = new ResearchProgress
        {
            Type = ProgressType.ContentExtracted,
            CurrentIteration = 1,
            MaxIterations = 3,
            Timestamp = DateTimeOffset.UtcNow,
            Content = new ContentProgress
            {
                Url = "https://example.com",
                ContentLength = 5000,
                Success = true
            }
        };

        progress.Content!.ContentLength.Should().Be(5000);
        progress.Content.Success.Should().BeTrue();
    }

    [Fact]
    public void ShouldInitialize_WithAnalysisProgress()
    {
        var score = new SufficiencyScore { OverallScore = 0.85m };
        var progress = new ResearchProgress
        {
            Type = ProgressType.AnalysisCompleted,
            CurrentIteration = 3,
            MaxIterations = 5,
            Timestamp = DateTimeOffset.UtcNow,
            Analysis = new AnalysisProgress
            {
                FindingsCount = 12,
                GapsIdentified = 3,
                Score = score
            }
        };

        progress.Analysis!.FindingsCount.Should().Be(12);
        progress.Analysis.GapsIdentified.Should().Be(3);
        progress.Analysis.Score.IsSufficient.Should().BeTrue();
    }

    [Fact]
    public void ShouldInitialize_WithReportChunk()
    {
        var progress = new ResearchProgress
        {
            Type = ProgressType.ReportChunk,
            CurrentIteration = 5,
            MaxIterations = 5,
            Timestamp = DateTimeOffset.UtcNow,
            ReportChunk = "## Introduction\n\nThis report covers..."
        };

        progress.ReportChunk.Should().StartWith("## Introduction");
    }
}

public class ProgressTypeEnumTests
{
    [Fact]
    public void ShouldHaveSixteenValues()
    {
        Enum.GetValues<ProgressType>().Should().HaveCount(16);
    }

    [Theory]
    [InlineData(ProgressType.Started, 0)]
    [InlineData(ProgressType.PlanGenerated, 1)]
    [InlineData(ProgressType.SearchStarted, 2)]
    [InlineData(ProgressType.SearchCompleted, 3)]
    [InlineData(ProgressType.ContentExtractionStarted, 4)]
    [InlineData(ProgressType.ContentExtracted, 5)]
    [InlineData(ProgressType.AnalysisStarted, 6)]
    [InlineData(ProgressType.AnalysisCompleted, 7)]
    [InlineData(ProgressType.SufficiencyEvaluated, 8)]
    [InlineData(ProgressType.IterationCompleted, 9)]
    [InlineData(ProgressType.ReportGenerationStarted, 10)]
    [InlineData(ProgressType.ReportSection, 11)]
    [InlineData(ProgressType.ReportChunk, 12)]
    [InlineData(ProgressType.Completed, 13)]
    [InlineData(ProgressType.Failed, 14)]
    [InlineData(ProgressType.Checkpoint, 15)]
    public void ShouldHaveExpectedIntValues(ProgressType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }
}

public class PlanProgressTests
{
    [Fact]
    public void ShouldInitialize_WithRequiredFields()
    {
        var plan = new PlanProgress
        {
            GeneratedQueries = ["q1", "q2", "q3"],
            ResearchAngles = ["technical", "economic"]
        };

        plan.GeneratedQueries.Should().HaveCount(3);
        plan.ResearchAngles.Should().HaveCount(2);
    }
}

public class SearchProgressTests
{
    [Fact]
    public void ShouldInitialize_WithRequiredFields()
    {
        var search = new SearchProgress
        {
            Query = "AI trends 2025",
            Provider = "tavily",
            ResultCount = 15
        };

        search.Query.Should().Be("AI trends 2025");
        search.Provider.Should().Be("tavily");
        search.ResultCount.Should().Be(15);
    }
}

public class ContentProgressTests
{
    [Fact]
    public void ShouldInitialize_Success()
    {
        var content = new ContentProgress
        {
            Url = "https://example.com",
            ContentLength = 3000,
            Success = true
        };

        content.Success.Should().BeTrue();
    }

    [Fact]
    public void ShouldInitialize_Failure()
    {
        var content = new ContentProgress
        {
            Url = "https://broken.com",
            ContentLength = 0,
            Success = false
        };

        content.Success.Should().BeFalse();
        content.ContentLength.Should().Be(0);
    }
}

public class AnalysisProgressTests
{
    [Fact]
    public void ShouldInitialize_WithScore()
    {
        var analysis = new AnalysisProgress
        {
            FindingsCount = 8,
            GapsIdentified = 2,
            Score = new SufficiencyScore { OverallScore = 0.75m }
        };

        analysis.FindingsCount.Should().Be(8);
        analysis.GapsIdentified.Should().Be(2);
        analysis.Score.IsSufficient.Should().BeFalse();
    }
}
