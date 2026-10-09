using System.Runtime.CompilerServices;
using System.Text;
using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Tracking;
using IndexThinking.Agents;
using IndexThinking.Client;
using IndexThinking.Core;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Agent loop with IndexThinking integration for token management and reasoning extraction.
/// </summary>
public class ThinkingAgentLoop : IAgentLoop, IAsyncDisposable
{
    private readonly ThinkingChatClient _thinkingClient;
    private readonly AgentOptions _options;
    private readonly IUsageTracker? _usageTracker;
    private readonly ContextManager? _contextManager;
    private readonly IToolRetriever? _toolRetriever;
    private readonly StickyToolSelection _stickyTools = new();
    private readonly IReadOnlyList<ITurnObserver> _turnObservers;
    private readonly TurnGuards _guards;
    private readonly List<ChatMessage> _history = [];
    private readonly HostToolResultStage _hostResults;

    /// <param name="chatClient">The client the thinking layer wraps.</param>
    /// <param name="turnManager">IndexThinking's turn manager (reasoning extraction, truncation continuation).</param>
    /// <param name="options">Loop options — system prompt, tools, model id used for pricing.</param>
    /// <param name="thinkingOptions">Options for the wrapping <see cref="ThinkingChatClient"/>.</param>
    /// <param name="usageTracker">Session usage accounting.</param>
    /// <param name="contextManager">History compaction and goal reminders.</param>
    /// <param name="toolRetriever">Per-turn tool selection.</param>
    /// <param name="turnObservers">Post-turn observers.</param>
    /// <param name="errorRecovery">When set, a buffered turn that fails transiently is retried once — the same
    /// safeguard <see cref="AgentLoop"/> applies.</param>
    /// <param name="usageLimiter">When set, a turn is refused with <see cref="Exceptions.UsageLimitExceededException"/>
    /// once the session limit is reached, and each turn's usage is fed into it — the same safeguard
    /// <see cref="AgentLoop"/> applies.</param>
    public ThinkingAgentLoop(
        IChatClient chatClient,
        IThinkingTurnManager turnManager,
        AgentOptions? options = null,
        ThinkingChatClientOptions? thinkingOptions = null,
        IUsageTracker? usageTracker = null,
        ContextManager? contextManager = null,
        IToolRetriever? toolRetriever = null,
        IEnumerable<ITurnObserver>? turnObservers = null,
        IErrorRecoveryService? errorRecovery = null,
        IUsageLimiter? usageLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(turnManager);

        _thinkingClient = new ThinkingChatClient(
            chatClient,
            turnManager,
            thinkingOptions ?? new ThinkingChatClientOptions());

        _options = options ?? new AgentOptions();
        _usageTracker = usageTracker;
        _contextManager = contextManager;
        // A pipeline built before this loop's manager existed (a chat client factory's decorator) can carry an unbound
        // ToolRoundContextChatClient inside function invocation; it reduces each tool round with this loop's manager.
        if (contextManager is not null)
        {
            chatClient.GetService<ToolRoundContextChatClient>()?.Bind(contextManager);
        }
        _toolRetriever = toolRetriever;
        _hostResults = new HostToolResultStage(chatClient);
        _turnObservers = turnObservers?.ToArray() ?? [];
        _guards = new TurnGuards(usageLimiter, errorRecovery, _options.ModelId, chatClient);

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
        _history.Add(UserTurn.Checked(message));
        return RunTurnAsync(overrideOptions, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentResponse> ContinueAsync(CancellationToken cancellationToken = default)
        => ContinueAsync(overrideOptions: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentResponse> ContinueAsync(ChatOptions? overrideOptions, CancellationToken cancellationToken = default)
    {
        ContinuationGuard.EnsureContinuable(_history);
        await _hostResults.ApplyAsync(_history, cancellationToken);
        return await RunTurnAsync(overrideOptions, cancellationToken);
    }

    /// <summary>One turn, as an <c>invoke_agent</c> span (<see cref="AgentTelemetry"/>).</summary>
    private async Task<AgentResponse> RunTurnAsync(ChatOptions? overrideOptions, CancellationToken cancellationToken)
    {
        using var span = AgentTelemetry.StartTurn(_options);
        using var deadline = TurnDeadline.Start(_options.MaxTurnDuration, cancellationToken);
        try
        {
            var response = await RunTurnCoreAsync(overrideOptions, deadline.Token);
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
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var span = AgentTelemetry.StartTurn(_options);
        using var deadline = TurnDeadline.Start(_options.MaxTurnDuration, cancellationToken);
        await using var chunks = RunTurnStreamingCoreAsync(overrideOptions, deadline.Token).GetAsyncEnumerator(deadline.Token);
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
    /// (a user prompt, or the host's tool results) at its end.
    /// </summary>
    private async Task<AgentResponse> RunTurnCoreAsync(ChatOptions? overrideOptions, CancellationToken cancellationToken)
    {
        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        _guards.ThrowIfUsageLimitExceeded();

        var chatOptions = await CreateChatOptionsAsync(overrideOptions, cancellationToken);
        var response = await _guards.CallWithRecoveryAsync(
            ct => _thinkingClient.GetResponseAsync(historyToSend, chatOptions, ct), cancellationToken);

        // Add assistant response to history
        _history.AddRange(response.Messages);
        _hostResults.MarkProduced(response.Messages);

        var toolCalls = ToolCallResultFactory.Extract(response);
        var stopReason = TurnStopReasons.Classify(response.Messages, response.FinishReason, chatOptions?.Tools, TurnStopReasons.MaximumIterationsOf(_thinkingClient));
        var thinkingContent = ExtractThinkingContent(response);
        var usage = MapUsage(response.Usage);

        // Record usage for session tracking
        if (usage is not null)
        {
            _usageTracker?.Record(usage);
            _guards.RecordUsage(usage);
        }

        var content = response.Text ?? string.Empty;
        var addendum = await TurnObserverNotifier.NotifyAsync(
            _turnObservers,
            new TurnRecord
            {
                Content = content,
                ToolCalls = toolCalls,
                Usage = usage,
                ThinkingContent = thinkingContent,
                StopReason = stopReason
            },
            cancellationToken);

        return new AgentResponse
        {
            Content = addendum is null ? content : content + TurnObserverNotifier.AddendumSeparator + addendum,
            HasTextOutput = !string.IsNullOrEmpty(response.Text),
            ToolCalls = toolCalls,
            Usage = usage,
            ThinkingContent = thinkingContent,
            Addendum = addendum,
            StopReason = stopReason
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
        await foreach (var chunk in RunTurnStreamingAsync(overrideOptions, cancellationToken))
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
        await foreach (var chunk in RunTurnStreamingAsync(overrideOptions, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>Streaming counterpart of <see cref="RunTurnAsync"/>.</summary>
    private async IAsyncEnumerable<AgentResponseChunk> RunTurnStreamingCoreAsync(
        ChatOptions? overrideOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        _guards.ThrowIfUsageLimitExceeded();

        var chatOptions = await CreateChatOptionsAsync(overrideOptions, cancellationToken);
        var responseBuilder = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();
        var toolResults = new List<FunctionResultContent>();
        var historyBuilder = new StreamingTurnHistoryBuilder();
        var thinkingBuilder = new StringBuilder();
        UsageDetails? usageDetails = null;
        ChatFinishReason? finishReason = null;

        // Track live reasoning streamed this turn so the turn-end metadata thinking is not emitted
        // a second time (prefer-live; see ComputeMetadataThinkingTail).
        var liveReasoning = new StringBuilder();
        var sawLiveReasoning = false;

        await foreach (var update in _thinkingClient.GetStreamingResponseAsync(historyToSend, chatOptions, cancellationToken))
        {
            // 1. Live, provider-native reasoning (M.E.AI TextReasoningContent, e.g. from the streaming
            //    reasoning separator or a reasoning-capable model). Bridge each delta to ThinkingDelta
            //    immediately so consumers get live separation instead of hand-splitting <think> tags.
            var liveDelta = LiveReasoning.Extract(update);
            if (!string.IsNullOrEmpty(liveDelta))
            {
                liveReasoning.Append(liveDelta);
                sawLiveReasoning = true;
                thinkingBuilder.Append(liveDelta);
                yield return new AgentResponseChunk
                {
                    ThinkingDelta = liveDelta
                };
            }

            // 2. Turn-end metadata thinking (AdditionalProperties[ThinkingContentKey]). If live
            //    reasoning already streamed, emit only the tail not covered by it (continuation rounds
            //    can append reasoning absent from the live stream); otherwise emit it whole — the path
            //    consumers relied on before live separation existed.
            var metadataThinking = ExtractMetadataThinking(update);
            if (!string.IsNullOrEmpty(metadataThinking))
            {
                var thinkingDelta = sawLiveReasoning
                    ? ComputeMetadataThinkingTail(liveReasoning.ToString(), metadataThinking)
                    : metadataThinking;
                if (!string.IsNullOrEmpty(thinkingDelta))
                {
                    thinkingBuilder.Append(thinkingDelta);
                    yield return new AgentResponseChunk
                    {
                        ThinkingDelta = thinkingDelta
                    };
                }
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                responseBuilder.Append(update.Text);
                historyBuilder.AppendText(update.Text);
                yield return new AgentResponseChunk
                {
                    TextDelta = update.Text
                };
            }

            // Argument fragments of a call still being written (AgentOptions.StreamToolArguments) — progress only.
            foreach (var fragment in update.Contents.OfType<IronHive.Extensions.AI.FunctionCallDeltaContent>())
            {
                yield return new AgentResponseChunk { ToolCallDelta = ToolCallChunkFactory.FromDelta(fragment) };
            }

            if (update.Contents.OfType<FunctionCallContent>().Any())
            {
                foreach (var functionCall in update.Contents.OfType<FunctionCallContent>())
                {
                    toolCalls.Add(functionCall);
                    historyBuilder.AppendCall(functionCall);
                    yield return new AgentResponseChunk
                    {
                        ToolCallDelta = ToolCallChunkFactory.FromFunctionCall(functionCall)
                    };
                }
            }

            var updateResults = update.Contents.OfType<FunctionResultContent>().ToArray();
            toolResults.AddRange(updateResults);
            historyBuilder.AppendResults(updateResults);

            // Each call's outcome as it arrives, not only in the final Turn record.
            foreach (var arrived in ToolCallResultFactory.ForArrivedResults(toolCalls, updateResults))
            {
                yield return new AgentResponseChunk { ToolResult = arrived };
            }

            usageDetails = TurnGuards.AccumulateUsage(usageDetails, update);
            finishReason = update.FinishReason ?? finishReason;
        }

        // Same rebuild as AgentLoop -- the peer implementation lost tool results in exactly the same way.
        var turnMessages = historyBuilder.Build();
        var streamedStopReason = TurnStopReasons.Classify(turnMessages, finishReason, chatOptions?.Tools, TurnStopReasons.MaximumIterationsOf(_thinkingClient));
        _history.AddRange(turnMessages);
        _hostResults.MarkProduced(turnMessages);

        // This path reported no usage at all until now -- RunAsync recorded it, the streaming twin
        // silently did not, so a session that streamed had no token accounting.
        var streamedUsage = MapUsage(usageDetails);
        if (streamedUsage is not null)
        {
            _usageTracker?.Record(streamedUsage);
            _guards.RecordUsage(streamedUsage);
        }

        var thinking = thinkingBuilder.Length > 0
            ? new ThinkingContent { Content = thinkingBuilder.ToString() }
            : null;

        var turn = new TurnRecord
        {
            Content = responseBuilder.ToString(),
            ToolCalls = ToolCallResultFactory.Extract(toolCalls, toolResults),
            Usage = streamedUsage,
            ThinkingContent = thinking,
            StopReason = streamedStopReason
        };

        yield return new AgentResponseChunk
        {
            Turn = turn,
            Usage = streamedUsage,
            Addendum = await TurnObserverNotifier.NotifyAsync(_turnObservers, turn, cancellationToken)
        };
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

        var preparedHistory = await _contextManager.PrepareHistoryAsync(_history, cancellationToken);

        // If history was modified (compaction or goal reminder injection),
        // update our internal history to stay in sync
        if (!ReferenceEquals(preparedHistory, _history))
        {
            _history.Clear();
            _history.AddRange(preparedHistory);
        }

        return preparedHistory;
    }

    /// <summary>
    /// Extracts turn-end thinking that <see cref="ThinkingChatClient"/> publishes on the final
    /// metadata update's <c>AdditionalProperties</c>. This is the whole-turn thinking blob, used as
    /// the path for callers without live reasoning separation.
    /// </summary>
    private static string? ExtractMetadataThinking(ChatResponseUpdate update)
    {
        if (update.AdditionalProperties?.TryGetValue(
            ThinkingChatClient.ThinkingContentKey, out var value) == true)
        {
            // Handle different possible formats
            if (value is string text)
            {
                return text;
            }
            if (value is IndexThinking.Core.ThinkingContent thinking)
            {
                return thinking.Text;
            }
        }

        return null;
    }

    /// <summary>
    /// When live reasoning already streamed this turn, returns only the portion of the turn-end
    /// metadata thinking not already covered by it — i.e. the suffix a continuation round appended
    /// (prefix match → empty tail, nothing re-emitted). On mismatch, suppresses rather than duplicate
    /// the live deltas.
    /// <para>
    /// KNOWN LIMITATION: the live text (<c>StreamingReasoningSeparator</c>, raw substrings) and
    /// the metadata text (turn manager <c>ParseReasoning</c>, which may heuristically strip/trim and is
    /// provider-format specific) come from DIFFERENT parsers and need not be prefix-aligned. When they
    /// diverge we dedup to the live deltas, so reasoning that a continuation round added is not shown —
    /// the same state as before live and metadata reasoning were separated (no new loss). Emitting the full
    /// metadata instead would duplicate the live part, which is worse.
    /// </para>
    /// </summary>
    private static string? ComputeMetadataThinkingTail(string liveReasoning, string metadataThinking)
    {
        if (metadataThinking.Length <= liveReasoning.Length)
        {
            return null;
        }

        return metadataThinking.StartsWith(liveReasoning, StringComparison.Ordinal)
            ? metadataThinking[liveReasoning.Length..]
            : null;
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

        return ChatOptionsOverride.Apply(ChatOptionsOverride.Baseline(_options, tools), overrideOptions);
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

    private static ThinkingContent? ExtractThinkingContent(ChatResponse response)
    {
        if (response.AdditionalProperties?.TryGetValue(
            ThinkingChatClient.ThinkingContentKey, out var value) == true &&
            value is IndexThinking.Core.ThinkingContent thinking)
        {
            return new ThinkingContent
            {
                Content = thinking.Text,
                TokenCount = thinking.TokenCount
            };
        }

        // Fallback: provider-native reasoning carried as M.E.AI TextReasoningContent on the response
        // contents, with no AdditionalProperties blob. No dedup needed — non-streaming has no live stream.
        return LiveReasoning.ToThinkingContent(LiveReasoning.Extract(response));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _thinkingClient.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static TokenUsage? MapUsage(UsageDetails? usage) => TokenUsage.From(usage);

    /// <summary>
    /// Clears the conversation history.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
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
    public void InitializeHistory(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Keep the system prompt at the beginning
        var systemPrompt = _history.FirstOrDefault(m => m.Role == ChatRole.System);
        _history.Clear();
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
