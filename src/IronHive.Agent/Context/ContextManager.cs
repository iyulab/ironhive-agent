using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Context usage statistics.
/// </summary>
public record ContextUsage
{
    /// <summary>
    /// Current token count in context.
    /// </summary>
    public int CurrentTokens { get; init; }

    /// <summary>
    /// Maximum allowed tokens.
    /// </summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// Usage percentage (0.0 to 1.0).
    /// </summary>
    public float UsagePercentage => MaxTokens > 0 ? (float)CurrentTokens / MaxTokens : 0;

    /// <summary>
    /// Whether compaction is needed.
    /// </summary>
    public bool NeedsCompaction { get; init; }

    /// <summary>
    /// Number of messages in history.
    /// </summary>
    public int MessageCount { get; init; }
}

/// <summary>
/// Warning when system prompt exceeds context budget.
/// </summary>
public record ContextFitWarning
{
    /// <summary>Token count of system messages.</summary>
    public required int SystemPromptTokens { get; init; }
    /// <summary>Maximum context tokens for the model.</summary>
    public required int MaxContextTokens { get; init; }
    /// <summary>Percentage of context consumed by system prompt.</summary>
    public float SystemPromptPercentage => MaxContextTokens > 0
        ? (float)SystemPromptTokens / MaxContextTokens : 0;
    /// <summary>Whether the system prompt alone exceeds 80% of context budget.</summary>
    public bool IsOverBudget => SystemPromptPercentage > 0.80f;
    /// <summary>
    /// Where those tokens come from: the system prompt and each contributed section by name, largest
    /// first — so an over-budget warning says which section to look at.
    /// </summary>
    public IReadOnlyList<ContextFitSection> Sections { get; init; } = [];
}

/// <summary>One system section and what it costs.</summary>
/// <param name="Name">"system prompt", "scratchpad", or an <see cref="ISystemInstructionContributor.Name"/>.</param>
/// <param name="Tokens">Tokens the section takes.</param>
public sealed record ContextFitSection(string Name, int Tokens);

/// <summary>
/// Manages context window for agent conversations.
/// Handles token counting, compaction triggering, goal reminders, and history management.
/// </summary>
public class ContextManager
{
    private readonly IContextTokenCounter _tokenCounter;
    private readonly ICompactionTrigger _compactionTrigger;
    private readonly IHistoryCompactor _historyCompactor;
    private readonly GoalReminder _goalReminder;
    private readonly ToolResultCompactor? _toolResultCompactor;
    private readonly ObservationMasker? _observationMasker;
    private readonly Scratchpad? _scratchpad;
    private readonly List<ISystemInstructionContributor> _instructionContributors = [];

    /// <summary>
    /// Marks a system message this manager composed for one turn. Such a message is not conversation:
    /// it is removed and recomputed every time history is prepared, so it can neither pile up when a
    /// caller stores the prepared history nor go stale.
    /// </summary>
    internal const string InjectedBlockKey = "ironhive.injected_instruction";

    public ContextManager(
        IContextTokenCounter tokenCounter,
        ICompactionTrigger? compactionTrigger = null,
        IHistoryCompactor? historyCompactor = null,
        GoalReminderOptions? goalReminderOptions = null,
        ToolResultCompactor? toolResultCompactor = null,
        ObservationMasker? observationMasker = null,
        Scratchpad? scratchpad = null,
        IEnumerable<ISystemInstructionContributor>? instructionContributors = null)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _compactionTrigger = compactionTrigger ?? new ThresholdCompactionTrigger();
        _historyCompactor = historyCompactor ?? new HistoryCompactor(tokenCounter);
        _goalReminder = new GoalReminder(goalReminderOptions);
        _toolResultCompactor = toolResultCompactor;
        _observationMasker = observationMasker;
        _scratchpad = scratchpad;
        if (instructionContributors is not null)
        {
            _instructionContributors.AddRange(instructionContributors);
        }
    }

    /// <summary>
    /// The contributors whose sections are added after the system prompt, in the order they apply.
    /// The scratchpad, when configured, is composed after them.
    /// </summary>
    public IReadOnlyList<ISystemInstructionContributor> InstructionContributors => _instructionContributors;

    /// <summary>
    /// Adds a contributor. Its section appears from the next prepared turn on.
    /// </summary>
    public void AddInstructionContributor(ISystemInstructionContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _instructionContributors.Add(contributor);
    }

    /// <summary>
    /// Gets the token counter being used.
    /// </summary>
    public IContextTokenCounter TokenCounter => _tokenCounter;

    /// <summary>
    /// Gets the goal reminder component.
    /// </summary>
    public GoalReminder GoalReminder => _goalReminder;

    /// <summary>
    /// Gets the scratchpad component, if configured.
    /// </summary>
    public Scratchpad? Scratchpad => _scratchpad;

    /// <summary>
    /// Gets the maximum context tokens.
    /// </summary>
    public int MaxContextTokens => _tokenCounter.MaxContextTokens;

    /// <summary>
    /// Gets the current context usage.
    /// </summary>
    public ContextUsage GetUsage(IReadOnlyList<ChatMessage> history)
    {
        var currentTokens = _tokenCounter.CountTokens(history);

        return new ContextUsage
        {
            CurrentTokens = currentTokens,
            MaxTokens = _tokenCounter.MaxContextTokens,
            NeedsCompaction = _compactionTrigger.ShouldCompact(currentTokens, _tokenCounter.MaxContextTokens),
            MessageCount = history.Count
        };
    }

    /// <summary>
    /// Checks if compaction should be triggered.
    /// </summary>
    public bool ShouldCompact(IReadOnlyList<ChatMessage> history)
    {
        var currentTokens = _tokenCounter.CountTokens(history);
        return _compactionTrigger.ShouldCompact(currentTokens, _tokenCounter.MaxContextTokens);
    }

    /// <summary>
    /// Compacts the history if needed.
    /// </summary>
    /// <param name="history">The conversation history.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Compacted history if compaction was performed, otherwise original history.</returns>
    public async Task<CompactionResult> CompactIfNeededAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var currentTokens = _tokenCounter.CountTokens(history);

        if (!_compactionTrigger.ShouldCompact(currentTokens, _tokenCounter.MaxContextTokens))
        {
            return new CompactionResult
            {
                CompactedHistory = history,
                OriginalTokens = currentTokens,
                CompactedTokens = currentTokens,
                MessagesCompacted = 0
            };
        }

        // Target: reduce to 70% of max to leave room for future messages
        var targetTokens = (int)(_tokenCounter.MaxContextTokens * 0.70f);

        return await _historyCompactor.CompactAsync(history, targetTokens, cancellationToken);
    }

    /// <summary>
    /// Forces compaction to a specific target.
    /// </summary>
    public Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        int targetTokens,
        CancellationToken cancellationToken = default)
    {
        return _historyCompactor.CompactAsync(history, targetTokens, cancellationToken);
    }

    /// <summary>
    /// Estimates how many more tokens can be added before compaction is triggered.
    /// </summary>
    public int GetRemainingTokens(IReadOnlyList<ChatMessage> history)
    {
        var currentTokens = _tokenCounter.CountTokens(history);
        var thresholdTokens = (int)(_tokenCounter.MaxContextTokens * _compactionTrigger.ThresholdPercentage);
        return Math.Max(0, thresholdTokens - currentTokens);
    }

    /// <summary>
    /// Sets the goal from the first user message in the history.
    /// </summary>
    public void SetGoalFromHistory(IReadOnlyList<ChatMessage> history)
    {
        _goalReminder.SetGoalFromFirstUserMessage(history);
    }

    /// <summary>
    /// Sets the current goal explicitly.
    /// </summary>
    public void SetGoal(string goal)
    {
        _goalReminder.CurrentGoal = goal;
    }

    /// <summary>
    /// Prepares the history for sending to the model.
    /// Applies observation masking, compaction if needed, and injects goal reminder.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> PrepareHistoryAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Step 0: drop the blocks a previous preparation composed. Callers store the prepared history
        // (the agent loops do), and without this every turn would add another copy of each block.
        history = RemoveInjectedBlocks(history);

        // Step 0a: Compact large tool results (cheap, always runs if enabled)
        var compactedResults = _toolResultCompactor?.CompactToolResults(history) ?? history;

        // Step 0b: Mask old observations (cheap, always runs if enabled)
        var maskedHistory = _observationMasker?.MaskObservations(compactedResults) ?? compactedResults;

        // Step 1: Compact if needed
        var compactionResult = await CompactIfNeededAsync(maskedHistory, cancellationToken);
        var preparedHistory = compactionResult.CompactedHistory;

        // Step 2: Inject goal reminder if needed
        preparedHistory = _goalReminder.InjectReminderIfNeeded(preparedHistory);

        // Step 3: Compose the contributed instruction blocks (contributors in order, then the scratchpad).
        // Insert after leading system messages for cross-provider compatibility
        // (OpenAI, Anthropic, Gemini all expect system messages at the start)
        var blocks = ComposeInstructionBlocks();
        if (blocks.Count > 0)
        {
            var result = new List<ChatMessage>(preparedHistory.Count + blocks.Count);
            var insertAt = 0;
            while (insertAt < preparedHistory.Count && preparedHistory[insertAt].Role == ChatRole.System)
            {
                insertAt++;
            }
            result.AddRange(preparedHistory.Take(insertAt));
            result.AddRange(blocks);
            result.AddRange(preparedHistory.Skip(insertAt));
            preparedHistory = result;
        }

        return preparedHistory;
    }

    private List<ChatMessage> ComposeInstructionBlocks()
    {
        var blocks = new List<ChatMessage>();

        foreach (var contributor in _instructionContributors)
        {
            var text = contributor.GetInstructions();
            if (!string.IsNullOrWhiteSpace(text))
            {
                blocks.Add(InjectedBlock(contributor.Name, text));
            }
        }

        if (_scratchpad?.HasContent == true)
        {
            blocks.Add(InjectedBlock("scratchpad", _scratchpad.ToContextBlock()));
        }

        return blocks;
    }

    private static ChatMessage InjectedBlock(string name, string text)
        => new(ChatRole.System, text)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [InjectedBlockKey] = name },
        };

    private static IReadOnlyList<ChatMessage> RemoveInjectedBlocks(IReadOnlyList<ChatMessage> history)
    {
        var any = false;
        foreach (var message in history)
        {
            if (message.AdditionalProperties?.ContainsKey(InjectedBlockKey) == true)
            {
                any = true;
                break;
            }
        }

        return any
            ? [.. history.Where(m => m.AdditionalProperties?.ContainsKey(InjectedBlockKey) != true)]
            : history;
    }

    /// <summary>
    /// Validates whether the system prompt fits within the context budget.
    /// Returns a warning if system messages consume more than 80% of max context tokens.
    /// Returns null if within budget.
    /// </summary>
    public ContextFitWarning? ValidateContextFit(IReadOnlyList<ChatMessage> history)
    {
        // Judge what the model will actually be sent: the stored system messages plus the sections
        // contributed for the coming turn - whether or not the caller passed a prepared history.
        var systemMessages = RemoveInjectedBlocks(history)
            .Where(m => m.Role == ChatRole.System)
            .Concat(ComposeInstructionBlocks())
            .ToList();
        if (systemMessages.Count == 0)
        {
            return null;
        }

        var systemTokens = systemMessages.Sum(m => _tokenCounter.CountTokens(m));
        var maxTokens = _tokenCounter.MaxContextTokens;

        if ((float)systemTokens / maxTokens <= 0.80f)
        {
            return null;
        }

        return new ContextFitWarning
        {
            SystemPromptTokens = systemTokens,
            MaxContextTokens = maxTokens,
            Sections =
            [
                .. systemMessages.Select(m => new ContextFitSection(
                    m.AdditionalProperties?.TryGetValue(InjectedBlockKey, out var name) == true && name is string s
                        ? s
                        : "system prompt",
                    _tokenCounter.CountTokens(m)))
                    .OrderByDescending(section => section.Tokens),
            ],
        };
    }

    /// Clamps token-based compaction parameters to fit within the actual context window.
    /// Without this, defaults (ProtectRecentTokens=40_000) exceed small-context models
    /// (e.g. 32K), making prunableTokens permanently negative and compaction non-functional.
    private static (int protect, int prune) ClampToContext(int protect, int prune, int maxCtx)
    {
        if (maxCtx <= 0)
        {
            return (protect, prune);
        }

        return (Math.Min(protect, maxCtx / 2), Math.Min(prune, maxCtx / 4));
    }

    private static CompactionConfig CloneWithClampedProtect(CompactionConfig source, int protect, int prune)
        => new()
        {
            ProtectRecentTokens = protect,
            MinimumPruneTokens = prune,
            ProtectedToolOutputs = source.ProtectedToolOutputs,
            TargetRatio = source.TargetRatio,
            UseTokenBasedCompaction = source.UseTokenBasedCompaction,
            ThresholdPercentage = source.ThresholdPercentage,
            EnableObservationMasking = source.EnableObservationMasking,
            ObservationMaskingProtectedTurns = source.ObservationMaskingProtectedTurns,
            ObservationMaskingMinResultLength = source.ObservationMaskingMinResultLength,
            ToolSchemaCompression = source.ToolSchemaCompression,
            EnableToolResultCompaction = source.EnableToolResultCompaction,
            MaxToolResultChars = source.MaxToolResultChars,
            ToolResultKeepHeadLines = source.ToolResultKeepHeadLines,
            ToolResultKeepTailLines = source.ToolResultKeepTailLines,
            UseAnchoredCompaction = source.UseAnchoredCompaction,
            MaxAnchorStateChars = source.MaxAnchorStateChars,
            MaxContextTokens = source.MaxContextTokens,
        };

    /// <summary>
    /// Creates a context manager with default settings for a model.
    /// </summary>
    public static ContextManager ForModel(string modelName, IChatClient? summarizer = null)
    {
        var tokenCounter = new ContextTokenCounter(modelName);
        var compactionTrigger = new ThresholdCompactionTrigger(0.92f);
        var historyCompactor = new HistoryCompactor(tokenCounter, summarizer);

        return new ContextManager(tokenCounter, compactionTrigger, historyCompactor);
    }

    /// <summary>
    /// Creates a context manager with the specified compaction configuration.
    /// </summary>
    /// <param name="modelName">Model name for token counting.</param>
    /// <param name="config">Compaction configuration.</param>
    /// <param name="summarizer">Optional chat client for LLM-based summarization.</param>
    public static ContextManager ForModel(
        string modelName,
        CompactionConfig config,
        IChatClient? summarizer = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var tokenCounter = new ContextTokenCounter(modelName, config.MaxContextTokens);

        ICompactionTrigger compactionTrigger;
        IHistoryCompactor historyCompactor;

        if (config.UseAnchoredCompaction)
        {
            var (effectiveProtect, effectivePrune) = ClampToContext(
                config.ProtectRecentTokens, config.MinimumPruneTokens, tokenCounter.MaxContextTokens);
            compactionTrigger = new TokenBasedCompactionTrigger(effectiveProtect, effectivePrune);
            var anchoredConfig = CloneWithClampedProtect(config, effectiveProtect, effectivePrune);
            historyCompactor = new AnchoredHistoryCompactor(tokenCounter, anchoredConfig, summarizer);
        }
        else if (config.UseTokenBasedCompaction)
        {
            var (effectiveProtect, effectivePrune) = ClampToContext(
                config.ProtectRecentTokens, config.MinimumPruneTokens, tokenCounter.MaxContextTokens);
            compactionTrigger = new TokenBasedCompactionTrigger(effectiveProtect, effectivePrune);
            var tokenConfig = CloneWithClampedProtect(config, effectiveProtect, effectivePrune);
            historyCompactor = new TokenBasedHistoryCompactor(tokenCounter, tokenConfig, summarizer);
        }
        else
        {
            compactionTrigger = new ThresholdCompactionTrigger(config.ThresholdPercentage);
            historyCompactor = new HistoryCompactor(tokenCounter, summarizer);
        }

        ToolResultCompactor? toolResultCompactor = config.EnableToolResultCompaction
            ? new ToolResultCompactor(
                config.MaxToolResultChars,
                config.ToolResultKeepHeadLines,
                config.ToolResultKeepTailLines)
            : null;

        ObservationMasker? observationMasker = config.EnableObservationMasking
            ? new ObservationMasker(
                config.ObservationMaskingProtectedTurns,
                config.ObservationMaskingMinResultLength)
            : null;

        return new ContextManager(
            tokenCounter, compactionTrigger, historyCompactor,
            toolResultCompactor: toolResultCompactor,
            observationMasker: observationMasker);
    }
}
