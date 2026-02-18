using FluentAssertions;
using IronHive.DeepResearch.Models.Analysis;
using Xunit;

namespace IronHive.DeepResearch.Tests.Models;

public class FindingTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var finding = new Finding
        {
            Id = "f-1",
            Claim = "Test claim",
            SourceId = "src-1",
            IterationDiscovered = 1,
            DiscoveredAt = DateTimeOffset.UtcNow
        };

        finding.EvidenceQuote.Should().BeNull();
        finding.VerificationScore.Should().Be(0);
        finding.IsVerified.Should().BeFalse();
    }

    [Fact]
    public void ShouldInitialize_WithAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var finding = new Finding
        {
            Id = "f-2",
            Claim = "AI improves productivity",
            SourceId = "src-2",
            EvidenceQuote = "Studies show 30% improvement...",
            VerificationScore = 0.85m,
            IsVerified = true,
            IterationDiscovered = 2,
            DiscoveredAt = now
        };

        finding.VerificationScore.Should().Be(0.85m);
        finding.IsVerified.Should().BeTrue();
        finding.EvidenceQuote.Should().Contain("30%");
    }
}

public class InformationGapTests
{
    [Fact]
    public void ShouldInitialize_WithRequiredFields()
    {
        var now = DateTimeOffset.UtcNow;
        var gap = new InformationGap
        {
            Description = "Missing cost analysis",
            Priority = GapPriority.High,
            SuggestedQuery = "AI implementation cost comparison",
            IdentifiedAt = now
        };

        gap.Description.Should().Be("Missing cost analysis");
        gap.Priority.Should().Be(GapPriority.High);
        gap.SuggestedQuery.Should().Contain("cost");
        gap.IdentifiedAt.Should().Be(now);
    }
}

public class SufficiencyScoreTests
{
    [Fact]
    public void DefaultValues_ShouldBeZero()
    {
        var score = new SufficiencyScore();

        score.OverallScore.Should().Be(0);
        score.CoverageScore.Should().Be(0);
        score.SourceDiversityScore.Should().Be(0);
        score.QualityScore.Should().Be(0);
        score.FreshnessScore.Should().Be(0);
        score.NewFindingsCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.5, false)]
    [InlineData(0.79, false)]
    [InlineData(0.8, true)]
    [InlineData(0.9, true)]
    [InlineData(1.0, true)]
    public void IsSufficient_ShouldCheckThreshold(double overall, bool expected)
    {
        var score = new SufficiencyScore { OverallScore = (decimal)overall };

        score.IsSufficient.Should().Be(expected);
    }

    [Fact]
    public void ShouldInitialize_WithAllScores()
    {
        var score = new SufficiencyScore
        {
            OverallScore = 0.85m,
            CoverageScore = 0.9m,
            SourceDiversityScore = 0.7m,
            QualityScore = 0.88m,
            FreshnessScore = 0.95m,
            NewFindingsCount = 5,
            EvaluatedAt = DateTimeOffset.UtcNow
        };

        score.IsSufficient.Should().BeTrue();
        score.NewFindingsCount.Should().Be(5);
    }
}

public class GapPriorityEnumTests
{
    [Fact]
    public void ShouldHaveThreeValues()
    {
        Enum.GetValues<GapPriority>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(GapPriority.Low, 0)]
    [InlineData(GapPriority.Medium, 1)]
    [InlineData(GapPriority.High, 2)]
    public void ShouldHaveExpectedIntValues(GapPriority priority, int expected)
    {
        ((int)priority).Should().Be(expected);
    }
}
