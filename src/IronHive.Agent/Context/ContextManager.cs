using IronHive.Abstractions.Exceptions;
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
    private ICompactionTrigger _compactionTrigger;
    private IHistoryCompactor _historyCompactor;
    private int _overflowHandlers;
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

    /// <summary>
    /// Whether <paramref name="message"/> was composed by a context manager for one preparation (an instruction block,
    /// the scratchpad, the goal reminder) rather than written by the user or the model. Such a message is removed and
    /// recomposed on every preparation, and is not the user's request.
    /// </summary>
    public static bool IsInjected(ChatMessage message)
        => message.AdditionalProperties?.ContainsKey(InjectedBlockKey) == true;

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
    /// Compact when the server reports an overflow rather than pre-emptively against a guessed window
    /// (<see cref="CompactionConfig.CompactOnOverflow"/>). Set by <see cref="ForModel(string, CompactionConfig, IChatClient?)"/>.
    /// </summary>
    public bool CompactOnOverflow { get; init; }

    /// <summary>
    /// The share of the window a compaction reduces the history to (<see cref="CompactionConfig.TargetRatio"/>). Default 0.70.
    /// </summary>
    public float TargetRatio { get; init; } = 0.70f;

    /// <summary>
    /// <c>true</c> while pre-emptive compaction is withheld: <see cref="CompactOnOverflow"/> is on, the window is still a guess
    /// (<see cref="IContextTokenCounter.IsContextWindowEstimated"/>), and a <see cref="ToolRoundContextChatClient"/> bound to this
    /// manager will catch the overflow. Without such a client nothing would catch it, so the guess is used as before.
    /// </summary>
    public bool DefersCompactionToOverflow
        => CompactOnOverflow && Volatile.Read(ref _overflowHandlers) > 0 && _tokenCounter.IsContextWindowEstimated;

    // Rebuilds the parts sized from the window (the token-based trigger and compactor clamp their budgets to it) once the
    // window is learned. Null for a manager assembled by hand; its parts are the caller's.
    internal Func<int, (ICompactionTrigger Trigger, IHistoryCompactor Compactor)>? RebuildForWindow { get; init; }

    internal void AttachOverflowHandler() => Interlocked.Increment(ref _overflowHandlers);

    /// <summary>
    /// Replaces the guessed window with <paramref name="tokens"/> learned from the server, and resizes the window-sized
    /// compaction parts to it. Returns <c>false</c> when the token counter cannot learn a window.
    /// </summary>
    public bool LearnContextWindow(int tokens)
    {
        if (!_tokenCounter.LearnContextWindow(tokens))
        {
            return false;
        }

        if (RebuildForWindow is { } rebuild)
        {
            var (trigger, compactor) = rebuild(tokens);
            _compactionTrigger = trigger;
            _historyCompactor = compactor;
        }

        return true;
    }

    /// <summary>
    /// Compacts a request the server refused as too long, and learns the window from the refusal: the window it states,
    /// or else the size that overflowed (an upper bound; a smaller real window is learned on the next overflow).
    /// </summary>
    internal async Task<IReadOnlyList<ChatMessage>> CompactAfterOverflowAsync(
        IReadOnlyList<ChatMessage> messages,
        ContextOverflowException overflow,
        CancellationToken cancellationToken)
    {
        var window = overflow.ContextWindow is > 0 and var stated
            ? stated
            : overflow.RequestTokens is > 0 and var sent ? sent : _tokenCounter.CountTokens(messages);
        LearnContextWindow(window);

        var result = await _historyCompactor.CompactAsync(messages, (int)(window * TargetRatio), cancellationToken);
        return result.CompactedHistory;
    }

    private bool NeedsCompaction(int currentTokens)
        => !DefersCompactionToOverflow && _compactionTrigger.ShouldCompact(currentTokens, _tokenCounter.MaxContextTokens);

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
            NeedsCompaction = NeedsCompaction(currentTokens),
            MessageCount = history.Count
        };
    }

    /// <summary>
    /// Checks if compaction should be triggered.
    /// </summary>
    public bool ShouldCompact(IReadOnlyList<ChatMessage> history)
    {
        return NeedsCompaction(_tokenCounter.CountTokens(history));
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

        if (!NeedsCompaction(currentTokens))
        {
            return new CompactionResult
            {
                CompactedHistory = history,
                OriginalTokens = currentTokens,
                CompactedTokens = currentTokens,
                MessagesCompacted = 0
            };
        }

        // Reduce to TargetRatio of the window to leave room for future messages
        var targetTokens = (int)(_tokenCounter.MaxContextTokens * TargetRatio);

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
        // The latest request, not the first: in a conversation every turn has its own, and the reminder must not point
        // the model back at turn 1's question.
        _goalReminder.SetGoalFromLatestUserMessage(history);
    }

    /// <summary>
    /// Sets the current goal explicitly.
    /// </summary>
    public void SetGoal(string goal)
    {
        _goalReminder.CurrentGoal = goal;
    }

    /// <summary>
    /// The cheap per-request reductions: compacts large tool results and masks old observations, when enabled. No LLM
    /// call, no injected blocks — safe to run before every model call, which is what
    /// <see cref="ToolRoundContextChatClient"/> does for the tool rounds inside a turn. Returns
    /// <paramref name="history"/> itself when nothing changed.
    /// </summary>
    public IReadOnlyList<ChatMessage> ReduceToolResults(IReadOnlyList<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var compactedResults = _toolResultCompactor?.CompactToolResults(history) ?? history;
        return _observationMasker?.MaskObservations(compactedResults) ?? compactedResults;
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

        // Step 0a/0b: Compact large tool results, then mask old observations (cheap, always run if enabled)
        var maskedHistory = ReduceToolResults(history);

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
            ObservationMaskingProtectedRounds = source.ObservationMaskingProtectedRounds,
            GoalReminder = source.GoalReminder,
            EnableToolResultCompaction = source.EnableToolResultCompaction,
            MaxToolResultChars = source.MaxToolResultChars,
            ToolResultKeepHeadLines = source.ToolResultKeepHeadLines,
            ToolResultKeepTailLines = source.ToolResultKeepTailLines,
            UseAnchoredCompaction = source.UseAnchoredCompaction,
            MaxAnchorStateChars = source.MaxAnchorStateChars,
            MaxContextTokens = source.MaxContextTokens,
            CompactOnOverflow = source.CompactOnOverflow,
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

        return FromConfig(new ContextTokenCounter(modelName, config.MaxContextTokens), config, summarizer);
    }

    /// <summary>
    /// Creates a context manager that applies every <see cref="CompactionConfig"/> setting — compaction trigger and
    /// compactor, tool-result compaction, observation masking, the goal reminder, <see cref="CompactOnOverflow"/> and
    /// <see cref="TargetRatio"/> — on the given token counter. <see cref="ForModel(string, CompactionConfig, IChatClient?)"/>
    /// and the container registration (<c>AddIronHiveAgent</c>) both build through this.
    /// </summary>
    /// <param name="tokenCounter">Token counter; its <see cref="IContextTokenCounter.MaxContextTokens"/> sizes the trigger.</param>
    /// <param name="config">Compaction configuration.</param>
    /// <param name="summarizer">Optional chat client for LLM-based summarization.</param>
    /// <param name="instructionContributors">System instruction sections added to every prepared history.</param>
    public static ContextManager FromConfig(
        IContextTokenCounter tokenCounter,
        CompactionConfig config,
        IChatClient? summarizer = null,
        IEnumerable<ISystemInstructionContributor>? instructionContributors = null)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(config);

        (ICompactionTrigger Trigger, IHistoryCompactor Compactor) WindowSized(int maxContextTokens)
        {
            if (config.UseAnchoredCompaction)
            {
                var (effectiveProtect, effectivePrune) = ClampToContext(
                    config.ProtectRecentTokens, config.MinimumPruneTokens, maxContextTokens);
                return (new TokenBasedCompactionTrigger(effectiveProtect, effectivePrune),
                    new AnchoredHistoryCompactor(tokenCounter, CloneWithClampedProtect(config, effectiveProtect, effectivePrune), summarizer));
            }

            if (config.UseTokenBasedCompaction)
            {
                var (effectiveProtect, effectivePrune) = ClampToContext(
                    config.ProtectRecentTokens, config.MinimumPruneTokens, maxContextTokens);
                return (new TokenBasedCompactionTrigger(effectiveProtect, effectivePrune),
                    new TokenBasedHistoryCompactor(tokenCounter, CloneWithClampedProtect(config, effectiveProtect, effectivePrune), summarizer));
            }

            return (new ThresholdCompactionTrigger(config.ThresholdPercentage), new HistoryCompactor(tokenCounter, summarizer));
        }

        var (compactionTrigger, historyCompactor) = WindowSized(tokenCounter.MaxContextTokens);

        ToolResultCompactor? toolResultCompactor = config.EnableToolResultCompaction
            ? new ToolResultCompactor(
                config.MaxToolResultChars,
                config.ToolResultKeepHeadLines,
                config.ToolResultKeepTailLines)
            : null;

        ObservationMasker? observationMasker = config.EnableObservationMasking
            ? new ObservationMasker(
                config.ObservationMaskingProtectedTurns,
                config.ObservationMaskingMinResultLength,
                config.ObservationMaskingProtectedRounds)
            : null;

        return new ContextManager(
            tokenCounter, compactionTrigger, historyCompactor,
            goalReminderOptions: config.GoalReminder,
            toolResultCompactor: toolResultCompactor,
            observationMasker: observationMasker,
            instructionContributors: instructionContributors)
        {
            CompactOnOverflow = config.CompactOnOverflow,
            TargetRatio = config.TargetRatio,
            RebuildForWindow = WindowSized,
        };
    }
}
