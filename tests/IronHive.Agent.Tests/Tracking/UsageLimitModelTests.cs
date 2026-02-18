using IronHive.Agent.Tracking;

namespace IronHive.Agent.Tests.Tracking;

public class UsageLimitResultTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var result = new UsageLimitResult();

        Assert.Equal(0, result.TokensUsed);
        Assert.Equal(0, result.TokenLimit);
        Assert.Equal(0m, result.CostUsed);
        Assert.Equal(0m, result.CostLimit);
        Assert.Equal(LimitStatus.Normal, result.TokenStatus);
        Assert.Equal(LimitStatus.Normal, result.CostStatus);
        Assert.False(result.ShouldStop);
        Assert.Equal(string.Empty, result.Message);
    }

    [Theory]
    [InlineData(500, 1000, 0.5f)]
    [InlineData(0, 1000, 0f)]
    [InlineData(1000, 1000, 1.0f)]
    [InlineData(800, 1000, 0.8f)]
    public void TokenPercentage_ShouldComputeCorrectly(int used, int limit, float expected)
    {
        var result = new UsageLimitResult { TokensUsed = used, TokenLimit = limit };
        Assert.NotNull(result.TokenPercentage);
        Assert.Equal(expected, result.TokenPercentage!.Value, 3);
    }

    [Fact]
    public void TokenPercentage_ShouldBeNull_WhenUnlimited()
    {
        var result = new UsageLimitResult { TokensUsed = 500, TokenLimit = 0 };
        Assert.Null(result.TokenPercentage);
    }

    [Fact]
    public void CostPercentage_ShouldComputeCorrectly()
    {
        var result = new UsageLimitResult { CostUsed = 0.5m, CostLimit = 1.0m };
        Assert.NotNull(result.CostPercentage);
        Assert.Equal(0.5f, result.CostPercentage!.Value, 3);
    }

    [Fact]
    public void CostPercentage_ShouldBeNull_WhenUnlimited()
    {
        var result = new UsageLimitResult { CostUsed = 0.5m, CostLimit = 0m };
        Assert.Null(result.CostPercentage);
    }
}

public class UsageLimitsConfigTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var config = new UsageLimitsConfig();

        Assert.Equal(0, config.MaxSessionTokens);
        Assert.Equal(0m, config.MaxSessionCost);
        Assert.Equal(0.8f, config.WarningThreshold);
        Assert.True(config.StopOnLimit);
    }

    [Fact]
    public void ShouldOverride_Defaults()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            MaxSessionCost = 5.0m,
            WarningThreshold = 0.9f,
            StopOnLimit = false
        };

        Assert.Equal(100000, config.MaxSessionTokens);
        Assert.Equal(5.0m, config.MaxSessionCost);
        Assert.Equal(0.9f, config.WarningThreshold);
        Assert.False(config.StopOnLimit);
    }
}

public class LimitStatusEnumTests
{
    [Fact]
    public void ShouldHaveThreeValues()
    {
        Assert.Equal(3, Enum.GetValues<LimitStatus>().Length);
    }

    [Theory]
    [InlineData(LimitStatus.Normal, 0)]
    [InlineData(LimitStatus.Warning, 1)]
    [InlineData(LimitStatus.Exceeded, 2)]
    public void ShouldHaveExpectedIntValues(LimitStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }
}
