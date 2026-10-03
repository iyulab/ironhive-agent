using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// Cycle 8-10: Compaction - 보호 영역 분할 및 LLM 요약
/// </summary>
public class TokenBasedHistoryCompactorTests
{
    #region Basic Compaction

    [Fact]
    public async Task CompactAsync_WithinTarget_ReturnsUnchanged()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 40_000,
            MinimumPruneTokens = 20_000
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config);

        var history = CreateHistory(5); // 5 * 100 = 500 tokens

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 1000, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(history.Count, result.CompactedHistory.Count);
        Assert.Equal(0, result.MessagesCompacted);
    }

    [Fact]
    public async Task CompactAsync_SystemMessagesAlwaysPreserved()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 500,  // Protect last 5 messages
            MinimumPruneTokens = 100
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt 1"),
            new(ChatRole.System, "System prompt 2"),
            new(ChatRole.User, "User message 1"),
            new(ChatRole.Assistant, "Assistant message 1"),
            new(ChatRole.User, "User message 2"),
            new(ChatRole.Assistant, "Assistant message 2"),
            new(ChatRole.User, "User message 3"),
            new(ChatRole.Assistant, "Assistant message 3"),
        };

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 400, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: System messages are preserved
        var systemMessages = result.CompactedHistory.Where(m => m.Role == ChatRole.System).ToList();
        Assert.True(systemMessages.Count >= 2); // Original system messages plus possibly summary marker
    }

    [Fact]
    public async Task CompactAsync_RecentMessagesProtected()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 300,  // Protect last 3 messages
            MinimumPruneTokens = 100
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Old message 1"),
            new(ChatRole.Assistant, "Old message 2"),
            new(ChatRole.User, "Old message 3"),
            new(ChatRole.Assistant, "Old message 4"),
            new(ChatRole.User, "Recent message 1"),
            new(ChatRole.Assistant, "Recent message 2"),
            new(ChatRole.User, "Most recent"),
        };

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 400, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Recent messages are preserved
        var lastMessage = result.CompactedHistory[^1];
        Assert.Equal("Most recent", lastMessage.Text);
    }

    #endregion

    #region Empty and Edge Cases

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmpty()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var compactor = new TokenBasedHistoryCompactor(tokenCounter);

        // Act
        var result = await compactor.CompactAsync([], targetTokens: 1000, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.CompactedHistory);
        Assert.Equal(0, result.OriginalTokens);
        Assert.Equal(0, result.CompactedTokens);
    }

    [Fact]
    public async Task CompactAsync_NullHistory_ThrowsArgumentNullException()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var compactor = new TokenBasedHistoryCompactor(tokenCounter);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            compactor.CompactAsync(null!, targetTokens: 1000, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompactAsync_OnlySystemMessages_PreservesAll()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var compactor = new TokenBasedHistoryCompactor(tokenCounter);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt 1"),
            new(ChatRole.System, "System prompt 2"),
        };

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 50, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Even if over target, system messages are kept
        Assert.Equal(2, result.CompactedHistory.Count);
    }

    #endregion

    #region Tool Output Protection

    [Fact]
    public async Task CompactAsync_KeepsAProtectedToolCallWithItsResult_AndSummarizesOtherToolGroups()
    {
        // A protected tool's call and result stay whole and together; an unprotected tool's call and result are left
        // out together (here: no summarizer, so a marker), never one without the other.
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100,
            ProtectedToolOutputs = ["ReadFile"]
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read the file"),
            new(ChatRole.Assistant, [new FunctionCallContent("r1", "ReadFile", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("r1", "File contents here...")]),
            new(ChatRole.Assistant, [new FunctionCallContent("l1", "ListDirectory")]),
            new(ChatRole.Tool, [new FunctionResultContent("l1", "a.txt b.txt")]),
            new(ChatRole.Assistant, "The file contains..."),
            new(ChatRole.User, "What did it say?"),
            new(ChatRole.Assistant, "It said..."),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 600, cancellationToken: TestContext.Current.CancellationToken);

        var contents = result.CompactedHistory.SelectMany(m => m.Contents).ToList();
        Assert.Contains(contents, c => c is FunctionCallContent { CallId: "r1" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "r1" });
        Assert.DoesNotContain(contents, c => c is FunctionCallContent { CallId: "l1" });
        Assert.DoesNotContain(contents, c => c is FunctionResultContent { CallId: "l1" });
        Assert.Contains(result.CompactedHistory, m => m.Role == ChatRole.User && m.Text == "Read the file");
        Assert.Contains(result.CompactedHistory, m => m.Text?.Contains("omitted", StringComparison.Ordinal) == true);
    }

    #endregion

    #region Truncation Fallback

    [Fact]
    public async Task CompactAsync_NoSummarizer_UsesTruncation()
    {
        // Arrange: No summarizer provided
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config, summarizer: null);

        var history = CreateHistory(10); // 1000 tokens

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 400, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Should use truncation (fewer messages)
        Assert.True(result.CompactedHistory.Count < history.Count);
        Assert.True(result.MessagesCompacted > 0);
    }

    [Fact]
    public async Task CompactAsync_Truncation_AddsTruncationMarker()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config, summarizer: null);

        var history = CreateHistory(10);

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 400, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Should have a truncation marker
        var hasMarker = result.CompactedHistory.Any(m =>
            m.Role == ChatRole.System &&
            (m.Text?.Contains("omitted", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.True(hasMarker);
    }

    #endregion

    #region CompactionResult

    [Fact]
    public async Task CompactAsync_ReturnsCorrectTokenCounts()
    {
        // Arrange
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new TokenBasedHistoryCompactor(tokenCounter, config);

        var history = CreateHistory(10); // 1000 tokens

        // Act
        var result = await compactor.CompactAsync(history, targetTokens: 500, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1000, result.OriginalTokens);
        Assert.True(result.CompactedTokens <= 600); // Some buffer allowed
    }

    #endregion

    #region Helper Methods

    private static List<ChatMessage> CreateHistory(int count)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            var role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new ChatMessage(role, $"Message {i + 1}"));
        }
        return messages;
    }

    #endregion

    #region Test Token Counter

    private sealed class SimpleTokenCounter : IContextTokenCounter
    {
        private readonly int _tokensPerMessage;

        public SimpleTokenCounter(int tokensPerMessage = 100)
        {
            _tokensPerMessage = tokensPerMessage;
        }

        public string ModelName => "test-model";

        public int MaxContextTokens => 100_000;

        public int CountTokens(ChatMessage message) => _tokensPerMessage;

        public int CountTokens(IEnumerable<ChatMessage> messages)
            => messages.Count() * _tokensPerMessage;

        public int CountTokens(string text)
            => text.Length / 4; // Approximate 4 chars per token
    }

    #endregion
}
