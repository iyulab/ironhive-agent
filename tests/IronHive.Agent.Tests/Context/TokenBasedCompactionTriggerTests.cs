using IronHive.Agent.Context;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// Cycle 7: Compaction - 토큰 기반 트리거 조건
/// </summary>
public class TokenBasedCompactionTriggerTests
{
    [Fact]
    public void Constructor_DefaultValues_SetsCorrectDefaults()
    {
        // Act
        var trigger = new TokenBasedCompactionTrigger();

        // Assert
        Assert.Equal(40_000, trigger.ProtectRecentTokens);
        Assert.Equal(20_000, trigger.MinimumPruneTokens);
    }

    [Fact]
    public void Constructor_CustomValues_SetsValues()
    {
        // Act
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 50_000,
            minimumPruneTokens: 10_000);

        // Assert
        Assert.Equal(50_000, trigger.ProtectRecentTokens);
        Assert.Equal(10_000, trigger.MinimumPruneTokens);
    }

    [Fact]
    public void Constructor_NegativeProtectTokens_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBasedCompactionTrigger(protectRecentTokens: -1));
    }

    [Fact]
    public void Constructor_NegativeMinimumPrune_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TokenBasedCompactionTrigger(minimumPruneTokens: -1));
    }

    #region ShouldCompact Tests

    [Fact]
    public void ShouldCompact_SufficientHeadroom_ReturnsFalse()
    {
        // Arrange: 60k tokens, max 100k → plenty of headroom
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act
        var result = trigger.ShouldCompact(60_000, 100_000);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCompact_ApproachingLimitWithEnoughPrunable_ReturnsTrue()
    {
        // Arrange: 90k tokens, max 100k → approaching limit, 50k prunable
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act
        var result = trigger.ShouldCompact(90_000, 100_000);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ShouldCompact_ApproachingLimitButNotEnoughPrunable_ReturnsFalse()
    {
        // Arrange: 50k tokens, max 60k → approaching limit, but only 10k prunable
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act
        var result = trigger.ShouldCompact(50_000, 60_000);

        // Assert: Not enough prunable tokens (10k < 20k minimum)
        Assert.False(result);
    }

    [Fact]
    public void ShouldCompact_ZeroMaxTokens_ReturnsFalse()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger();

        // Act
        var result = trigger.ShouldCompact(50_000, 0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ShouldCompact_NegativeMaxTokens_ReturnsFalse()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger();

        // Act
        var result = trigger.ShouldCompact(50_000, -1);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(70_000, 100_000, false)]  // 30k remaining > 20k (half of 40k), sufficient headroom
    [InlineData(80_000, 100_000, false)]  // 20k remaining = 20k (half of 40k), not yet approaching
    [InlineData(81_000, 100_000, true)]   // 19k remaining < 20k, approaching limit
    [InlineData(85_000, 100_000, true)]   // 15k remaining, approaching limit
    [InlineData(90_000, 100_000, true)]   // 10k remaining, clearly approaching limit
    public void ShouldCompact_BoundaryConditions(int currentTokens, int maxTokens, bool expected)
    {
        // Arrange: protectRecentTokens=40k, half=20k threshold
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act
        var result = trigger.ShouldCompact(currentTokens, maxTokens);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldCompact_ExactlyAtThreshold_Approaches()
    {
        // Arrange: Current = max - (protectRecentTokens / 2)
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // 100k - 40k = 60k current puts remaining at 40k, not approaching
        // 100k - 20k = 80k current puts remaining at 20k, exact boundary
        var result = trigger.ShouldCompact(80_000, 100_000);

        // At exactly the boundary (remaining == half of protect), not triggered
        Assert.False(result);
    }

    #endregion

    #region GetInfo Tests

    [Fact]
    public void GetInfo_ReturnsCorrectInfo()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act
        var info = trigger.GetInfo(90_000, 100_000);

        // Assert
        Assert.Equal(90_000, info.CurrentTokens);
        Assert.Equal(100_000, info.MaxTokens);
        Assert.Equal(40_000, info.ProtectedTokens);
        Assert.Equal(50_000, info.PrunableTokens);  // 90k - 40k
        Assert.Equal(10_000, info.TokensRemaining); // 100k - 90k
        Assert.True(info.ShouldCompact);
        Assert.NotNull(info.Reason);
    }

    [Fact]
    public void GetInfo_SufficientHeadroom_IndicatesInReason()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger();

        // Act
        var info = trigger.GetInfo(50_000, 100_000);

        // Assert
        Assert.False(info.ShouldCompact);
        Assert.Contains("remaining", info.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetInfo_NotEnoughPrunable_IndicatesInReason()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act: 50k tokens, 60k max → 10k prunable < 20k minimum
        var info = trigger.GetInfo(50_000, 60_000);

        // Assert
        Assert.False(info.ShouldCompact);
        Assert.Contains("need", info.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetInfo_PrunableTokens_NeverNegative()
    {
        // Arrange: Current tokens less than protect limit
        var trigger = new TokenBasedCompactionTrigger(
            protectRecentTokens: 40_000,
            minimumPruneTokens: 20_000);

        // Act: Only 20k tokens, but protect is 40k
        var info = trigger.GetInfo(20_000, 100_000);

        // Assert: Prunable should be 0, not negative
        Assert.Equal(0, info.PrunableTokens);
    }

    #endregion

    #region ThresholdPercentage Compatibility

    [Fact]
    public void ThresholdPercentage_Returns092ForCompatibility()
    {
        // Arrange
        var trigger = new TokenBasedCompactionTrigger();

        // Act & Assert
        Assert.Equal(0.92f, trigger.ThresholdPercentage);
    }

    #endregion
}
