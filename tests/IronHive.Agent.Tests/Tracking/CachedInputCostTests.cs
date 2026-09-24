using IronHive.Agent.Loop;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Tests.Tracking;

/// <summary>
/// Prompt-cache reads are priced at the cache-read rate. A provider reports them as part of the input
/// (<see cref="UsageDetails.CachedInputTokenCount"/> ⊂ <see cref="UsageDetails.InputTokenCount"/>); pricing every input
/// token at the full rate overstated the cost of a cache hit — and IronHive 0.37.0 made Anthropic's input include
/// cache reads, so the overstatement reached every provider.
/// </summary>
public class CachedInputCostTests
{
    private static readonly ModelInfo Pricing = new()
    {
        ModelId = "test-model",
        Provider = "Test",
        DisplayName = "Test",
        InputPricePerMillion = 3m,
        OutputPricePerMillion = 15m,
        CacheReadPricePerMillion = 0.3m,
    };

    [Fact]
    public void From_CarriesTheCachedInputCount()
    {
        var usage = TokenUsage.From(new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 10, CachedInputTokenCount = 800 })!;

        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(800, usage.CachedInputTokens);
        Assert.Null(TokenUsage.From(null));
    }

    [Fact]
    public void CostAt_PricesCachedInputAtTheCacheReadRate()
    {
        var usage = new TokenUsage { InputTokens = 1_000_000, OutputTokens = 0, CachedInputTokens = 800_000 };

        // 200k uncached at $3/M + 800k cached at $0.30/M = $0.60 + $0.24
        Assert.Equal(0.84m, usage.CostAt(Pricing));
        Assert.Equal(3m, (usage with { CachedInputTokens = 0 }).CostAt(Pricing));
        Assert.Null(usage.CostAt(null));
    }

    [Fact]
    public void CostAt_ClampsAReportedCacheLargerThanTheInput()
    {
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 0, CachedInputTokens = 500 };

        Assert.Equal((100 / 1_000_000m) * 0.3m, usage.CostAt(Pricing));
    }

    [Fact]
    public void UsageTracker_SumsCachedInput_AndPricesTheSession()
    {
        var model = ModelCatalog.All.Values.FirstOrDefault(m => m.CacheReadPricePerMillion is not null && m.InputPricePerMillion is not null && m.OutputPricePerMillion is not null);
        Assert.NotNull(model);
        var tracker = new UsageTracker();
        tracker.SetModel(model.ModelId);

        tracker.Record(new TokenUsage { InputTokens = 1_000_000, OutputTokens = 0, CachedInputTokens = 1_000_000 });
        var session = tracker.GetSessionUsage();

        Assert.Equal(1_000_000, session.TotalCachedInputTokens);
        Assert.Equal(model.CacheReadPricePerMillion!.Value, session.EstimatedCostUsd);
    }
}
