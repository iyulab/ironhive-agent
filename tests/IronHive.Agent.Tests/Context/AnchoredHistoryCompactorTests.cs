using IronHive.Agent.Context;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-08: Tests for AnchoredHistoryCompactor — anchored iterative summarization.
/// </summary>
public class AnchoredHistoryCompactorTests
{
    #region Basic Compaction Behavior

    [Fact]
    public async Task CompactAsync_WithinTarget_ReturnsUnchanged()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig { ProtectRecentTokens = 40_000 };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = CreateHistory(5); // 500 tokens

        var result = await compactor.CompactAsync(history, targetTokens: 1000);

        Assert.Equal(history.Count, result.CompactedHistory.Count);
        Assert.Equal(0, result.MessagesCompacted);
    }

    [Fact]
    public async Task CompactAsync_NullHistory_ThrowsArgumentNullException()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var compactor = new AnchoredHistoryCompactor(tokenCounter);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            compactor.CompactAsync(null!, targetTokens: 1000));
    }

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsEmpty()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var compactor = new AnchoredHistoryCompactor(tokenCounter);

        var result = await compactor.CompactAsync([], targetTokens: 1000);

        Assert.Empty(result.CompactedHistory);
        Assert.Equal(0, result.OriginalTokens);
    }

    [Fact]
    public async Task CompactAsync_SystemMessagesPreserved()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt"),
            new(ChatRole.User, "Message 1"),
            new(ChatRole.Assistant, "Response 1"),
            new(ChatRole.User, "Message 2"),
            new(ChatRole.Assistant, "Response 2"),
            new(ChatRole.User, "Message 3"),
            new(ChatRole.Assistant, "Response 3"),
            new(ChatRole.User, "Recent"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // System messages should be at the beginning
        Assert.Equal(ChatRole.System, result.CompactedHistory[0].Role);
        Assert.Equal("System prompt", result.CompactedHistory[0].Text);
    }

    [Fact]
    public async Task CompactAsync_RecentMessagesProtected()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 300,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Old message 1"),
            new(ChatRole.Assistant, "Old response 1"),
            new(ChatRole.User, "Old message 2"),
            new(ChatRole.Assistant, "Old response 2"),
            new(ChatRole.User, "Recent message"),
            new(ChatRole.Assistant, "Recent response"),
            new(ChatRole.User, "Most recent"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // Most recent message should be preserved
        Assert.Equal("Most recent", result.CompactedHistory[^1].Text);
    }

    #endregion

    #region Anchor Extraction — Goal

    [Fact]
    public async Task CompactAsync_ExtractsGoalFromFirstUserMessage()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Refactor the authentication module"),
            new(ChatRole.Assistant, "I'll start by reading the existing code."),
            new(ChatRole.User, "Sounds good"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "What about tests?"),
            new(ChatRole.Assistant, "Writing tests now"),
            new(ChatRole.User, "Most recent message"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // Should contain a state block with the goal
        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Role == ChatRole.System &&
            m.Text?.Contains(AnchoredHistoryCompactor.StateBlockStart, StringComparison.Ordinal) == true);

        Assert.NotNull(stateMessage);
        Assert.Contains("Refactor the authentication module", stateMessage!.Text!);
    }

    [Fact]
    public async Task CompactAsync_TruncatesLongGoal()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var longGoal = new string('x', 300);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, longGoal),
            new(ChatRole.Assistant, "Ok"),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "More"),
            new(ChatRole.Assistant, "Finished"),
            new(ChatRole.User, "Recent"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains(AnchoredHistoryCompactor.StateBlockStart, StringComparison.Ordinal) == true);

        Assert.NotNull(stateMessage);
        // Goal should be truncated with "..."
        Assert.Contains("...", stateMessage!.Text!);
    }

    #endregion

    #region Anchor Extraction — Files Modified

    [Fact]
    public async Task CompactAsync_ExtractsFilesFromToolCalls()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        // Create assistant message with tool call
        var toolCallMessage = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call1", "write_file",
                new Dictionary<string, object?> { ["path"] = "src/Foo.cs", ["content"] = "code" }),
        ]);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Write a file"),
            toolCallMessage,
            new(ChatRole.User, "Now edit another"),
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call2", "edit_file",
                    new Dictionary<string, object?> { ["file_path"] = "src/Bar.cs" }),
            ]),
            new(ChatRole.User, "Great"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "Recent message"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains("Files modified:", StringComparison.Ordinal) == true);

        Assert.NotNull(stateMessage);
        Assert.Contains("src/Foo.cs", stateMessage!.Text!);
        Assert.Contains("src/Bar.cs", stateMessage.Text!);
    }

    [Fact]
    public async Task CompactAsync_IgnoresNonFileModifyingTools()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var toolCallMessage = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call1", "read_file",
                new Dictionary<string, object?> { ["path"] = "src/ReadOnly.cs" }),
        ]);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read a file"),
            toolCallMessage,
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Ok"),
            new(ChatRole.User, "More"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "Recent"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains("Files modified:", StringComparison.Ordinal) == true);

        // read_file is not a modifying tool, so no "Files modified" section
        Assert.Null(stateMessage);
    }

    #endregion

    #region Anchor Extraction — Error Codes

    [Fact]
    public async Task CompactAsync_ExtractsErrorCodes()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Fix the build"),
            new(ChatRole.Assistant, "Found error CS8600 in FooTests.cs"),
            new(ChatRole.User, "Also check CA1859"),
            new(ChatRole.Assistant, "Fixed both issues"),
            new(ChatRole.User, "And IDE0011"),
            new(ChatRole.Assistant, "All done"),
            new(ChatRole.User, "Recent message"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains("Errors:", StringComparison.Ordinal) == true);

        Assert.NotNull(stateMessage);
        Assert.Contains("CS8600", stateMessage!.Text!);
        Assert.Contains("CA1859", stateMessage.Text!);
        Assert.Contains("IDE0011", stateMessage.Text!);
    }

    #endregion

    #region Anchor State Persistence

    [Fact]
    public async Task CompactAsync_PreservesExistingAnchors()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        // Simulate a previous compaction that left a state block
        var existingState = $"""
            {AnchoredHistoryCompactor.StateBlockStart}
            Goal: Original goal from first session
            Files modified:
              - src/OldFile.cs
            Errors:
              - CS1234
            {AnchoredHistoryCompactor.StateBlockEnd}
            """;

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, existingState),
            new(ChatRole.User, "New task"),
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("call1", "write_file",
                    new Dictionary<string, object?> { ["path"] = "src/NewFile.cs" }),
            ]),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Found error CA5678"),
            new(ChatRole.User, "Fix it"),
            new(ChatRole.Assistant, "Fixed"),
            new(ChatRole.User, "Recent message"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 500);

        var stateMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains(AnchoredHistoryCompactor.StateBlockStart, StringComparison.Ordinal) == true);

        Assert.NotNull(stateMessage);
        // Should preserve original goal
        Assert.Contains("Original goal from first session", stateMessage!.Text!);
        // Should include both old and new files
        Assert.Contains("src/OldFile.cs", stateMessage.Text!);
        Assert.Contains("src/NewFile.cs", stateMessage.Text!);
        // Should include both old and new errors
        Assert.Contains("CS1234", stateMessage.Text!);
        Assert.Contains("CA5678", stateMessage.Text!);
    }

    #endregion

    #region ConversationAnchors Format/Parse Round-Trip

    [Fact]
    public void ConversationAnchors_FormatAndParse_RoundTrip()
    {
        var original = new ConversationAnchors
        {
            SessionGoal = "Refactor auth module"
        };
        original.CompletedSteps.Add("Read existing code");
        original.CompletedSteps.Add("Identify patterns");
        original.FilesModified.Add("src/Auth.cs");
        original.FilesModified.Add("tests/AuthTests.cs");
        original.FailedApproaches.Add("Pattern A caused stack overflow");
        original.KeyDecisions.Add("Use NSubstitute for mocking");
        original.ErrorsEncountered.Add("CS8600");
        original.ErrorsEncountered.Add("CA1859");

        var stateBlock = original.FormatStateBlock();
        var parsed = ConversationAnchors.Parse(stateBlock);

        Assert.Equal("Refactor auth module", parsed.SessionGoal);
        Assert.Equal(2, parsed.CompletedSteps.Count);
        Assert.Contains("Read existing code", parsed.CompletedSteps);
        Assert.Contains("Identify patterns", parsed.CompletedSteps);
        Assert.Equal(2, parsed.FilesModified.Count);
        Assert.Contains("src/Auth.cs", parsed.FilesModified);
        Assert.Contains("tests/AuthTests.cs", parsed.FilesModified);
        Assert.Single(parsed.FailedApproaches);
        Assert.Contains("Pattern A caused stack overflow", parsed.FailedApproaches);
        Assert.Single(parsed.KeyDecisions);
        Assert.Contains("Use NSubstitute for mocking", parsed.KeyDecisions);
        Assert.Equal(2, parsed.ErrorsEncountered.Count);
        Assert.Contains("CS8600", parsed.ErrorsEncountered);
        Assert.Contains("CA1859", parsed.ErrorsEncountered);
    }

    [Fact]
    public void ConversationAnchors_Parse_EmptyString_ReturnsEmptyAnchors()
    {
        var anchors = ConversationAnchors.Parse("");

        Assert.Null(anchors.SessionGoal);
        Assert.Empty(anchors.CompletedSteps);
        Assert.Empty(anchors.FilesModified);
        Assert.False(anchors.HasContent);
    }

    [Fact]
    public void ConversationAnchors_HasContent_FalseWhenEmpty()
    {
        var anchors = new ConversationAnchors();
        Assert.False(anchors.HasContent);
    }

    [Fact]
    public void ConversationAnchors_HasContent_TrueWithGoalOnly()
    {
        var anchors = new ConversationAnchors { SessionGoal = "Test" };
        Assert.True(anchors.HasContent);
    }

    [Fact]
    public void ConversationAnchors_HasContent_TrueWithFilesOnly()
    {
        var anchors = new ConversationAnchors();
        anchors.FilesModified.Add("src/Foo.cs");
        Assert.True(anchors.HasContent);
    }

    [Fact]
    public void ConversationAnchors_FormatStateBlock_ContainsMarkers()
    {
        var anchors = new ConversationAnchors { SessionGoal = "Test goal" };
        var block = anchors.FormatStateBlock();

        Assert.Contains(AnchoredHistoryCompactor.StateBlockStart, block);
        Assert.Contains(AnchoredHistoryCompactor.StateBlockEnd, block);
    }

    #endregion

    #region LLM Summarization

    [Fact]
    public async Task CompactAsync_WithSummarizer_UsesSummarization()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };

        var summarizer = Substitute.For<IChatClient>();
        summarizer.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "Summary of the conversation.")]));

        var compactor = new AnchoredHistoryCompactor(tokenCounter, config, summarizer);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "First task"),
            new(ChatRole.Assistant, "Working on it"),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "Next"),
            new(ChatRole.Assistant, "Processing"),
            new(ChatRole.User, "Recent"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // Should have summary message
        var summaryMessage = result.CompactedHistory.FirstOrDefault(m =>
            m.Text?.Contains("Previous conversation summary", StringComparison.Ordinal) == true);

        Assert.NotNull(summaryMessage);
        Assert.Contains("Summary of the conversation", summaryMessage!.Text!);
    }

    [Fact]
    public async Task CompactAsync_SummarizerFails_FallsBackToTruncation()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };

        var summarizer = Substitute.For<IChatClient>();
        summarizer.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var compactor = new AnchoredHistoryCompactor(tokenCounter, config, summarizer);

        var history = CreateHistory(10); // 1000 tokens

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // Should still compact (via truncation fallback)
        Assert.True(result.CompactedHistory.Count < history.Count);
    }

    #endregion

    #region Truncation Fallback

    [Fact]
    public async Task CompactAsync_NoSummarizer_UsesAnchorTruncation()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = CreateHistory(10); // 1000 tokens

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        Assert.True(result.CompactedHistory.Count < history.Count);
        Assert.True(result.MessagesCompacted > 0);
    }

    [Fact]
    public async Task CompactAsync_NoSummarizer_IncludesStateBlock()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "My goal is to refactor"),
            new(ChatRole.Assistant, "Starting now"),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Done"),
            new(ChatRole.User, "More work"),
            new(ChatRole.Assistant, "Processing"),
            new(ChatRole.User, "Recent message"),
        };

        var result = await compactor.CompactAsync(history, targetTokens: 400);

        // Should have a state block with the goal
        var hasStateBlock = result.CompactedHistory.Any(m =>
            m.Text?.Contains(AnchoredHistoryCompactor.StateBlockStart, StringComparison.Ordinal) == true);

        Assert.True(hasStateBlock);
    }

    #endregion

    #region CompactionConfig Integration

    [Fact]
    public void CompactionConfig_DefaultAnchoredCompaction_IsFalse()
    {
        var config = new CompactionConfig();
        Assert.False(config.UseAnchoredCompaction);
    }

    [Fact]
    public void CompactionConfig_DefaultMaxAnchorStateChars_Is2000()
    {
        var config = new CompactionConfig();
        Assert.Equal(2000, config.MaxAnchorStateChars);
    }

    #endregion

    #region CompactionResult

    [Fact]
    public async Task CompactAsync_ReturnsCorrectTokenCounts()
    {
        var tokenCounter = new SimpleTokenCounter(tokensPerMessage: 100);
        var config = new CompactionConfig
        {
            ProtectRecentTokens = 200,
            MinimumPruneTokens = 100
        };
        var compactor = new AnchoredHistoryCompactor(tokenCounter, config);

        var history = CreateHistory(10); // 1000 tokens

        var result = await compactor.CompactAsync(history, targetTokens: 500);

        Assert.Equal(1000, result.OriginalTokens);
        Assert.True(result.CompactedTokens <= 600); // Allow buffer
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
            => text.Length / 4;
    }

    #endregion
}
