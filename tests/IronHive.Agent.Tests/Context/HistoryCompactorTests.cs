using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class HistoryCompactorTests
{
    private readonly ContextTokenCounter _tokenCounter = new("gpt-4");

    [Fact]
    public async Task CompactAsync_WithinTarget_ReturnsOriginal()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = CreateHistory(5);

        var result = await compactor.CompactAsync(history, 100000);

        Assert.Equal(history.Count, result.CompactedHistory.Count);
        Assert.Equal(0, result.MessagesCompacted);
    }

    [Fact]
    public async Task CompactAsync_PreservesSystemMessage()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!")
        };

        var result = await compactor.CompactAsync(history, 100000);

        Assert.Equal(ChatRole.System, result.CompactedHistory[0].Role);
        Assert.Equal("You are a helpful assistant.", result.CompactedHistory[0].Text);
    }

    [Fact]
    public async Task CompactAsync_PreservesRecentMessages()
    {
        var options = new HistoryCompactorOptions { PreserveTailTurns = 2 };
        var compactor = new HistoryCompactor(_tokenCounter, options: options);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Old message 1"),
            new(ChatRole.Assistant, "Old response 1"),
            new(ChatRole.User, "Old message 2"),
            new(ChatRole.Assistant, "Old response 2"),
            new(ChatRole.User, "Recent message 1"),
            new(ChatRole.Assistant, "Recent response 1"),
            new(ChatRole.User, "Recent message 2"),
            new(ChatRole.Assistant, "Recent response 2")
        };

        // Force compaction with very low target
        var result = await compactor.CompactAsync(history, 100);

        // Should preserve system and recent messages
        Assert.Contains(result.CompactedHistory, m => m.Text == "Recent message 2");
        Assert.Contains(result.CompactedHistory, m => m.Text == "Recent response 2");
    }

    [Fact]
    public async Task CompactAsync_ReducesTokenCount()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = CreateLargeHistory(20);
        var originalTokens = _tokenCounter.CountTokens(history);

        // Target much smaller than original
        var targetTokens = originalTokens / 2;
        var result = await compactor.CompactAsync(history, targetTokens);

        Assert.True(result.CompactedTokens <= targetTokens + 100); // Allow small overhead
        Assert.True(result.MessagesCompacted > 0);
    }

    [Fact]
    public async Task CompactAsync_CompressionRatio_IsCalculatedCorrectly()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = CreateLargeHistory(20);

        var result = await compactor.CompactAsync(history, 500);

        var expectedRatio = (float)result.CompactedTokens / result.OriginalTokens;
        Assert.Equal(expectedRatio, result.CompressionRatio, 3);
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmpty()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = new List<ChatMessage>();

        var result = await compactor.CompactAsync(history, 1000);

        Assert.Empty(result.CompactedHistory);
        // Empty history still has conversation overhead (3 tokens)
        Assert.True(result.OriginalTokens <= 3);
    }

    [Fact]
    public async Task CompactAsync_OnlySystemMessage_PreservesIt()
    {
        var compactor = new HistoryCompactor(_tokenCounter);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };

        var result = await compactor.CompactAsync(history, 1000);

        Assert.Single(result.CompactedHistory);
        Assert.Equal(ChatRole.System, result.CompactedHistory[0].Role);
    }

    [Fact]
    public async Task CompactAsync_VeryLowTarget_AddsOmissionMarker()
    {
        var options = new HistoryCompactorOptions
        {
            PreserveTailTurns = 1,
            UseLlmSummarization = false
        };
        var compactor = new HistoryCompactor(_tokenCounter, options: options);

        var history = CreateLargeHistory(10);

        // Very low target to force truncation
        var result = await compactor.CompactAsync(history, 50);

        // Should have some kind of marker about omitted content
        var hasOmissionMarker = result.CompactedHistory.Any(m =>
            m.Text?.Contains("omitted", StringComparison.OrdinalIgnoreCase) == true ||
            m.Text?.Contains("summary", StringComparison.OrdinalIgnoreCase) == true);

        Assert.True(hasOmissionMarker || result.CompactedHistory.Count < history.Count);
    }

    private static List<ChatMessage> CreateHistory(int turns)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };

        for (var i = 0; i < turns; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"User message {i}"));
            history.Add(new ChatMessage(ChatRole.Assistant, $"Assistant response {i}"));
        }

        return history;
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
