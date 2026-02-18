using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Mode;

public class RiskAssessmentTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var assessment = new RiskAssessment();

        Assert.False(assessment.IsRisky);
        Assert.Equal(RiskLevel.Low, assessment.Level);
        Assert.Null(assessment.Reason);
        Assert.Null(assessment.ApprovalPrompt);
    }

    [Fact]
    public void Safe_ShouldReturnNonRiskyAssessment()
    {
        var safe = RiskAssessment.Safe;

        Assert.False(safe.IsRisky);
        Assert.Equal(RiskLevel.Low, safe.Level);
        Assert.Null(safe.Reason);
        Assert.Null(safe.ApprovalPrompt);
    }

    [Fact]
    public void Risky_ShouldSetCorrectValues()
    {
        var risky = RiskAssessment.Risky(
            RiskLevel.High,
            "Destructive file operation",
            "Allow deleting this file?");

        Assert.True(risky.IsRisky);
        Assert.Equal(RiskLevel.High, risky.Level);
        Assert.Equal("Destructive file operation", risky.Reason);
        Assert.Equal("Allow deleting this file?", risky.ApprovalPrompt);
    }

    [Fact]
    public void Risky_WithoutApprovalPrompt_ShouldBeNull()
    {
        var risky = RiskAssessment.Risky(RiskLevel.Critical, "Denied command");
        Assert.Null(risky.ApprovalPrompt);
    }

    [Theory]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    [InlineData(RiskLevel.Critical)]
    public void Risky_ShouldAcceptAllLevels(RiskLevel level)
    {
        var risky = RiskAssessment.Risky(level, "Test reason");
        Assert.Equal(level, risky.Level);
        Assert.True(risky.IsRisky);
    }
}

public class RiskLevelEnumTests
{
    [Fact]
    public void ShouldHaveFourValues()
    {
        Assert.Equal(4, Enum.GetValues<RiskLevel>().Length);
    }

    [Theory]
    [InlineData(RiskLevel.Low, 0)]
    [InlineData(RiskLevel.Medium, 1)]
    [InlineData(RiskLevel.High, 2)]
    [InlineData(RiskLevel.Critical, 3)]
    public void ShouldHaveExpectedIntValues(RiskLevel level, int expected)
    {
        Assert.Equal(expected, (int)level);
    }
}
