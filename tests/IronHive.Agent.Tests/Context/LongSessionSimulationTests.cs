using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// Tests simulating long conversation sessions (100+ turns)
/// to verify context management stability.
/// </summary>
public class LongSessionSimulationTests
{
    private readonly ContextManager _contextManager;

    public LongSessionSimulationTests()
    {
        _contextManager = ContextManager.ForModel("gpt-4o");
    }

    [Fact]
    public void TokenCounter_HandlesLargeHistory()
    {
        // Simulate 100 turns
        var history = CreateLongHistory(100);

        var usage = _contextManager.GetUsage(history);

        Assert.True(usage.CurrentTokens > 0);
        Assert.Equal(201, usage.MessageCount); // System + 200 messages
    }

    [Fact]
    public async Task Compaction_ReducesLargeHistory()
    {
        // Create a history that exceeds typical context limits
        var history = CreateLongHistory(200);
        var originalTokens = _contextManager.TokenCounter.CountTokens(history);

        // Force compaction to 50% of original
        var targetTokens = originalTokens / 2;
        var result = await _contextManager.CompactAsync(history, targetTokens);

        Assert.True(result.CompactedTokens < originalTokens);
        Assert.True(result.MessagesCompacted > 0);
        Assert.True(result.CompressionRatio < 1.0f);
    }

    [Fact]
    public void GoalReminder_WorksWithLongHistory()
    {
        var history = CreateLongHistory(50);
        _contextManager.SetGoalFromHistory(history);

        Assert.NotNull(_contextManager.GoalReminder.CurrentGoal);
        Assert.True(_contextManager.GoalReminder.ShouldInjectReminder(history));
    }

    [Fact]
    public async Task PrepareHistory_HandlesLongSession()
    {
        var history = CreateLongHistory(50);
        _contextManager.SetGoalFromHistory(history);

        var prepared = await _contextManager.PrepareHistoryAsync(history);

        // Should have goal reminder appended
        Assert.True(prepared.Count >= history.Count);
    }

    [Fact]
    public void RemainingTokens_AccurateForLargeHistory()
    {
        var history = CreateLongHistory(100);

        var remaining = _contextManager.GetRemainingTokens(history);
        var usage = _contextManager.GetUsage(history);

        // Remaining should be threshold - current
        var expectedThreshold = (int)(_contextManager.MaxContextTokens * 0.92f);
        var expected = expectedThreshold - usage.CurrentTokens;

        Assert.Equal(expected, remaining);
    }

    [Fact]
    public async Task IncrementalGrowth_SimulatesRealConversation()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };

        _contextManager.SetGoal("Help me with a coding project");

        // Simulate incremental conversation growth
        for (var turn = 0; turn < 50; turn++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"Turn {turn}: Can you help me with task {turn}? " + new string('x', 50)));
            history.Add(new ChatMessage(ChatRole.Assistant, $"Sure, here's help for task {turn}. " + new string('y', 100)));

            // Prepare history each turn (simulates real usage)
            var prepared = await _contextManager.PrepareHistoryAsync(history);

            // Should never crash or fail
            Assert.NotNull(prepared);
            Assert.True(prepared.Count >= history.Count);
        }

        // Final state should be valid
        var finalUsage = _contextManager.GetUsage(history);
        Assert.True(finalUsage.CurrentTokens > 0);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void MessageCount_ScalesCorrectly(int turns)
    {
        var history = CreateLongHistory(turns);
        var usage = _contextManager.GetUsage(history);

        // Should be system + (turns * 2 messages per turn)
        Assert.Equal(1 + turns * 2, usage.MessageCount);
    }

    [Fact]
    public async Task Compaction_PreservesRecentContext()
    {
        var history = CreateLongHistory(100);

        // Add distinctive recent messages
        history.Add(new ChatMessage(ChatRole.User, "IMPORTANT_RECENT_USER_MESSAGE"));
        history.Add(new ChatMessage(ChatRole.Assistant, "IMPORTANT_RECENT_ASSISTANT_RESPONSE"));

        var result = await _contextManager.CompactAsync(history, 1000);

        // Recent messages should be preserved
        Assert.Contains(result.CompactedHistory,
            m => m.Text?.Contains("IMPORTANT_RECENT") == true);
    }

    [Fact]
    public void UsagePercentage_CalculatesCorrectly()
    {
        var history = CreateLongHistory(10);
        var usage = _contextManager.GetUsage(history);

        // For a small history, usage should be very low percentage
        Assert.True(usage.UsagePercentage < 0.1f);
        Assert.Equal((float)usage.CurrentTokens / usage.MaxTokens, usage.UsagePercentage);
    }

    private static List<ChatMessage> CreateLongHistory(int turns)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant specialized in software development.")
        };

        for (var i = 0; i < turns; i++)
        {
            // Vary message lengths to simulate real conversations
            var userLength = 30 + (i % 50);
            var assistantLength = 50 + (i % 100);

            history.Add(new ChatMessage(ChatRole.User,
                $"Question {i}: " + new string((char)('a' + (i % 26)), userLength)));
            history.Add(new ChatMessage(ChatRole.Assistant,
                $"Answer {i}: " + new string((char)('A' + (i % 26)), assistantLength)));
        }

        return history;
    }
}
