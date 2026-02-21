using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// History compactor that preserves structured anchor information during summarization.
/// Prevents "silent information drift" by maintaining key facts (goals, modified files,
/// errors encountered, key decisions) across multiple compaction rounds.
/// </summary>
public partial class AnchoredHistoryCompactor : HistoryCompactorBase
{
    /// <summary>Marker for the start of a conversation state block.</summary>
    public const string StateBlockStart = "[CONVERSATION STATE]";

    /// <summary>Marker for the end of a conversation state block.</summary>
    public const string StateBlockEnd = "[END STATE]";

    private readonly CompactionConfig _config;

    public AnchoredHistoryCompactor(
        IContextTokenCounter tokenCounter,
        CompactionConfig? config = null,
        IChatClient? summarizer = null)
        : base(tokenCounter, summarizer)
    {
        _config = config ?? new CompactionConfig();
    }

    /// <inheritdoc />
    public override async Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        int targetTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);

        var originalTokens = TokenCounter.CountTokens(history);
        if (originalTokens <= targetTokens)
        {
            return CreateNoOpResult(history, originalTokens);
        }

        var (systemMessages, conversationMessages) = SplitSystemMessages(history);
        var protectedRegion = GetProtectedRecentMessages(conversationMessages);
        var prunableRegion = GetPrunableMessages(conversationMessages, protectedRegion.Count);

        var systemTokens = TokenCounter.CountTokens(systemMessages);
        var protectedTokens = TokenCounter.CountTokens(protectedRegion);
        var prunableTargetTokens = Math.Max(0, targetTokens - systemTokens - protectedTokens);

        // Look for existing anchors in ALL messages (including system messages
        // which may contain a state block from a previous compaction)
        var existingAnchors = ParseExistingAnchors(history);

        // Remove old state block from system messages to avoid duplication.
        // The info is preserved in existingAnchors and will be merged into the new state block.
        if (existingAnchors is not null)
        {
            systemMessages.RemoveAll(m =>
                m.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true);
            // Recalculate after removal
            systemTokens = TokenCounter.CountTokens(systemMessages);
            prunableTargetTokens = Math.Max(0, targetTokens - systemTokens - protectedTokens);
        }

        var compactedPrunable = await CompactWithAnchorsAsync(
            prunableRegion, existingAnchors, prunableTargetTokens, cancellationToken);

        var compactedHistory = new List<ChatMessage>();
        compactedHistory.AddRange(systemMessages);
        compactedHistory.AddRange(compactedPrunable);
        compactedHistory.AddRange(protectedRegion);

        return CreateResult(history, compactedHistory, originalTokens,
            prunableRegion.Count - compactedPrunable.Count);
    }

    #region History Splitting

    private static (List<ChatMessage> system, List<ChatMessage> conversation) SplitSystemMessages(
        IReadOnlyList<ChatMessage> history)
    {
        var system = new List<ChatMessage>();
        var conversation = new List<ChatMessage>();

        foreach (var message in history)
        {
            if (message.Role == ChatRole.System)
            {
                system.Add(message);
            }
            else
            {
                conversation.Add(message);
            }
        }

        return (system, conversation);
    }

    private List<ChatMessage> GetProtectedRecentMessages(List<ChatMessage> conversation)
    {
        var protectedTokens = _config.ProtectRecentTokens;
        var result = new List<ChatMessage>();
        var currentTokens = 0;

        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            var messageTokens = TokenCounter.CountTokens(conversation[i]);
            if (currentTokens + messageTokens > protectedTokens)
            {
                break;
            }

            result.Insert(0, conversation[i]);
            currentTokens += messageTokens;
        }

        return result;
    }

    private static List<ChatMessage> GetPrunableMessages(List<ChatMessage> conversation, int protectedCount)
    {
        var prunableCount = conversation.Count - protectedCount;
        return conversation.Take(prunableCount).ToList();
    }

    #endregion

    #region Anchored Compaction

    private async Task<List<ChatMessage>> CompactWithAnchorsAsync(
        List<ChatMessage> prunable,
        ConversationAnchors? existingAnchors,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        if (prunable.Count == 0)
        {
            return [];
        }

        var prunableTokens = TokenCounter.CountTokens(prunable);
        if (prunableTokens <= targetTokens)
        {
            return prunable;
        }

        if (prunableTokens < _config.MinimumPruneTokens)
        {
            return prunable;
        }

        // Extract new anchors from messages (rule-based)
        var newAnchors = ExtractAnchorsFromMessages(prunable);

        // Merge existing + new
        var merged = MergeAnchors(existingAnchors, newAnchors);

        // Try LLM-based anchored summarization
        if (Summarizer is not null)
        {
            try
            {
                return await SummarizeWithAnchorsAsync(
                    prunable, merged, targetTokens, cancellationToken);
            }
            catch
            {
                // Fallback on LLM failure — intentional
            }
        }

        // Fallback: anchor state block + truncation
        return BuildAnchorTruncation(prunable, merged, targetTokens);
    }

    #endregion

    #region Anchor Extraction

    /// <summary>
    /// Looks for an existing state block message from a previous compaction.
    /// </summary>
    private static ConversationAnchors? ParseExistingAnchors(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System &&
                message.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true)
            {
                return ConversationAnchors.Parse(message.Text);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts anchor information from conversation messages using rule-based patterns.
    /// </summary>
    internal static ConversationAnchors ExtractAnchorsFromMessages(IReadOnlyList<ChatMessage> messages)
    {
        var anchors = new ConversationAnchors();
        var goalFound = false;

        foreach (var message in messages)
        {
            // Skip existing state block messages
            if (message.Role == ChatRole.System &&
                message.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true)
            {
                continue;
            }

            // Extract goal from first user message
            if (message.Role == ChatRole.User && !goalFound)
            {
                var text = message.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    anchors.SessionGoal = text.Length > 200 ? text[..200] + "..." : text;
                    goalFound = true;
                }
            }

            // Extract file paths from tool calls in assistant messages
            if (message.Role == ChatRole.Assistant && message.Contents is not null)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        ExtractFromToolCall(functionCall, anchors);
                    }
                }
            }

            // Extract error codes from any message text
            ExtractErrorCodes(message.Text, anchors);
        }

        return anchors;
    }

    private static void ExtractFromToolCall(FunctionCallContent functionCall, ConversationAnchors anchors)
    {
        if (string.IsNullOrEmpty(functionCall.Name))
        {
            return;
        }

        // Look for file-modifying tool calls to track modified files
        if (IsFileModifyingTool(functionCall.Name) && functionCall.Arguments is not null)
        {
            foreach (var kvp in functionCall.Arguments)
            {
                if (IsPathArgument(kvp.Key))
                {
                    var path = kvp.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        anchors.FilesModified.Add(path);
                    }
                }
            }
        }
    }

    private static bool IsFileModifyingTool(string toolName)
    {
        return toolName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathArgument(string key)
    {
        return key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("filePath", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?:CS|CA|IDE|SA)\d{4,5}")]
    private static partial Regex ErrorCodePattern();

    private static void ExtractErrorCodes(string? text, ConversationAnchors anchors)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in ErrorCodePattern().Matches(text))
        {
            anchors.ErrorsEncountered.Add(match.Value);
        }
    }

    #endregion

    #region Anchor Merging

    /// <summary>
    /// Merges two sets of anchors, with existing anchors taking priority for the goal.
    /// Deduplicates list items.
    /// </summary>
    internal static ConversationAnchors MergeAnchors(
        ConversationAnchors? existing,
        ConversationAnchors newAnchors)
    {
        if (existing is null)
        {
            return newAnchors;
        }

        var merged = new ConversationAnchors
        {
            SessionGoal = existing.SessionGoal ?? newAnchors.SessionGoal
        };

        MergeList(merged.CompletedSteps, existing.CompletedSteps, newAnchors.CompletedSteps);
        MergeSet(merged.FilesModified, existing.FilesModified, newAnchors.FilesModified);
        MergeList(merged.FailedApproaches, existing.FailedApproaches, newAnchors.FailedApproaches);
        MergeList(merged.KeyDecisions, existing.KeyDecisions, newAnchors.KeyDecisions);
        MergeList(merged.ErrorsEncountered, existing.ErrorsEncountered, newAnchors.ErrorsEncountered);

        return merged;
    }

    private static void MergeList(List<string> target, List<string> existing, List<string> newItems)
    {
        target.AddRange(existing);
        foreach (var item in newItems)
        {
            if (!target.Contains(item, StringComparer.Ordinal))
            {
                target.Add(item);
            }
        }
    }

    private static void MergeSet(HashSet<string> target, HashSet<string> existing, HashSet<string> newItems)
    {
        foreach (var item in existing)
        {
            target.Add(item);
        }

        foreach (var item in newItems)
        {
            target.Add(item);
        }
    }

    #endregion

    #region Summarization

    private async Task<List<ChatMessage>> SummarizeWithAnchorsAsync(
        List<ChatMessage> messages,
        ConversationAnchors anchors,
        int targetTokens,
        CancellationToken cancellationToken)
    {
        var stateBlock = anchors.HasContent ? anchors.FormatStateBlock() : string.Empty;
        var stateBlockTokens = stateBlock.Length > 0 ? TokenCounter.CountTokens(stateBlock) : 0;
        var summaryTargetTokens = Math.Max(100, targetTokens - stateBlockTokens);

        // Build conversation text for summarization (skip existing state blocks)
        var conversationText = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System &&
                message.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true)
            {
                continue;
            }

            conversationText.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"[{message.Role}]: {message.Text}");
        }

        var prompt = $"""
            Summarize the following conversation concisely.
            Preserve ALL of these critical details:
            - Goals and objectives discussed
            - Key decisions made and their rationale
            - Failed approaches and why they failed (IMPORTANT: prevents repeating mistakes)
            - Specific file paths, error codes, and technical details
            - Current progress and next steps

            Keep the summary under {summaryTargetTokens / 4} tokens.
            Focus on actionable information needed to continue the work.

            Conversation:
            {conversationText}

            Summary:
            """;

        var response = await Summarizer!.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        var summary = response.Text ?? string.Empty;

        var result = new List<ChatMessage>();

        if (stateBlock.Length > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, stateBlock));
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Add(new ChatMessage(ChatRole.System, $"[Previous conversation summary]: {summary}"));
        }

        return result;
    }

    private List<ChatMessage> BuildAnchorTruncation(
        List<ChatMessage> prunable,
        ConversationAnchors anchors,
        int targetTokens)
    {
        var result = new List<ChatMessage>();

        // Add anchor state block first
        if (anchors.HasContent)
        {
            result.Add(new ChatMessage(ChatRole.System, anchors.FormatStateBlock()));
        }

        // Calculate remaining budget for truncated messages
        var usedTokens = TokenCounter.CountTokens(result);
        var remainingTokens = Math.Max(0, targetTokens - usedTokens);

        // Filter out old state block messages before truncation
        var filteredPrunable = prunable
            .Where(m => !(m.Role == ChatRole.System &&
                          m.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true))
            .ToList();

        // Add truncated recent messages from the prunable region
        var truncated = TruncateFromBeginning(filteredPrunable, remainingTokens);
        result.AddRange(truncated);

        return result;
    }

    #endregion
}

/// <summary>
/// Structured anchor information extracted from conversation history.
/// Preserves key facts across compaction rounds to prevent silent information drift.
/// </summary>
public sealed class ConversationAnchors
{
    /// <summary>The session's primary goal, extracted from the first user message.</summary>
    public string? SessionGoal { get; set; }

    /// <summary>Completed steps or actions.</summary>
    public List<string> CompletedSteps { get; } = [];

    /// <summary>Files that were modified during the conversation.</summary>
    public HashSet<string> FilesModified { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Approaches that were tried and failed.</summary>
    public List<string> FailedApproaches { get; } = [];

    /// <summary>Key decisions made during the conversation.</summary>
    public List<string> KeyDecisions { get; } = [];

    /// <summary>Error codes encountered (CS/CA/IDE/SA codes).</summary>
    public List<string> ErrorsEncountered { get; } = [];

    /// <summary>Whether any anchor information has been captured.</summary>
    public bool HasContent =>
        SessionGoal is not null ||
        CompletedSteps.Count > 0 ||
        FilesModified.Count > 0 ||
        FailedApproaches.Count > 0 ||
        KeyDecisions.Count > 0 ||
        ErrorsEncountered.Count > 0;

    /// <summary>
    /// Formats the anchors as a structured state block for insertion into conversation history.
    /// </summary>
    public string FormatStateBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine(AnchoredHistoryCompactor.StateBlockStart);

        if (SessionGoal is not null)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Goal: {SessionGoal}");
        }

        if (CompletedSteps.Count > 0)
        {
            sb.AppendLine("Completed:");
            foreach (var step in CompletedSteps)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {step}");
            }
        }

        if (FilesModified.Count > 0)
        {
            sb.AppendLine("Files modified:");
            foreach (var file in FilesModified.Order())
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {file}");
            }
        }

        if (FailedApproaches.Count > 0)
        {
            sb.AppendLine("Failed approaches:");
            foreach (var approach in FailedApproaches)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {approach}");
            }
        }

        if (KeyDecisions.Count > 0)
        {
            sb.AppendLine("Key decisions:");
            foreach (var decision in KeyDecisions)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {decision}");
            }
        }

        if (ErrorsEncountered.Count > 0)
        {
            sb.AppendLine("Errors:");
            foreach (var error in ErrorsEncountered)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {error}");
            }
        }

        sb.Append(AnchoredHistoryCompactor.StateBlockEnd);
        return sb.ToString();
    }

    /// <summary>
    /// Parses a state block string back into ConversationAnchors.
    /// </summary>
    public static ConversationAnchors Parse(string stateBlock)
    {
        var anchors = new ConversationAnchors();

        if (string.IsNullOrWhiteSpace(stateBlock))
        {
            return anchors;
        }

        var lines = stateBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? currentSection = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line is AnchoredHistoryCompactor.StateBlockStart or AnchoredHistoryCompactor.StateBlockEnd)
            {
                continue;
            }

            if (line.StartsWith("Goal:", StringComparison.Ordinal))
            {
                anchors.SessionGoal = line["Goal:".Length..].Trim();
                currentSection = null;
            }
            else if (line is "Completed:")
            {
                currentSection = "completed";
            }
            else if (line is "Files modified:")
            {
                currentSection = "files";
            }
            else if (line is "Failed approaches:")
            {
                currentSection = "failed";
            }
            else if (line is "Key decisions:")
            {
                currentSection = "decisions";
            }
            else if (line is "Errors:")
            {
                currentSection = "errors";
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = line[2..].Trim();
                switch (currentSection)
                {
                    case "completed":
                        anchors.CompletedSteps.Add(item);
                        break;
                    case "files":
                        anchors.FilesModified.Add(item);
                        break;
                    case "failed":
                        anchors.FailedApproaches.Add(item);
                        break;
                    case "decisions":
                        anchors.KeyDecisions.Add(item);
                        break;
                    case "errors":
                        anchors.ErrorsEncountered.Add(item);
                        break;
                }
            }
        }

        return anchors;
    }
}
