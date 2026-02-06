using IronHive.Agent.Context;

namespace IronHive.Agent.Tests.Context;

public class CompactionTriggerTests
{
    [Fact]
    public void ShouldCompact_BelowThreshold_ReturnsFalse()
    {
        var trigger = new ThresholdCompactionTrigger(0.92f);
        Assert.False(trigger.ShouldCompact(8000, 10000)); // 80%
    }

    [Fact]
    public void ShouldCompact_AtThreshold_ReturnsTrue()
    {
        var trigger = new ThresholdCompactionTrigger(0.92f);
        Assert.True(trigger.ShouldCompact(9200, 10000)); // 92%
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var trigger = new ThresholdCompactionTrigger(0.92f);
        Assert.True(trigger.ShouldCompact(9500, 10000)); // 95%
    }

    [Fact]
    public void ShouldCompact_ZeroMaxTokens_ReturnsFalse()
    {
        var trigger = new ThresholdCompactionTrigger(0.92f);
        Assert.False(trigger.ShouldCompact(100, 0));
    }

    [Fact]
    public void ShouldCompact_CustomThreshold_UsesCustomValue()
    {
        var trigger = new ThresholdCompactionTrigger(0.80f);
        Assert.True(trigger.ShouldCompact(8000, 10000)); // 80%
        Assert.False(trigger.ShouldCompact(7900, 10000)); // 79%
    }

    [Fact]
    public void ThresholdPercentage_ReturnsConfiguredValue()
    {
        var trigger = new ThresholdCompactionTrigger(0.85f);
        Assert.Equal(0.85f, trigger.ThresholdPercentage);
    }

    [Theory]
    [InlineData(0.49f)]
    [InlineData(1.01f)]
    [InlineData(-0.1f)]
    public void Constructor_InvalidThreshold_ThrowsArgumentOutOfRangeException(float threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThresholdCompactionTrigger(threshold));
    }

    [Fact]
    public void DefaultThreshold_Is92Percent()
    {
        var trigger = new ThresholdCompactionTrigger();
        Assert.Equal(0.92f, trigger.ThresholdPercentage);
    }
}
