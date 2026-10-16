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
/// Unlike the masking stage (IndexThinking's ToolResultMasker, which replaces old results with tiny placeholders),
/// this compactor preserves the most useful portions of large results regardless of age.
/// </summary>
public class ToolResultCompactor
{
    private readonly IndexThinking.Reduction.ToolResultTrimmer _trimmer;

    /// <summary>Results longer than this are shortened.</summary>
    internal int MaxResultChars => _trimmer.MaxChars;

    /// <summary>
    /// Creates a new tool result compactor.
    /// </summary>
    /// <param name="maxResultChars">Maximum result character count before compaction triggers. Default: 30,000.</param>
    /// <param name="keepHeadLines">Number of lines to keep from the beginning. Default: 50.</param>
    /// <param name="keepTailLines">Number of lines to keep from the end. Default: 20.</param>
    public ToolResultCompactor(int maxResultChars = 30_000, int keepHeadLines = 50, int keepTailLines = 20)
    {
        // The rule itself is IndexThinking's (ToolResultTrimmer), shared with its ContextBudgetChatReducer, so a pipeline
        // that reduces with either trims a result the same way.
        _trimmer = new IndexThinking.Reduction.ToolResultTrimmer(maxResultChars, keepHeadLines, keepTailLines);
    }

    /// <summary>
    /// Compacts a single tool result string.
    /// </summary>
    /// <param name="result">The tool result text.</param>
    /// <returns>Compaction result with metadata.</returns>
    public ToolResultCompaction Compact(string result)
    {
        var compacted = _trimmer.Trim(result);
        return new ToolResultCompaction(compacted, !ReferenceEquals(compacted, result), result.Length, compacted.Length);
    }

    /// <summary>
    /// Compacts large tool results in the conversation history.
    /// Processes ALL tool messages (including recent ones), only compacting results
    /// that exceed the character threshold.
    /// </summary>
    /// <param name="history">The conversation history.</param>
    /// <returns>History with large tool results compacted, or the original if nothing changed.</returns>
    public IReadOnlyList<ChatMessage> CompactToolResults(IReadOnlyList<ChatMessage> history) => CompactToolResults(history, archive: null);

    /// <summary>
    /// As <see cref="CompactToolResults(IReadOnlyList{ChatMessage})"/>; with an <paramref name="archive"/>, each shortened
    /// result's full text is kept in it, and the shortened text ends with how to read the whole of it.
    /// </summary>
    internal IReadOnlyList<ChatMessage> CompactToolResults(IReadOnlyList<ChatMessage> history, ToolResultArchive? archive)
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

            var compacted = CompactToolMessage(message, archive);
            result.Add(compacted);
            if (!ReferenceEquals(compacted, message))
            {
                anyCompacted = true;
            }
        }

        return anyCompacted ? result : history;
    }

    private ChatMessage CompactToolMessage(ChatMessage toolMessage, ToolResultArchive? archive)
    {
        var needsCompaction = false;

        foreach (var content in toolMessage.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var resultText = ToolResultText.Of(frc.Result);
                if (resultText.Length > _trimmer.MaxChars)
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
                var resultText = ToolResultText.Of(frc.Result);
                if (resultText.Length > _trimmer.MaxChars)
                {
                    var compaction = Compact(resultText);
                    if (archive is null)
                    {
                        compactedContents.Add(new FunctionResultContent(frc.CallId, compaction.CompactedResult));
                        continue;
                    }

                    // The id goes on the shortened result too, so the masker keeps it under the same id (not a hash of
                    // the shortened text) and never replaces the original with the shortened copy.
                    var id = archive.Retain(frc, resultText);
                    compactedContents.Add(new FunctionResultContent(frc.CallId, string.Concat(compaction.CompactedResult,
                        $"\n[Shortened. Call {archive.ToolName} with id \"{id}\" to read the whole result.]"))
                    {
                        AdditionalProperties = new AdditionalPropertiesDictionary { [ToolResultArchive.IdProperty] = id },
                    });
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
