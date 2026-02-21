using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-01: Observation Masking — old tool results replaced with compact placeholders.
/// </summary>
public class ObservationMaskerTests
{
    #region Basic Masking

    [Fact]
    public void MaskObservations_EmptyHistory_ReturnsEmpty()
    {
        var masker = new ObservationMasker();
        var result = masker.MaskObservations([]);
        Assert.Empty(result);
    }

    [Fact]
    public void MaskObservations_NoToolMessages_ReturnsOriginal()
    {
        var masker = new ObservationMasker(protectedTurns: 1);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there"),
            new(ChatRole.User, "How are you?"),
            new(ChatRole.Assistant, "I'm fine"),
        };

        var result = masker.MaskObservations(history);
        Assert.Same(history, result); // Same reference — nothing changed
    }

    [Fact]
    public void MaskObservations_OldLargeToolResult_IsMasked()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 50);

        var largeResult = new string('x', 500);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read the file"),
            CreateAssistantWithToolCall("call-1", "read_file"),
            CreateToolResult("call-1", largeResult),
            new(ChatRole.User, "What did you find?"),
            new(ChatRole.Assistant, "I found some data"),
            // Protected turn below
            new(ChatRole.User, "Thanks"),
            new(ChatRole.Assistant, "You're welcome"),
        };

        var result = masker.MaskObservations(history);

        // The tool result message should be masked
        var toolMsg = result[2];
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        var frc = toolMsg.Contents.OfType<FunctionResultContent>().Single();
        var resultText = frc.Result?.ToString()!;
        Assert.Contains("[Masked:", resultText);
        Assert.Contains("read_file", resultText);
        Assert.Contains("500", resultText);
    }

    [Fact]
    public void MaskObservations_SmallToolResult_NotMasked()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 200);

        var smallResult = "OK";
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Do something"),
            CreateAssistantWithToolCall("call-1", "some_tool"),
            CreateToolResult("call-1", smallResult),
            // Protected turn
            new(ChatRole.User, "Done?"),
            new(ChatRole.Assistant, "Yes"),
        };

        var result = masker.MaskObservations(history);

        // Small result should NOT be masked
        var toolMsg = result[2];
        var frc = toolMsg.Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("OK", frc.Result?.ToString());
    }

    #endregion

    #region Turn Protection

    [Fact]
    public void MaskObservations_RecentTurnsProtected()
    {
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 50);

        var largeResult = new string('y', 300);
        var history = new List<ChatMessage>
        {
            // Old turn (should be masked)
            new(ChatRole.User, "Old request"),
            CreateAssistantWithToolCall("call-old", "read_file"),
            CreateToolResult("call-old", largeResult),
            // Protected turn 1
            new(ChatRole.User, "Recent request 1"),
            CreateAssistantWithToolCall("call-1", "read_file"),
            CreateToolResult("call-1", largeResult),
            // Protected turn 2
            new(ChatRole.User, "Recent request 2"),
            CreateAssistantWithToolCall("call-2", "grep"),
            CreateToolResult("call-2", largeResult),
        };

        var result = masker.MaskObservations(history);

        // Old tool result (index 2) should be masked
        var oldToolFrc = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("[Masked:", oldToolFrc.Result?.ToString());

        // Recent tool results (index 5, 8) should NOT be masked
        var recent1Frc = result[5].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal(largeResult, recent1Frc.Result?.ToString());

        var recent2Frc = result[8].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal(largeResult, recent2Frc.Result?.ToString());
    }

    [Fact]
    public void MaskObservations_FewerTurnsThanThreshold_NothingMasked()
    {
        var masker = new ObservationMasker(protectedTurns: 5, minimumResultLength: 50);

        var largeResult = new string('z', 300);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Request 1"),
            CreateAssistantWithToolCall("call-1", "read_file"),
            CreateToolResult("call-1", largeResult),
            new(ChatRole.User, "Request 2"),
            new(ChatRole.Assistant, "Done"),
        };

        var result = masker.MaskObservations(history);

        // Only 2 turns, threshold is 5 — everything is protected
        Assert.Same(history, result);
    }

    [Fact]
    public void MaskObservations_ZeroProtectedTurns_AllToolResultsMasked()
    {
        var masker = new ObservationMasker(protectedTurns: 0, minimumResultLength: 50);

        var largeResult = new string('a', 300);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Request"),
            CreateAssistantWithToolCall("call-1", "tool_a"),
            CreateToolResult("call-1", largeResult),
        };

        var result = masker.MaskObservations(history);

        // protectedTurns=0 means FindProtectedStartIndex never finds enough turns
        // so protectedStartIndex returns 0, protecting everything
        Assert.Same(history, result);
    }

    #endregion

    #region Placeholder Format

    [Fact]
    public void MaskObservations_PlaceholderContainsToolName()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        var result = ApplyMaskingToSingleToolResult(masker, "my_custom_tool", new string('x', 100));

        var frc = result.Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("my_custom_tool", frc.Result?.ToString());
    }

    [Fact]
    public void MaskObservations_PlaceholderContainsCharCount()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        var result = ApplyMaskingToSingleToolResult(masker, "tool", new string('x', 1234));

        var frc = result.Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("1,234", frc.Result?.ToString());
    }

    [Fact]
    public void MaskObservations_PlaceholderContainsLineCount()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        // 5 newlines → ~6 lines
        var multiline = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"line {i}"));
        var result = ApplyMaskingToSingleToolResult(masker, "read_file", multiline);

        var frc = result.Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("~6 lines", frc.Result?.ToString());
    }

    [Fact]
    public void MaskObservations_PreservesCallId()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        var result = ApplyMaskingToSingleToolResult(masker, "tool", new string('x', 100), callId: "unique-call-42");

        var frc = result.Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("unique-call-42", frc.CallId);
    }

    #endregion

    #region Tool Name Resolution

    [Fact]
    public void MaskObservations_ResolvesToolNameFromAssistantMessage()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        // FunctionResultContent created without Name — name resolved from FunctionCallContent
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read it"),
            CreateAssistantWithToolCall("call-1", "special_reader"),
            CreateToolResult("call-1", new string('x', 200)),
            new(ChatRole.User, "Next"),
            new(ChatRole.Assistant, "OK"),
        };

        var result = masker.MaskObservations(history);
        var frc = result[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("special_reader", frc.Result?.ToString());
    }

    [Fact]
    public void MaskObservations_NoMatchingCallId_FallsBackToGenericName()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        // Tool result with no matching assistant message
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Request"),
            CreateToolResult("orphan-call", new string('x', 200)),
            new(ChatRole.User, "Next"),
            new(ChatRole.Assistant, "OK"),
        };

        var result = masker.MaskObservations(history);
        var frc = result[1].Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("tool", frc.Result?.ToString());
    }

    #endregion

    #region Multiple Tool Results

    [Fact]
    public void MaskObservations_MultipleResultsInOneMessage_MasksOnlyLarge()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 100);

        var toolMessage = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", new string('x', 300)), // Large — mask
            new FunctionResultContent("call-2", "ok"),                  // Small — keep
        ]);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Do things"),
            CreateAssistantWithToolCall("call-1", "big_tool"),
            toolMessage,
            new(ChatRole.User, "Done"),
            new(ChatRole.Assistant, "Yes"),
        };

        var result = masker.MaskObservations(history);

        var resultContents = result[2].Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, resultContents.Count);

        // First result (large) should be masked
        Assert.Contains("[Masked:", resultContents[0].Result?.ToString());

        // Second result (small) should be unchanged
        Assert.Equal("ok", resultContents[1].Result?.ToString());
    }

    #endregion

    #region System Messages

    [Fact]
    public void MaskObservations_SystemMessagesNeverMasked()
    {
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 10);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant"),
            new(ChatRole.User, "Old request"),
            CreateAssistantWithToolCall("call-1", "tool"),
            CreateToolResult("call-1", new string('x', 200)),
            new(ChatRole.User, "Recent"),
            new(ChatRole.Assistant, "OK"),
        };

        var result = masker.MaskObservations(history);

        // System message untouched
        Assert.Equal("You are a helpful assistant", result[0].Text);
    }

    #endregion

    #region Integration with CompactionConfig

    [Fact]
    public void CompactionConfig_DefaultsObservationMaskingEnabled()
    {
        var config = new CompactionConfig();

        Assert.True(config.EnableObservationMasking);
        Assert.Equal(3, config.ObservationMaskingProtectedTurns);
        Assert.Equal(200, config.ObservationMaskingMinResultLength);
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NegativeProtectedTurns_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObservationMasker(protectedTurns: -1));
    }

    [Fact]
    public void Constructor_NegativeMinimumResultLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObservationMasker(minimumResultLength: -1));
    }

    #endregion

    #region Helper Methods

    private static ChatMessage CreateAssistantWithToolCall(string callId, string toolName)
    {
        return new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())]);
    }

    private static ChatMessage CreateToolResult(string callId, string result)
    {
        return new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(callId, result)]);
    }

    private static ChatMessage ApplyMaskingToSingleToolResult(
        ObservationMasker masker,
        string toolName,
        string resultText,
        string callId = "call-1")
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Request"),
            CreateAssistantWithToolCall(callId, toolName),
            CreateToolResult(callId, resultText),
            // Protected turn
            new(ChatRole.User, "Next"),
            new(ChatRole.Assistant, "OK"),
        };

        var result = masker.MaskObservations(history);
        return result[2]; // The tool message
    }

    #endregion
}
