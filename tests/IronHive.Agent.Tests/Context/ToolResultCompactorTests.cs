using System.Globalization;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-03: Tool Result Compaction — head+tail truncation of large tool results.
/// </summary>
public class ToolResultCompactorTests
{
    #region Constructor Validation

    [Fact]
    public void Constructor_ZeroMaxChars_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultCompactor(maxResultChars: 0));
    }

    [Fact]
    public void Constructor_NegativeHeadLines_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultCompactor(keepHeadLines: -1));
    }

    [Fact]
    public void Constructor_NegativeTailLines_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultCompactor(keepTailLines: -1));
    }

    [Fact]
    public void Constructor_ValidZeroHeadAndTail_DoesNotThrow()
    {
        var compactor = new ToolResultCompactor(keepHeadLines: 0, keepTailLines: 0);
        Assert.NotNull(compactor);
    }

    #endregion

    #region Compact (Single Result)

    [Fact]
    public void Compact_ShortResult_NotTruncated()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 100);
        var result = compactor.Compact("short result");

        Assert.False(result.WasTruncated);
        Assert.Equal("short result", result.CompactedResult);
        Assert.Equal(result.OriginalLength, result.CompactedLength);
    }

    [Fact]
    public void Compact_ExactlyAtLimit_NotTruncated()
    {
        var text = new string('x', 100);
        var compactor = new ToolResultCompactor(maxResultChars: 100);
        var result = compactor.Compact(text);

        Assert.False(result.WasTruncated);
        Assert.Equal(text, result.CompactedResult);
    }

    [Fact]
    public void Compact_LargeResult_LineBased_Truncated()
    {
        // Create 100 lines of text, each 10 chars
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i:D4}!").ToArray();
        var text = string.Join('\n', lines);
        var compactor = new ToolResultCompactor(maxResultChars: 50, keepHeadLines: 3, keepTailLines: 2);

        var result = compactor.Compact(text);

        Assert.True(result.WasTruncated);
        Assert.True(result.CompactedLength < result.OriginalLength);
        Assert.Contains("lines omitted", result.CompactedResult);
        // Should contain head lines
        Assert.StartsWith("line 0001!", result.CompactedResult);
        // Should contain tail lines
        Assert.EndsWith("line 0100!", result.CompactedResult);
    }

    [Fact]
    public void Compact_OmittedCount_Correct()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"line-{i}").ToArray();
        var text = string.Join('\n', lines);
        var compactor = new ToolResultCompactor(maxResultChars: 10, keepHeadLines: 5, keepTailLines: 3);

        var result = compactor.Compact(text);

        // 50 lines - 5 head - 3 tail = 42 omitted
        Assert.Contains("42", result.CompactedResult);
    }

    [Fact]
    public void Compact_FewLines_FallsBackToCharTruncation()
    {
        // A single very long line — can't do line-based truncation
        var text = new string('a', 200);
        var compactor = new ToolResultCompactor(maxResultChars: 50, keepHeadLines: 5, keepTailLines: 3);

        var result = compactor.Compact(text);

        Assert.True(result.WasTruncated);
        Assert.Contains("truncated", result.CompactedResult);
        // First part should be the first 50 chars
        Assert.StartsWith(new string('a', 50), result.CompactedResult);
    }

    [Fact]
    public void Compact_LinesNotEnoughForSplit_FallsBackToCharTruncation()
    {
        // 5 lines, but keepHead=3 + keepTail=3 = 6 > 5 lines
        var lines = Enumerable.Range(1, 5).Select(i => new string('x', 100)).ToArray();
        var text = string.Join('\n', lines);
        var compactor = new ToolResultCompactor(maxResultChars: 10, keepHeadLines: 3, keepTailLines: 3);

        var result = compactor.Compact(text);

        Assert.True(result.WasTruncated);
        Assert.Contains("truncated", result.CompactedResult);
    }

    [Fact]
    public void Compact_Placeholder_UsesInvariantCulture()
    {
        // Verify numbers use comma grouping (InvariantCulture: 1,000) not locale-dependent
        var lines = Enumerable.Range(1, 200).Select(i => $"line-{i}-padding").ToArray();
        var text = string.Join('\n', lines);
        var compactor = new ToolResultCompactor(maxResultChars: 100, keepHeadLines: 5, keepTailLines: 5);

        var result = compactor.Compact(text);

        // 200 - 10 = 190 omitted, should be formatted with comma
        Assert.True(result.WasTruncated);
        // The total chars count should use N0 formatting
        Assert.Matches(@"\d{1,3}(,\d{3})*\s+chars total", result.CompactedResult);
    }

    #endregion

    #region CompactToolResults (History)

    [Fact]
    public void CompactToolResults_EmptyHistory_ReturnsSame()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 100);
        var history = Array.Empty<ChatMessage>();

        var result = compactor.CompactToolResults(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void CompactToolResults_NoToolMessages_ReturnsSame()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 100);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi")
        };

        var result = compactor.CompactToolResults(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void CompactToolResults_SmallToolResults_ReturnsSame()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 1000);
        var history = CreateHistoryWithToolResult("call-1", "short result");

        var result = compactor.CompactToolResults(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void CompactToolResults_LargeToolResult_Compacted()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 50, keepHeadLines: 3, keepTailLines: 2);
        var largeResult = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var history = CreateHistoryWithToolResult("call-1", largeResult);

        var result = compactor.CompactToolResults(history);

        Assert.NotSame(history, result);
        Assert.Equal(history.Count, result.Count);

        // The tool message should be compacted
        var toolMsg = result[2]; // user, assistant, tool
        var frc = toolMsg.Contents.OfType<FunctionResultContent>().Single();
        var resultText = frc.Result?.ToString();
        Assert.Contains("lines omitted", resultText);
    }

    [Fact]
    public void CompactToolResults_MixedResults_OnlyLargeCompacted()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 100, keepHeadLines: 2, keepTailLines: 1);

        var smallResult = "small";
        var largeResult = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line-{i}-content"));

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "do it"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "tool_a", new Dictionary<string, object?> { ["x"] = "1" }),
                new FunctionCallContent("call-2", "tool_b", new Dictionary<string, object?> { ["y"] = "2" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", smallResult),
                new FunctionResultContent("call-2", largeResult)
            ])
        };

        var result = compactor.CompactToolResults(history);

        Assert.NotSame(history, result);
        var toolMsg = result[2];
        var contents = toolMsg.Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, contents.Count);

        // First result should be unchanged
        Assert.Equal(smallResult, contents[0].Result?.ToString());

        // Second result should be compacted
        Assert.Contains("lines omitted", contents[1].Result?.ToString());
    }

    [Fact]
    public void CompactToolResults_PreservesNonToolMessages()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 10, keepHeadLines: 1, keepTailLines: 1);
        var largeResult = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line-{i}"));

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "query"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "tool", new Dictionary<string, object?> { ["a"] = "b" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", largeResult)
            ]),
            new(ChatRole.Assistant, "done")
        };

        var result = compactor.CompactToolResults(history);

        // System, user, assistant messages should be same references
        Assert.Same(history[0], result[0]);
        Assert.Same(history[1], result[1]);
        Assert.Same(history[2], result[2]);
        Assert.Same(history[4], result[4]);
    }

    [Fact]
    public void CompactToolResults_PreservesCallId()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 10, keepHeadLines: 1, keepTailLines: 1);
        var largeResult = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line-{i}"));
        var history = CreateHistoryWithToolResult("my-call-id", largeResult);

        var result = compactor.CompactToolResults(history);

        var toolMsg = result[2];
        var frc = toolMsg.Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("my-call-id", frc.CallId);
    }

    #endregion

    #region CompactionConfig Integration

    [Fact]
    public void CompactionConfig_DefaultEnableToolResultCompaction_IsTrue()
    {
        var config = new CompactionConfig();
        Assert.True(config.EnableToolResultCompaction);
    }

    [Fact]
    public void CompactionConfig_DefaultMaxToolResultChars_Is30000()
    {
        var config = new CompactionConfig();
        Assert.Equal(30_000, config.MaxToolResultChars);
    }

    [Fact]
    public void CompactionConfig_DefaultKeepHeadLines_Is50()
    {
        var config = new CompactionConfig();
        Assert.Equal(50, config.ToolResultKeepHeadLines);
    }

    [Fact]
    public void CompactionConfig_DefaultKeepTailLines_Is20()
    {
        var config = new CompactionConfig();
        Assert.Equal(20, config.ToolResultKeepTailLines);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Compact_ZeroKeepLines_FallsBackToCharTruncation()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line-{i}").ToArray();
        var text = string.Join('\n', lines);
        var compactor = new ToolResultCompactor(maxResultChars: 10, keepHeadLines: 0, keepTailLines: 0);

        var result = compactor.Compact(text);

        Assert.True(result.WasTruncated);
        Assert.Contains("truncated", result.CompactedResult);
    }

    [Fact]
    public void CompactToolResults_NullResultValue_TreatedAsEmpty()
    {
        var compactor = new ToolResultCompactor(maxResultChars: 100);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [
                new FunctionCallContent("call-1", "tool", new Dictionary<string, object?> { ["a"] = "b" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent("call-1", (object?)null)
            ])
        };

        var result = compactor.CompactToolResults(history);

        // Null result is empty, which is below threshold — no compaction
        Assert.Same(history, result);
    }

    #endregion

    #region Helpers

    private static List<ChatMessage> CreateHistoryWithToolResult(string callId, string toolResult)
    {
        return
        [
            new(ChatRole.User, "do something"),
            new(ChatRole.Assistant, [
                new FunctionCallContent(callId, "test_tool", new Dictionary<string, object?> { ["arg"] = "val" })
            ]),
            new(ChatRole.Tool, [
                new FunctionResultContent(callId, toolResult)
            ])
        ];
    }

    #endregion
}
