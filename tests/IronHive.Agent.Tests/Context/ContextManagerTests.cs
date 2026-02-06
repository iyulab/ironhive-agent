using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class ContextManagerTests
{
    [Fact]
    public void GetUsage_ReturnsCorrectStatistics()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!")
        };

        var usage = manager.GetUsage(history);

        Assert.True(usage.CurrentTokens > 0);
        Assert.Equal(128000, usage.MaxTokens);
        Assert.Equal(3, usage.MessageCount);
        Assert.True(usage.UsagePercentage > 0);
        Assert.True(usage.UsagePercentage < 0.01); // Very small percentage
    }

    [Fact]
    public void GetUsage_NeedsCompaction_FalseWhenBelowThreshold()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var usage = manager.GetUsage(history);

        Assert.False(usage.NeedsCompaction);
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenBelowThreshold()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        Assert.False(manager.ShouldCompact(history));
    }

    [Fact]
    public void GetRemainingTokens_ReturnsPositiveValue()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var remaining = manager.GetRemainingTokens(history);

        // Should have almost all tokens remaining
        Assert.True(remaining > 100000);
    }

    [Fact]
    public async Task CompactIfNeededAsync_NoCompactionNeeded_ReturnsOriginal()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        var result = await manager.CompactIfNeededAsync(history);

        Assert.Equal(history.Count, result.CompactedHistory.Count);
        Assert.Equal(0, result.MessagesCompacted);
    }

    [Fact]
    public async Task CompactAsync_ForcesCompaction()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        var history = CreateLargeHistory(20);
        var originalTokens = manager.TokenCounter.CountTokens(history);

        var result = await manager.CompactAsync(history, originalTokens / 4);

        Assert.True(result.CompactedTokens < originalTokens);
    }

    [Fact]
    public void ForModel_CreatesCorrectManager()
    {
        var manager = ContextManager.ForModel("claude-3.5-sonnet");

        Assert.Equal(200000, manager.MaxContextTokens);
        Assert.Equal("claude-3.5-sonnet", manager.TokenCounter.ModelName);
    }

    [Fact]
    public void MaxContextTokens_ReturnsTokenCounterValue()
    {
        var tokenCounter = new ContextTokenCounter("gpt-4o", 50000);
        var manager = new ContextManager(tokenCounter);

        Assert.Equal(50000, manager.MaxContextTokens);
    }

    private static List<ChatMessage> CreateLargeHistory(int turns)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant. " + new string('x', 100))
        };

        for (var i = 0; i < turns; i++)
        {
            history.Add(new ChatMessage(ChatRole.User,
                $"User message {i}: " + new string('a', 50 + i * 10)));
            history.Add(new ChatMessage(ChatRole.Assistant,
                $"Assistant response {i}: " + new string('b', 100 + i * 20)));
        }

        return history;
    }
}
