using System.Runtime.CompilerServices;
using System.Text;
using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Loop;

/// <summary>
/// Default implementation of the agent loop using Microsoft.Extensions.AI.
/// Implements "nO" style single-threaded master loop pattern.
/// </summary>
public class AgentLoop : IAgentLoop
{
    private readonly IChatClient _chatClient;
    private readonly AgentOptions _options;
    private readonly IUsageTracker? _usageTracker;
    private readonly ContextManager? _contextManager;
    private readonly TurnGuards _guards;
    private readonly IToolRetriever? _toolRetriever;
    private readonly StickyToolSelection _stickyTools = new();
    private readonly IReadOnlyList<ITurnObserver> _turnObservers;
    private readonly List<ChatMessage> _history = [];

    // What context management made of the history for the model; the history itself keeps every message
    private readonly ModelView _view = new();
    private readonly HostToolResultStage _hostResults;

    public AgentLoop(
        IChatClient chatClient,
        AgentOptions? options = null,
        IUsageTracker? usageTracker = null,
        ContextManager? contextManager = null,
        IErrorRecoveryService? errorRecovery = null,
        IToolRetriever? toolRetriever = null,
        IUsageLimiter? usageLimiter = null,
        IEnumerable<ITurnObserver>? turnObservers = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new AgentOptions();
        _usageTracker = usageTracker;
        _contextManager = contextManager;
        // A pipeline built before this loop's manager existed (a chat client factory's decorator) can carry an unbound
        // ToolRoundContextChatClient inside function invocation; it reduces each tool round with this loop's manager.
        if (contextManager is not null)
        {
            chatClient.GetService<ToolRoundContextChatClient>()?.Bind(contextManager);
        }
        _guards = new TurnGuards(usageLimiter, errorRecovery, _options.ModelId, chatClient);
        _toolRetriever = toolRetriever;
        _hostResults = new HostToolResultStage(chatClient);
        _turnObservers = turnObservers?.ToArray() ?? [];

        // Configure usage tracker with model ID for accurate pricing
        if (_usageTracker is not null && !string.IsNullOrEmpty(_options.ModelId))
        {
            _usageTracker.SetModel(_options.ModelId);
        }

        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, _options.SystemPrompt));
        }
    }

    /// <inheritdoc />
    public Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default)
        => RunAsync(prompt, overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public Task<AgentResponse> RunAsync(ChatMessage message, CancellationToken cancellationToken = default)
        => RunAsync(message, overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public Task<AgentResponse> RunAsync(string prompt, ChatOptions? overrideOptions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return RunAsync(new ChatMessage(ChatRole.User, prompt), overrideOptions, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentResponse> RunAsync(ChatMessage message, ChatOptions? overrideOptions, CancellationToken cancellationToken = default)
    {
        var started = UserTurn.Checked(message);
        _history.Add(started);
        return RunTurnAsync(started, overrideOptions, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentResponse> ContinueAsync(CancellationToken cancellationToken = default)
        => ContinueAsync(overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentResponse> ContinueAsync(ChatOptions? overrideOptions, CancellationToken cancellationToken = default)
    {
        ContinuationGuard.EnsureContinuable(_history);
        await _hostResults.ApplyAsync(_history, cancellationToken);
        return await RunTurnAsync(started: null, overrideOptions, cancellationToken);
    }

    /// <summary>One turn, as an <c>invoke_agent</c> span (<see cref="AgentTelemetry"/>).</summary>
    private async Task<AgentResponse> RunTurnAsync(ChatMessage? started, ChatOptions? overrideOptions, CancellationToken cancellationToken)
    {
        using var span = AgentTelemetry.StartTurn(_options);
        using var deadline = TurnDeadline.Start(_options.MaxTurnDuration, cancellationToken);
        try
        {
            var response = await RunTurnCoreAsync(started, overrideOptions, deadline.Token);
            AgentTelemetry.Complete(span, response.Usage, response.ToolCalls.Count, response.StopReason);
            return response;
        }
        catch (OperationCanceledException ex) when (deadline.Expired(ex))
        {
            var timeout = deadline.TimeoutException();
            AgentTelemetry.Fail(span, timeout);
            throw timeout;
        }
        catch (Exception ex)
        {
            AgentTelemetry.Fail(span, ex);
            throw;
        }
    }

    /// <summary>One streamed turn, as an <c>invoke_agent</c> span (<see cref="AgentTelemetry"/>).</summary>
    private async IAsyncEnumerable<AgentResponseChunk> RunTurnStreamingAsync(
        ChatMessage? started,
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var span = AgentTelemetry.StartTurn(_options);
        using var deadline = TurnDeadline.Start(_options.MaxTurnDuration, cancellationToken);
        await using var chunks = RunTurnStreamingCoreAsync(started, overrideOptions, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (true)
        {
            AgentResponseChunk chunk;
            try
            {
                if (!await chunks.MoveNextAsync())
                {
                    break;
                }

                chunk = chunks.Current;
            }
            catch (OperationCanceledException ex) when (deadline.Expired(ex))
            {
                var timeout = deadline.TimeoutException();
                AgentTelemetry.Fail(span, timeout);
                throw timeout;
            }
            catch (Exception ex)
            {
                AgentTelemetry.Fail(span, ex);
                throw;
            }

            if (chunk.Turn is { } turn)
            {
                AgentTelemetry.Complete(span, turn.Usage, turn.ToolCalls.Count, turn.StopReason);
            }

            yield return chunk;
        }
    }

    /// <summary>
    /// One turn over the current history. The caller has already put the message that starts it
    /// (a user prompt, or the host's tool results) at its end; <paramref name="started"/> is that user prompt, or
    /// <c>null</c> for a continuation.
    /// </summary>
    private async Task<AgentResponse> RunTurnCoreAsync(ChatMessage? started, ChatOptions? overrideOptions, CancellationToken cancellationToken)
    {
        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        _guards.ThrowIfUsageLimitExceeded();

        var chatOptions = await CreateChatOptionsAsync(overrideOptions, cancellationToken);
        var response = await _guards.CallWithRecoveryAsync(
            ct => _chatClient.GetResponseAsync(historyToSend, chatOptions, ct), cancellationToken);

        // Suggestion blocks leave the answer before the caller or the history sees it
        var suggestions = new TurnSuggestions(_options.Suggestions).Strip(response.Messages);

        // Add assistant response to history
        _history.AddRange(response.Messages);
        _hostResults.MarkProduced(response.Messages);
        var produced = new List<ChatMessage>(response.Messages);

        var toolCalls = ToolCallResultFactory.Extract(response);
        var stopReason = TurnStopReasons.Classify(response.Messages, response.FinishReason, chatOptions?.Tools, TurnStopReasons.MaximumIterationsOf(_chatClient));
        var usageDetails = response.Usage;
        var text = response.Text ?? string.Empty;
        var reasoning = LiveReasoning.Extract(response);

        // At the iteration cap, one closing request instead of ending the turn there (AgentOptions.WrapUpAtStepLimit)
        if (stopReason == TurnStopReason.StepLimit && _options.WrapUpAtStepLimit is { } wrapUp)
        {
            var closing = StepLimitWrapUps.ClosingMessages(response.Messages, wrapUp);
            _history.AddRange(closing);
            produced.AddRange(closing);

            var closingHistory = await PrepareHistoryForSendingAsync(cancellationToken);
            _guards.ThrowIfUsageLimitExceeded();
            var closingOptions = StepLimitWrapUps.Options(chatOptions!, wrapUp);
            var closed = await _guards.CallWithRecoveryAsync(
                ct => _chatClient.GetResponseAsync(closingHistory, closingOptions, ct), cancellationToken);
            suggestions = new TurnSuggestions(_options.Suggestions).Strip(closed.Messages) ?? suggestions;

            _history.AddRange(closed.Messages);
            _hostResults.MarkProduced(closed.Messages);
            produced.AddRange(closed.Messages);
            toolCalls.AddRange(ToolCallResultFactory.Extract(closed));
            stopReason = StepLimitWrapUps.StopReason(
                TurnStopReasons.Classify(closed.Messages, closed.FinishReason, closingOptions.Tools, TurnStopReasons.MaximumIterationsOf(_chatClient)));
            if (closed.Usage is { } closedUsage)
            {
                usageDetails ??= new UsageDetails();
                usageDetails.Add(closedUsage);
            }

            text = JoinParts(text, closed.Text);
            reasoning = JoinParts(reasoning ?? string.Empty, LiveReasoning.Extract(closed));
        }

        var added = TurnMessages.Of(started, produced);
        var usage = MapUsage(usageDetails);

        // Record usage for session tracking
        if (usage is not null)
        {
            _usageTracker?.Record(usage);
            _guards.RecordUsage(usage);
        }

        var content = text;
        var thinking = LiveReasoning.ToThinkingContent(reasoning);
        var addendum = await TurnObserverNotifier.NotifyAsync(
            _turnObservers,
            new TurnRecord { Content = content, ToolCalls = toolCalls, Usage = usage, ThinkingContent = thinking, StopReason = stopReason, Suggestions = suggestions, Messages = added },
            cancellationToken);

        return new AgentResponse
        {
            // The addendum rides at the end of Content so a consumer that only renders Content still
            // shows it, and stays separately readable so it can be told apart from the model's words.
            Content = addendum is null ? content : content + TurnObserverNotifier.AddendumSeparator + addendum,
            HasTextOutput = content.Length > 0,
            ToolCalls = toolCalls,
            Usage = usage,
            ThinkingContent = thinking,
            Addendum = addendum,
            Suggestions = suggestions,
            StopReason = stopReason,
            Messages = added
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string prompt, CancellationToken cancellationToken = default)
        => RunStreamingAsync(prompt, overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(ChatMessage message, CancellationToken cancellationToken = default)
        => RunStreamingAsync(message, overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(
        string prompt,
        ChatOptions? overrideOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), overrideOptions, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(
        ChatMessage message,
        ChatOptions? overrideOptions,
        CancellationToken cancellationToken = default)
    {
        UserTurn.Checked(message);
        return RunStreamingFromAsync(message, overrideOptions, cancellationToken);
    }

    private async IAsyncEnumerable<AgentResponseChunk> RunStreamingFromAsync(
        ChatMessage message,
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _history.Add(message);
        await foreach (var chunk in RunTurnStreamingAsync(message, overrideOptions, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentResponseChunk> ContinueStreamingAsync(CancellationToken cancellationToken = default)
        => ContinueStreamingAsync(overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentResponseChunk> ContinueStreamingAsync(
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ContinuationGuard.EnsureContinuable(_history);
        await _hostResults.ApplyAsync(_history, cancellationToken);
        await foreach (var chunk in RunTurnStreamingAsync(started: null, overrideOptions, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>Streaming counterpart of <see cref="RunTurnAsync"/>.</summary>
    private async IAsyncEnumerable<AgentResponseChunk> RunTurnStreamingCoreAsync(
        ChatMessage? started,
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        _guards.ThrowIfUsageLimitExceeded();

        var chatOptions = await CreateChatOptionsAsync(overrideOptions, cancellationToken);
        var streamedSuggestions = new TurnSuggestions(_options.Suggestions);
        var round = new StreamedRound();
        await foreach (var chunk in StreamRoundAsync(historyToSend, chatOptions, round, streamedSuggestions, textPrefix: null, cancellationToken))
        {
            yield return chunk;
        }

        // Add the turn to history for multi-turn conversations. Rebuilt to the same shape the
        // non-streaming path leaves behind -- tool results included, and in the order they arrived.
        var turnMessages = round.History.Build();
        var streamedStopReason = TurnStopReasons.Classify(turnMessages, round.FinishReason, chatOptions?.Tools, TurnStopReasons.MaximumIterationsOf(_chatClient));
        _history.AddRange(turnMessages);
        _hostResults.MarkProduced(turnMessages);
        var produced = new List<ChatMessage>(turnMessages);
        var rounds = new List<StreamedRound> { round };

        // At the iteration cap, one closing request instead of ending the turn there (AgentOptions.WrapUpAtStepLimit)
        if (streamedStopReason == TurnStopReason.StepLimit && _options.WrapUpAtStepLimit is { } wrapUp)
        {
            var closing = StepLimitWrapUps.ClosingMessages(turnMessages, wrapUp);
            _history.AddRange(closing);
            produced.AddRange(closing);

            var closingHistory = await PrepareHistoryForSendingAsync(cancellationToken);
            _guards.ThrowIfUsageLimitExceeded();
            var closingOptions = StepLimitWrapUps.Options(chatOptions!, wrapUp);
            var closingRound = new StreamedRound();
            var separator = round.Text.Length > 0 ? TurnObserverNotifier.AddendumSeparator : null;
            await foreach (var chunk in StreamRoundAsync(closingHistory, closingOptions, closingRound, streamedSuggestions, separator, cancellationToken))
            {
                yield return chunk;
            }

            var closingMessages = closingRound.History.Build();
            _history.AddRange(closingMessages);
            _hostResults.MarkProduced(closingMessages);
            produced.AddRange(closingMessages);
            rounds.Add(closingRound);
            streamedStopReason = StepLimitWrapUps.StopReason(
                TurnStopReasons.Classify(closingMessages, closingRound.FinishReason, closingOptions.Tools, TurnStopReasons.MaximumIterationsOf(_chatClient)));
        }

        var streamedAdded = TurnMessages.Of(started, produced);

        UsageDetails? usageDetails = null;
        foreach (var r in rounds.Where(r => r.Usage is not null))
        {
            usageDetails ??= new UsageDetails();
            usageDetails.Add(r.Usage!);
        }

        var streamedUsage = MapUsage(usageDetails);
        if (streamedUsage is not null)
        {
            _usageTracker?.Record(streamedUsage);
            _guards.RecordUsage(streamedUsage);
        }

        // One final chunk always closes the stream, carrying the turn's consolidated record. Before
        // it existed this path yielded tool calls one delta at a time and nothing that said what the
        // turn as a whole did -- so every consumer rebuilt that correlation outside the loop.
        var turn = new TurnRecord
        {
            Content = string.Concat(rounds.Select(r => r.Text.ToString())),
            ToolCalls = ToolCallResultFactory.Extract([.. rounds.SelectMany(r => r.ToolCalls)], [.. rounds.SelectMany(r => r.ToolResults)]),
            Usage = streamedUsage,
            ThinkingContent = LiveReasoning.ToThinkingContent(string.Concat(rounds.Select(r => r.Reasoning.ToString()))),
            StopReason = streamedStopReason,
            Suggestions = streamedSuggestions.Drain(),
            Messages = streamedAdded
        };

        yield return new AgentResponseChunk
        {
            Turn = turn,
            Usage = streamedUsage,
            // Appended, never edited in: the deltas above are already rendered by the time a turn is
            // complete, so amending what the model said is not a capability this seam can honestly offer.
            // Carried as its own field, not as a text delta, so consumers can tell it from the model's words.
            Addendum = await TurnObserverNotifier.NotifyAsync(_turnObservers, turn, cancellationToken)
        };
    }

    /// <summary>What one streamed model request of a turn produced.</summary>
    private sealed class StreamedRound
    {
        public StringBuilder Text { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public List<FunctionCallContent> ToolCalls { get; } = [];

        // Function-invocation middleware, when present, streams the outcome of each call back as a
        // FunctionResultContent update. Collecting them is what lets the turn record report an
        // honest Success instead of an unknown one.
        public List<FunctionResultContent> ToolResults { get; } = [];
        public StreamingTurnHistoryBuilder History { get; } = new();
        public UsageDetails? Usage { get; set; }
        public ChatFinishReason? FinishReason { get; set; }
    }

    /// <summary>
    /// Streams one model request, yielding its chunks and collecting into <paramref name="round"/>.
    /// <paramref name="textPrefix"/>, when given, is yielded before the request's first text (it separates a closing
    /// request's answer from the capped round's).
    /// </summary>
    private async IAsyncEnumerable<AgentResponseChunk> StreamRoundAsync(
        IReadOnlyList<ChatMessage> historyToSend,
        ChatOptions chatOptions,
        StreamedRound round,
        TurnSuggestions streamedSuggestions,
        string? textPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = _chatClient.GetStreamingResponseAsync(historyToSend, chatOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _guards.RecordStreamingError(ex);
            throw;
        }

        string Prefixed(string text)
        {
            if (textPrefix is null)
            {
                return text;
            }

            var prefixed = textPrefix + text;
            textPrefix = null;
            return prefixed;
        }

        await foreach (var update in stream)
        {
            // Live reasoning from a thinking model, as it is written. It is not part of the response text; the turn
            // record carries it whole as ThinkingContent, as the non-streaming path does.
            if (LiveReasoning.Extract(update) is { } reasoning)
            {
                round.Reasoning.Append(reasoning);
                yield return new AgentResponseChunk { ThinkingDelta = reasoning };
            }

            // Yield and collect text content
            var visible = string.IsNullOrEmpty(update.Text) ? update.Text : streamedSuggestions.Feed(update.Text);
            if (!string.IsNullOrEmpty(visible))
            {
                visible = Prefixed(visible);
                round.Text.Append(visible);
                round.History.AppendText(visible);
                yield return new AgentResponseChunk
                {
                    TextDelta = visible
                };
            }

            // Argument fragments of a call still being written (AgentOptions.StreamToolArguments) — progress only; the
            // complete call below is what is recorded and run.
            foreach (var fragment in update.Contents.OfType<IronHive.Extensions.AI.FunctionCallDeltaContent>())
            {
                yield return new AgentResponseChunk { ToolCallDelta = ToolCallChunkFactory.FromDelta(fragment) };
            }

            // Yield and collect tool call updates
            // Text before a tool call is complete: what the suggestion parser held back is text after all.
            if (update.Contents.OfType<FunctionCallContent>().Any() && streamedSuggestions.Flush() is { Length: > 0 } heldBeforeCall)
            {
                var held = Prefixed(heldBeforeCall);
                round.Text.Append(held);
                round.History.AppendText(held);
                yield return new AgentResponseChunk { TextDelta = held };
            }

            if (update.Contents.OfType<FunctionCallContent>().Any())
            {
                foreach (var functionCall in update.Contents.OfType<FunctionCallContent>())
                {
                    round.ToolCalls.Add(functionCall);
                    round.History.AppendCall(functionCall);
                    yield return new AgentResponseChunk
                    {
                        ToolCallDelta = ToolCallChunkFactory.FromFunctionCall(functionCall)
                    };
                }
            }

            var updateResults = update.Contents.OfType<FunctionResultContent>().ToArray();
            round.ToolResults.AddRange(updateResults);
            round.History.AppendResults(updateResults);

            // Each call's outcome as it arrives, not only in the final Turn record.
            foreach (var arrived in ToolCallResultFactory.ForArrivedResults(round.ToolCalls, updateResults))
            {
                yield return new AgentResponseChunk { ToolResult = arrived };
            }

            round.Usage = TurnGuards.AccumulateUsage(round.Usage, update);
            round.FinishReason = update.FinishReason ?? round.FinishReason;
        }

        if (streamedSuggestions.Flush() is { Length: > 0 } heldAtEnd)
        {
            var held = Prefixed(heldAtEnd);
            round.Text.Append(held);
            round.History.AppendText(held);
            yield return new AgentResponseChunk { TextDelta = held };
        }
    }

    /// <summary>
    /// Prepares history for sending to the model.
    /// Applies context management (compaction, goal reminder) if available.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> PrepareHistoryForSendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (_contextManager is null)
        {
            return _history.AsReadOnly();
        }

        // The history keeps every message; what compaction and masking made of it is the model's view, kept apart and
        // extended on the next turn (before 0.64.0 it replaced the history, and the originals were gone for good)
        var preparedHistory = await _contextManager.PrepareHistoryAsync(_view.Compose(_history), cancellationToken);
        _view.Update(preparedHistory, _history);

        return preparedHistory;
    }

    private async Task<ChatOptions> CreateChatOptionsAsync(ChatOptions? overrideOptions, CancellationToken cancellationToken)
    {
        var tools = _options.Tools;

        // Step 1: Dynamic tool retrieval (select relevant tools for the query)
        if (_toolRetriever is not null && tools is { Count: > 0 })
        {
            var query = GetLatestUserQuery();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var retrievalOptions = _stickyTools.Apply(_options.ToolRetrievalOptions);
                var result = await _toolRetriever.RetrieveAsync(
                    query, tools, retrievalOptions, cancellationToken);
                _stickyTools.Record(retrievalOptions, result);
                tools = result.SelectedTools;
            }
        }

        // Step 2: Tool schema compression (reduce token usage)
        if (tools is { Count: > 0 } && _options.ToolSchemaCompression != ToolSchemaCompressionLevel.None)
        {
            tools = ToolSchemaCompressor.CompressTools(tools, _options.ToolSchemaCompression);
        }

        return TurnSuggestions.Request(ChatOptionsOverride.Apply(ChatOptionsOverride.Baseline(_options, tools), overrideOptions), _options.Suggestions);
    }

    private string GetLatestUserQuery()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            // An injected message (the goal reminder) is not the user's request.
            if (_history[i].Role == ChatRole.User && !ContextManager.IsInjected(_history[i]))
            {
                return _history[i].Text ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static TokenUsage? MapUsage(UsageDetails? usage) => TokenUsage.From(usage);

    /// <summary>The text of a capped round and of its closing request, as one answer.</summary>
    private static string JoinParts(string first, string? second) =>
        string.IsNullOrEmpty(second) ? first : first.Length == 0 ? second : first + TurnObserverNotifier.AddendumSeparator + second;

    /// <summary>
    /// Clears the conversation history.
    /// </summary>
    public void ClearHistory()
    {
        _contextManager?.ClearRetainedResults();
        _history.Clear();
        _view.Reset();
        _stickyTools.Clear();

        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, _options.SystemPrompt));
        }
    }

    /// <summary>
    /// Gets the current conversation history.
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<AITool> Tools => _options.Tools is { } tools ? [.. tools] : [];

    /// <summary>
    /// Gets the context manager if one is configured.
    /// </summary>
    public ContextManager? ContextManager => _contextManager;

    /// <summary>
    /// Gets the current context usage if context manager is configured.
    /// </summary>
    public ContextUsage? GetContextUsage()
    {
        return _contextManager?.GetUsage(_view.Compose(_history));
    }

    /// <inheritdoc />
    public void InitializeHistory(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _contextManager?.ClearRetainedResults();

        // Keep the system prompt at the beginning
        var systemPrompt = _history.FirstOrDefault(m => m.Role == ChatRole.System);
        _history.Clear();
        _view.Reset();
        _stickyTools.Clear();

        if (systemPrompt is not null)
        {
            _history.Add(systemPrompt);
        }
        else if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, _options.SystemPrompt));
        }

        // Add the restored messages (skip system messages from restored history)
        foreach (var message in messages.Where(m => m.Role != ChatRole.System))
        {
            _history.Add(message);
        }
    }
}

/// <summary>
/// Options for configuring the agent loop.
/// </summary>
public class AgentOptions
{
    /// <summary>
    /// The agent's name, reported as <c>gen_ai.agent.name</c> on each turn's <c>invoke_agent</c> span
    /// (<see cref="AgentTelemetry"/>). Optional.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Longest one turn may run — model calls and tool runs together. A turn still running then is cancelled and the
    /// turn method throws <see cref="TimeoutException"/>; a streamed turn's chunks already delivered stay delivered.
    /// Null (the default): no limit.
    /// </summary>
    public TimeSpan? MaxTurnDuration { get; set; }

    /// <summary>
    /// System prompt to initialize the agent.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Model ID for accurate token pricing calculation.
    /// Used by TokenMeter to look up model-specific pricing.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Temperature for response generation.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Maximum tokens for response generation.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// How much the model reasons before it answers, sent with every model call of every turn — e.g.
    /// <c>new ReasoningOptions { Effort = ReasoningEffort.Low }</c>. A reasoning model left at its own default can think
    /// for tens of thousands of tokens on a single call. The OpenAI-compatible provider sends <c>reasoning_effort</c>,
    /// a thinking-token budget and the thinking switches open-weight chat templates read; how strictly a server holds a
    /// level is the server's — <see cref="ReasoningEffort.None"/> (thinking off) is the setting servers enforce. A per-turn
    /// <see cref="ChatOptions.Reasoning"/> override replaces it for that turn. Null (the default): nothing is sent, and
    /// the provider's default applies.
    /// </summary>
    public ReasoningOptions? Reasoning { get; set; }

    /// <summary>
    /// Asks the model to end its answer with follow-up suggestions — a question and a few answers the user can pick — as
    /// <c>MessageRequest.Suggestions</c> does on the IronHive message loop, with the same instruction and block format
    /// (<see cref="IronHive.Abstractions.Messages.SuggestionCollector"/>). The blocks are taken out of the answer text (streamed or not, and out of the
    /// history the next turn sends) and returned as <see cref="AgentResponse.Suggestions"/> / <see cref="TurnRecord.Suggestions"/>.
    /// Null (the default): no instruction is added and the text is left as the model wrote it.
    /// </summary>
    public IronHive.Abstractions.Messages.SuggestionOptions? Suggestions { get; set; }

    /// <summary>
    /// Available tools for the agent.
    /// </summary>
    /// <remarks>
    /// The agent loop only extracts <see cref="Microsoft.Extensions.AI.FunctionCallContent"/> from
    /// the model's response — it does not itself invoke a matching tool. The <c>IChatClient</c>
    /// passed to the agent loop's constructor must be wrapped with
    /// <c>Microsoft.Extensions.AI</c>'s function-invocation middleware
    /// (<c>chatClient.AsBuilder().UseFunctionInvocation().Build()</c>) for a tool listed here to
    /// ever actually run; without it, registering a tool has no effect beyond exposing its schema
    /// to the model.
    /// </remarks>
    public IList<AITool>? Tools { get; set; }

    /// <summary>
    /// Tool schema compression level. Reduces tool definition token usage.
    /// Default: None (no compression).
    /// </summary>
    public ToolSchemaCompressionLevel ToolSchemaCompression { get; set; } = ToolSchemaCompressionLevel.None;

    /// <summary>
    /// Options for dynamic tool retrieval. Only used when an IToolRetriever is configured.
    /// </summary>
    public ToolRetrievalOptions? ToolRetrievalOptions { get; set; }

    /// <summary>
    /// Stream a tool call's arguments while the model writes them: <c>RunStreamingAsync</c> then also yields
    /// <see cref="AgentResponseChunk.ToolCallDelta"/> chunks with <see cref="ToolCallChunk.IsComplete"/> = <c>false</c>
    /// (same <see cref="ToolCallChunk.Id"/>, name on the first, raw partial JSON in <see cref="ToolCallChunk.ArgumentsDelta"/>)
    /// before the complete chunk, which still follows and is what tools run on. Needs a chat client built on IronHive's
    /// bridge (<c>IronHive.Extensions.AI.ChatClientAdapter</c>) and a provider that streams arguments (Chat Completions,
    /// Responses, Anthropic); with any other client nothing changes. Default false: a consumer that shows one event per
    /// tool chunk would otherwise see each call several times.
    /// </summary>
    public bool StreamToolArguments { get; set; }

    /// <summary>
    /// Closes a turn that reached the function-invocation middleware's iteration cap with one more request instead of
    /// ending it there — see <see cref="StepLimitWrapUp"/>. Null (the default): the turn ends with
    /// <see cref="TurnStopReason.StepLimit"/>. Applied by <see cref="AgentLoop"/>; <see cref="ThinkingAgentLoop"/> refuses it
    /// (<see cref="NotSupportedException"/> from its constructor).
    /// </summary>
    public StepLimitWrapUp? WrapUpAtStepLimit { get; set; }
}
