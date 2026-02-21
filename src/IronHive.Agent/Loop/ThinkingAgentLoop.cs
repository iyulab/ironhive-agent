using System.Runtime.CompilerServices;
using System.Text;
using IronHive.Agent.Context;
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
    private readonly List<ChatMessage> _history = [];

    public ThinkingAgentLoop(
        IChatClient chatClient,
        IThinkingTurnManager turnManager,
        AgentOptions? options = null,
        ThinkingChatClientOptions? thinkingOptions = null,
        IUsageTracker? usageTracker = null,
        ContextManager? contextManager = null,
        IToolRetriever? toolRetriever = null)
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
        _toolRetriever = toolRetriever;

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
    public async Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        _history.Add(new ChatMessage(ChatRole.User, prompt));

        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        var chatOptions = await CreateChatOptionsAsync(cancellationToken);
        var response = await _thinkingClient.GetResponseAsync(historyToSend, chatOptions, cancellationToken);

        // Add assistant response to history
        _history.AddRange(response.Messages);

        var toolCalls = ExtractToolCalls(response);
        var thinkingContent = ExtractThinkingContent(response);
        var usage = MapUsage(response.Usage);

        // Record usage for session tracking
        if (usage is not null)
        {
            _usageTracker?.Record(usage);
        }

        return new AgentResponse
        {
            Content = response.Text ?? string.Empty,
            ToolCalls = toolCalls,
            Usage = usage,
            ThinkingContent = thinkingContent
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        _history.Add(new ChatMessage(ChatRole.User, prompt));

        // Set goal from first user message if context manager is present
        _contextManager?.SetGoalFromHistory(_history);

        // Prepare history (compact if needed, inject goal reminder)
        var historyToSend = await PrepareHistoryForSendingAsync(cancellationToken);

        var chatOptions = await CreateChatOptionsAsync(cancellationToken);
        var responseBuilder = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();

        await foreach (var update in _thinkingClient.GetStreamingResponseAsync(historyToSend, chatOptions, cancellationToken))
        {
            // Extract thinking content from AdditionalProperties if available
            var thinkingDelta = ExtractStreamingThinking(update);
            if (!string.IsNullOrEmpty(thinkingDelta))
            {
                yield return new AgentResponseChunk
                {
                    ThinkingDelta = thinkingDelta
                };
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                responseBuilder.Append(update.Text);
                yield return new AgentResponseChunk
                {
                    TextDelta = update.Text
                };
            }

            if (update.Contents.OfType<FunctionCallContent>().Any())
            {
                foreach (var functionCall in update.Contents.OfType<FunctionCallContent>())
                {
                    toolCalls.Add(functionCall);
                    yield return new AgentResponseChunk
                    {
                        ToolCallDelta = new ToolCallChunk
                        {
                            Id = functionCall.CallId,
                            NameDelta = functionCall.Name,
                            ArgumentsDelta = functionCall.Arguments?.ToString()
                        }
                    };
                }
            }
        }

        // Add complete assistant response to history for multi-turn conversations
        var assistantMessage = new ChatMessage(ChatRole.Assistant, responseBuilder.ToString());
        if (toolCalls.Count > 0)
        {
            foreach (var toolCall in toolCalls)
            {
                assistantMessage.Contents.Add(toolCall);
            }
        }
        _history.Add(assistantMessage);
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

    private static string? ExtractStreamingThinking(ChatResponseUpdate update)
    {
        // Try to extract thinking from AdditionalProperties
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

    private async Task<ChatOptions> CreateChatOptionsAsync(CancellationToken cancellationToken)
    {
        var tools = _options.Tools;

        // Dynamic tool retrieval (select relevant tools for the query)
        if (_toolRetriever is not null && tools is { Count: > 0 })
        {
            var query = GetLatestUserQuery();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var result = await _toolRetriever.RetrieveAsync(
                    query, tools, _options.ToolRetrievalOptions, cancellationToken);
                tools = result.SelectedTools;
            }
        }

        return new ChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.MaxTokens,
            Tools = tools
        };
    }

    private string GetLatestUserQuery()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == ChatRole.User)
            {
                return _history[i].Text ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static List<ToolCallResult> ExtractToolCalls(ChatResponse response)
    {
        var results = new List<ToolCallResult>();

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents.OfType<FunctionCallContent>())
            {
                results.Add(new ToolCallResult
                {
                    ToolName = content.Name,
                    Arguments = content.Arguments?.ToString() ?? "{}",
                    Result = string.Empty,
                    Success = true
                });
            }
        }

        return results;
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

        return null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _thinkingClient.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static TokenUsage? MapUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new TokenUsage
        {
            InputTokens = usage.InputTokenCount ?? 0,
            OutputTokens = usage.OutputTokenCount ?? 0
        };
    }

    /// <summary>
    /// Clears the conversation history.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();

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
