using System.Globalization;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Result of compacting a single tool result.
/// </summary>
/// <param name="CompactedResult">The compacted result text.</param>
/// <param name="WasTruncated">Whether truncation was applied.</param>
/// <param name="OriginalLength">Original result character count.</param>
/// <param name="CompactedLength">Compacted result character count.</param>
public record ToolResultCompaction(
    string CompactedResult,
    bool WasTruncated,
    int OriginalLength,
    int CompactedLength);

/// <summary>
/// Compacts large tool results by applying head+tail truncation.
/// Unlike ObservationMasker (which replaces old results with tiny placeholders),
/// this compactor preserves the most useful portions of large results regardless of age.
/// </summary>
public class ToolResultCompactor
{
    private readonly int _maxResultChars;
    private readonly int _keepHeadLines;
    private readonly int _keepTailLines;

    /// <summary>
    /// Creates a new tool result compactor.
    /// </summary>
    /// <param name="maxResultChars">Maximum result character count before compaction triggers. Default: 30,000.</param>
    /// <param name="keepHeadLines">Number of lines to keep from the beginning. Default: 50.</param>
    /// <param name="keepTailLines">Number of lines to keep from the end. Default: 20.</param>
    public ToolResultCompactor(int maxResultChars = 30_000, int keepHeadLines = 50, int keepTailLines = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultChars);
        ArgumentOutOfRangeException.ThrowIfNegative(keepHeadLines);
        ArgumentOutOfRangeException.ThrowIfNegative(keepTailLines);

        _maxResultChars = maxResultChars;
        _keepHeadLines = keepHeadLines;
        _keepTailLines = keepTailLines;
    }

    /// <summary>
    /// Compacts a single tool result string.
    /// </summary>
    /// <param name="result">The tool result text.</param>
    /// <returns>Compaction result with metadata.</returns>
    public ToolResultCompaction Compact(string result)
    {
        if (result.Length <= _maxResultChars)
        {
            return new ToolResultCompaction(result, false, result.Length, result.Length);
        }

        // Try line-based truncation (head + tail)
        var lines = result.Split('\n');
        var totalKeep = _keepHeadLines + _keepTailLines;

        if (lines.Length > totalKeep && totalKeep > 0)
        {
            var head = lines.AsSpan(0, _keepHeadLines);
            var tail = lines.AsSpan(lines.Length - _keepTailLines);
            var omitted = lines.Length - totalKeep;

            var compacted = string.Concat(
                string.Join('\n', head.ToArray()),
                string.Create(CultureInfo.InvariantCulture,
                    $"\n\n[... {omitted:N0} lines omitted ({result.Length:N0} chars total) ...]\n\n"),
                string.Join('\n', tail.ToArray()));

            return new ToolResultCompaction(compacted, true, result.Length, compacted.Length);
        }

        // Fallback: character-based truncation
        var truncated = string.Concat(
            result.AsSpan(0, _maxResultChars),
            string.Create(CultureInfo.InvariantCulture,
                $"\n[... truncated ({result.Length:N0} chars total) ...]"));

        return new ToolResultCompaction(truncated, true, result.Length, truncated.Length);
    }

    /// <summary>
    /// Compacts large tool results in the conversation history.
    /// Processes ALL tool messages (including recent ones), only compacting results
    /// that exceed the character threshold.
    /// </summary>
    /// <param name="history">The conversation history.</param>
    /// <returns>History with large tool results compacted, or the original if nothing changed.</returns>
    public IReadOnlyList<ChatMessage> CompactToolResults(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count == 0)
        {
            return history;
        }

        var result = new List<ChatMessage>(history.Count);
        var anyCompacted = false;

        foreach (var message in history)
        {
            if (message.Role != ChatRole.Tool || message.Contents is null || message.Contents.Count == 0)
            {
                result.Add(message);
                continue;
            }

            var compacted = CompactToolMessage(message);
            result.Add(compacted);
            if (!ReferenceEquals(compacted, message))
            {
                anyCompacted = true;
            }
        }

        return anyCompacted ? result : history;
    }

    private ChatMessage CompactToolMessage(ChatMessage toolMessage)
    {
        var needsCompaction = false;

        foreach (var content in toolMessage.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var resultText = frc.Result?.ToString() ?? string.Empty;
                if (resultText.Length > _maxResultChars)
                {
                    needsCompaction = true;
                    break;
                }
            }
        }

        if (!needsCompaction)
        {
            return toolMessage;
        }

        var compactedContents = new List<AIContent>(toolMessage.Contents.Count);

        foreach (var content in toolMessage.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var resultText = frc.Result?.ToString() ?? string.Empty;
                if (resultText.Length > _maxResultChars)
                {
                    var compaction = Compact(resultText);
                    compactedContents.Add(new FunctionResultContent(frc.CallId, compaction.CompactedResult));
                }
                else
                {
                    compactedContents.Add(content);
                }
            }
            else
            {
                compactedContents.Add(content);
            }
        }

        return new ChatMessage(ChatRole.Tool, compactedContents);
    }
}
