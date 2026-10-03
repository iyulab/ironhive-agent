using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// History compactor that preserves structured anchor information during summarization.
/// Prevents "silent information drift" by maintaining key facts (goals, modified files,
/// errors encountered, key decisions) across multiple compaction rounds.
/// </summary>
/// <remarks>
/// User messages are kept verbatim: the state block and the summary stand in for the rest (assistant replies, tool calls
/// and their results). A summary can change what a user said — an instruction for one turn read back as a standing rule —
/// and a user message cannot be fetched again. When the user messages alone exceed the target, the oldest join the
/// summary first.
/// </remarks>
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
        var (prunableRegion, protectedRegion) = SplitRecent(conversationMessages, _config.ProtectRecentTokens);

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

        // Extract new anchors from messages (rule-based) and merge them with the previous round's
        var merged = MergeAnchors(existingAnchors, ExtractAnchorsFromMessages(prunable));
        var stateBlock = merged.HasContent ? merged.FormatStateBlock(_config.MaxAnchorStateChars) : string.Empty;
        var stateBlockTokens = stateBlock.Length > 0 ? TokenCounter.CountTokens(stateBlock) : 0;

        // Keep user messages verbatim; the oldest join the summary when they alone exceed the target
        var groups = GroupMessages([.. prunable.Where(m => !IsStateBlock(m))]);
        var keep = groups.Select(IsUserMessageGroup).ToArray();
        DemoteOldestUntilWithin(groups, keep, Math.Max(0, targetTokens - stateBlockTokens - SummaryFloorTokens), _ => true);
        var restTargetTokens = Math.Max(0, targetTokens - stateBlockTokens - KeptTokens(groups, keep));

        // Try LLM-based anchored summarization of the rest
        var rest = groups.Where((_, g) => !keep[g]).SelectMany(x => x).ToList();
        if (Summarizer is not null && rest.Count > 0)
        {
            try
            {
                var summary = await SummarizeAsync(rest, Math.Max(SummaryFloorTokens, restTargetTokens), cancellationToken);
                return Assemble(stateBlock, summary, groups, keep);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fallback on LLM failure — intentional
            }
        }

        // Fallback: anchor state block + the newest of the rest that fits, user messages in place
        return BuildAnchorTruncation(stateBlock, groups, keep, restTargetTokens);
    }

    private static bool IsStateBlock(ChatMessage message) =>
        message.Role == ChatRole.System &&
        message.Text?.Contains(StateBlockStart, StringComparison.Ordinal) == true;

    private static List<ChatMessage> Assemble(
        string stateBlock, string summary, List<List<ChatMessage>> groups, bool[] keep)
    {
        var result = new List<ChatMessage>();
        if (stateBlock.Length > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, stateBlock));
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Add(new ChatMessage(ChatRole.System, $"[Previous conversation summary]: {summary}"));
        }

        result.AddRange(groups.Where((_, g) => keep[g]).SelectMany(x => x));
        return result;
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

    private async Task<string> SummarizeAsync(
        List<ChatMessage> messages,
        int summaryTargetTokens,
        CancellationToken cancellationToken)
    {
        var conversationText = new StringBuilder();
        foreach (var message in messages)
        {
            conversationText.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"[{message.Role}]: {message.Text}");
        }

        var prompt = $"""
            Summarize the following conversation concisely.
            The user's messages are kept separately, word for word; do not restate their instructions.
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
        return response.Text ?? string.Empty;
    }

    private List<ChatMessage> BuildAnchorTruncation(
        string stateBlock,
        List<List<ChatMessage>> groups,
        bool[] keep,
        int restTargetTokens)
    {
        // Add the newest groups of the rest while they fit, then keep everything in its original order
        var include = (bool[])keep.Clone();
        var used = 0;
        for (var g = groups.Count - 1; g >= 0; g--)
        {
            if (include[g])
            {
                continue;
            }

            var tokens = TokenCounter.CountTokens(groups[g]);
            if (used + tokens > restTargetTokens)
            {
                break;
            }

            include[g] = true;
            used += tokens;
        }

        var result = new List<ChatMessage>();
        if (stateBlock.Length > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, stateBlock));
        }

        var omitted = groups.Where((_, g) => !include[g]).Sum(group => group.Count);
        if (omitted > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, $"[{omitted} earlier messages omitted]"));
        }

        result.AddRange(groups.Where((_, g) => include[g]).SelectMany(x => x));
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
        => Format(SessionGoal, CompletedSteps, FilesModified.Order(), FailedApproaches, KeyDecisions, ErrorsEncountered);

    /// <summary>
    /// Formats the anchors within <paramref name="maxChars"/> characters, leaving out the oldest entries first: completed
    /// steps, then errors, modified files and key decisions, and failed approaches last. Zero or less means no limit.
    /// </summary>
    public string FormatStateBlock(int maxChars)
    {
        var block = FormatStateBlock();
        if (maxChars <= 0 || block.Length <= maxChars)
        {
            return block;
        }

        List<string> completed = [.. CompletedSteps];
        List<string> errors = [.. ErrorsEncountered];
        List<string> files = [.. FilesModified.Order()];
        List<string> decisions = [.. KeyDecisions];
        List<string> failed = [.. FailedApproaches];
        var goal = SessionGoal;
        List<string>[] dropOrder = [completed, errors, files, decisions, failed];
        while (block.Length > maxChars && dropOrder.FirstOrDefault(entries => entries.Count > 0) is { } oldest)
        {
            oldest.RemoveAt(0);
            block = Format(goal, completed, files, failed, decisions, errors);
        }

        if (block.Length > maxChars && goal is not null)
        {
            var keep = goal.Length - (block.Length - maxChars) - 3;
            goal = keep > 0 ? goal[..keep] + "..." : null;
            block = Format(goal, completed, files, failed, decisions, errors);
        }

        return block;
    }

    private static string Format(
        string? sessionGoal,
        IEnumerable<string> completedSteps,
        IEnumerable<string> filesModified,
        IEnumerable<string> failedApproaches,
        IEnumerable<string> keyDecisions,
        IEnumerable<string> errorsEncountered)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AnchoredHistoryCompactor.StateBlockStart);

        if (sessionGoal is not null)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Goal: {sessionGoal}");
        }

        AppendSection(sb, "Completed:", completedSteps);
        AppendSection(sb, "Files modified:", filesModified);
        AppendSection(sb, "Failed approaches:", failedApproaches);
        AppendSection(sb, "Key decisions:", keyDecisions);
        AppendSection(sb, "Errors:", errorsEncountered);

        sb.Append(AnchoredHistoryCompactor.StateBlockEnd);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IEnumerable<string> entries)
    {
        var first = true;
        foreach (var entry in entries)
        {
            if (first)
            {
                sb.AppendLine(heading);
                first = false;
            }

            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {entry}");
        }
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
