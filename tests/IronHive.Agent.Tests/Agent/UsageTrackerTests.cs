using IronHive.Agent.Loop;
using IronHive.Agent.Tracking;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// Tests for UsageTracker session-level token tracking.
/// </summary>
public class UsageTrackerTests
{
    [Fact]
    public void Record_SingleUsage_TracksCorrectly()
    {
        // Arrange
        var tracker = new UsageTracker();
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };

        // Act
        tracker.Record(usage);
        var session = tracker.GetSessionUsage();

        // Assert
        Assert.Equal(100, session.TotalInputTokens);
        Assert.Equal(50, session.TotalOutputTokens);
        Assert.Equal(150, session.TotalTokens);
        Assert.Equal(1, session.RequestCount);
    }

    [Fact]
    public void Record_MultipleUsages_AccumulatesCorrectly()
    {
        // Arrange
        var tracker = new UsageTracker();

        // Act
        tracker.Record(new TokenUsage { InputTokens = 100, OutputTokens = 50 });
        tracker.Record(new TokenUsage { InputTokens = 200, OutputTokens = 100 });
        tracker.Record(new TokenUsage { InputTokens = 150, OutputTokens = 75 });

        var session = tracker.GetSessionUsage();

        // Assert
        Assert.Equal(450, session.TotalInputTokens);
        Assert.Equal(225, session.TotalOutputTokens);
        Assert.Equal(675, session.TotalTokens);
        Assert.Equal(3, session.RequestCount);
        Assert.Equal(225.0, session.AverageTokensPerRequest);
    }

    [Fact]
    public void Reset_ClearsAllStatistics()
    {
        // Arrange
        var tracker = new UsageTracker();
        tracker.Record(new TokenUsage { InputTokens = 100, OutputTokens = 50 });

        // Act
        tracker.Reset();
        var session = tracker.GetSessionUsage();

        // Assert
        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);
        Assert.Equal(0, session.RequestCount);
    }

    [Fact]
    public void GetSessionUsage_NoRecords_ReturnsZeros()
    {
        // Arrange
        var tracker = new UsageTracker();

        // Act
        var session = tracker.GetSessionUsage();

        // Assert
        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);
        Assert.Equal(0, session.TotalTokens);
        Assert.Equal(0, session.RequestCount);
        Assert.Equal(0, session.AverageTokensPerRequest);
    }

    [Fact]
    public void EstimatedCostUsd_CalculatesCorrectly()
    {
        // Arrange
        var tracker = new UsageTracker();

        // Act - 1M input tokens, 1M output tokens
        tracker.Record(new TokenUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000 });
        var session = tracker.GetSessionUsage();

        // Assert - Using GPT-4o-mini pricing: $0.15/1M input + $0.60/1M output = $0.75
        Assert.Equal(0.75m, session.EstimatedCostUsd);
    }

    [Fact]
    public void SessionUsage_TotalTokens_CalculatesCorrectly()
    {
        // Arrange
        var session = new SessionUsage
        {
            TotalInputTokens = 500,
            TotalOutputTokens = 300,
            RequestCount = 2
        };

        // Assert
        Assert.Equal(800, session.TotalTokens);
    }

    [Fact]
    public async Task Record_ThreadSafe_HandlesParallelCalls()
    {
        // Arrange
        var tracker = new UsageTracker();
        var tasks = new List<Task>();

        // Act - Record 100 usages in parallel
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                tracker.Record(new TokenUsage { InputTokens = 10, OutputTokens = 5 });
            }));
        }

        await Task.WhenAll(tasks);
        var session = tracker.GetSessionUsage();

        // Assert
        Assert.Equal(1000, session.TotalInputTokens);
        Assert.Equal(500, session.TotalOutputTokens);
        Assert.Equal(100, session.RequestCount);
    }
}
