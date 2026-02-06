using IronHive.Agent.Loop;
using IronHive.Agent.Tracking;

namespace IronHive.Agent.Tests.Agent;

public class UsageLimiterTests
{
    [Fact]
    public void CheckLimits_UnlimitedConfig_ReturnsNormal()
    {
        var config = new UsageLimitsConfig(); // Defaults to unlimited
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(100000, 10.00m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Normal, result.TokenStatus);
        Assert.Equal(LimitStatus.Normal, result.CostStatus);
        Assert.False(result.ShouldStop);
    }

    [Fact]
    public void CheckLimits_UnderLimit_ReturnsNormal()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            MaxSessionCost = 10.00m
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(50000, 5.00m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Normal, result.TokenStatus);
        Assert.Equal(LimitStatus.Normal, result.CostStatus);
        Assert.False(result.ShouldStop);
    }

    [Fact]
    public void CheckLimits_AtWarningThreshold_ReturnsWarning()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            WarningThreshold = 0.8f
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(85000, 0m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Warning, result.TokenStatus);
        Assert.False(result.ShouldStop);
    }

    [Fact]
    public void CheckLimits_ExceedsLimit_ReturnsExceeded()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            StopOnLimit = true
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(110000, 0m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Exceeded, result.TokenStatus);
        Assert.True(result.ShouldStop);
    }

    [Fact]
    public void CheckLimits_CostExceeds_ReturnsExceeded()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionCost = 5.00m,
            StopOnLimit = true
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(0, 6.00m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Exceeded, result.CostStatus);
        Assert.True(result.ShouldStop);
    }

    [Fact]
    public void CheckLimits_StopOnLimitFalse_DoesNotStop()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            StopOnLimit = false
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(110000, 0m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Exceeded, result.TokenStatus);
        Assert.False(result.ShouldStop);
    }

    [Fact]
    public void RecordTokenUsage_AccumulatesUsage()
    {
        var config = new UsageLimitsConfig();
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(1000, 0.10m);
        limiter.RecordTokenUsage(2000, 0.20m);
        limiter.RecordTokenUsage(3000, 0.30m);

        var (tokens, cost) = limiter.GetCurrentUsage();

        Assert.Equal(6000, tokens);
        Assert.Equal(0.60m, cost);
    }

    [Fact]
    public void Reset_ClearsUsage()
    {
        var config = new UsageLimitsConfig();
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(10000, 1.00m);
        limiter.Reset();

        var (tokens, cost) = limiter.GetCurrentUsage();

        Assert.Equal(0, tokens);
        Assert.Equal(0m, cost);
    }

    [Fact]
    public void TokenPercentage_WithLimit_ReturnsCorrectValue()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(50000, 0m);
        var result = limiter.CheckLimits();

        Assert.Equal(0.5f, result.TokenPercentage);
    }

    [Fact]
    public void TokenPercentage_NoLimit_ReturnsNull()
    {
        var config = new UsageLimitsConfig(); // Unlimited
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(50000, 0m);
        var result = limiter.CheckLimits();

        Assert.Null(result.TokenPercentage);
    }

    [Fact]
    public void CostPercentage_WithLimit_ReturnsCorrectValue()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionCost = 10.00m
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(0, 2.50m);
        var result = limiter.CheckLimits();

        Assert.Equal(0.25f, result.CostPercentage);
    }

    [Fact]
    public void Message_UnderLimit_IndicatesNormal()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(50000, 0m);
        var result = limiter.CheckLimits();

        Assert.Contains("within limits", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Message_AtWarning_IndicatesPercentage()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            WarningThreshold = 0.8f
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(85000, 0m);
        var result = limiter.CheckLimits();

        Assert.Contains("85", result.Message);
        Assert.Contains("100,000", result.Message);
    }

    [Fact]
    public void Message_Exceeded_IndicatesExceeded()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(110000, 0m);
        var result = limiter.CheckLimits();

        Assert.Contains("exceeded", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckLimits_BothLimitsExceeded_ReportsAll()
    {
        var config = new UsageLimitsConfig
        {
            MaxSessionTokens = 100000,
            MaxSessionCost = 5.00m,
            StopOnLimit = true
        };
        var limiter = new UsageLimiter(config);

        limiter.RecordTokenUsage(110000, 6.00m);
        var result = limiter.CheckLimits();

        Assert.Equal(LimitStatus.Exceeded, result.TokenStatus);
        Assert.Equal(LimitStatus.Exceeded, result.CostStatus);
        Assert.True(result.ShouldStop);
        Assert.Contains("Token limit exceeded", result.Message);
        Assert.Contains("Cost limit exceeded", result.Message);
    }
}
