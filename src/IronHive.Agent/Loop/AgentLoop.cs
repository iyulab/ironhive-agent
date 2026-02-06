using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IronHive.Agent.Context;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;

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
    private readonly List<ChatMessage> _history = [];

    public AgentLoop(
        IChatClient chatClient,
        AgentOptions? options = null,
        IUsageTracker? usageTracker = null,
        ContextManager? contextManager = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? new AgentOptions();
        _usageTracker = usageTracker;
        _contextManager = contextManager;

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

        var chatOptions = CreateChatOptions();
        var response = await _chatClient.GetResponseAsync(historyToSend, chatOptions, cancellationToken);

        // Add assistant response to history
        _history.AddRange(response.Messages);

        var toolCalls = ExtractToolCalls(response);
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
            Usage = usage
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

        var chatOptions = CreateChatOptions();
        var responseBuilder = new StringBuilder();
        var toolCalls = new List<FunctionCallContent>();

        await foreach (var update in _chatClient.GetStreamingResponseAsync(historyToSend, chatOptions, cancellationToken))
        {
            // Yield and collect text content
            if (!string.IsNullOrEmpty(update.Text))
            {
                responseBuilder.Append(update.Text);
                yield return new AgentResponseChunk
                {
                    TextDelta = update.Text
                };
            }

            // Yield and collect tool call updates
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
                            ArgumentsDelta = functionCall.Arguments is not null
                                ? JsonSerializer.Serialize(functionCall.Arguments)
                                : null
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

        // If history was compacted, update our internal history
        if (preparedHistory.Count < _history.Count)
        {
            _history.Clear();
            _history.AddRange(preparedHistory);
        }

        return preparedHistory;
    }

    private ChatOptions CreateChatOptions()
    {
        return new ChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.MaxTokens,
            Tools = _options.Tools
        };
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
                    Arguments = content.Arguments is not null
                        ? JsonSerializer.Serialize(content.Arguments)
                        : "{}",
                    Result = string.Empty, // Will be filled after execution
                    Success = true
                });
            }
        }

        return results;
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

    /// <summary>
    /// Gets the context manager if one is configured.
    /// </summary>
    public ContextManager? ContextManager => _contextManager;

    /// <summary>
    /// Gets the current context usage if context manager is configured.
    /// </summary>
    public ContextUsage? GetContextUsage()
    {
        return _contextManager?.GetUsage(_history);
    }

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

/// <summary>
/// Options for configuring the agent loop.
/// </summary>
public class AgentOptions
{
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
    /// Available tools for the agent.
    /// </summary>
    public IList<AITool>? Tools { get; set; }
}
