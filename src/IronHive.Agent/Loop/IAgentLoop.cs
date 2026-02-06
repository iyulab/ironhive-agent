using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Agent loop interface for handling conversation cycles.
/// Implements "nO" style single-threaded master loop pattern.
/// </summary>
public interface IAgentLoop
{
    /// <summary>
    /// Runs the agent loop with the given prompt.
    /// </summary>
    /// <param name="prompt">User input prompt</param>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
    /// <returns>Agent response</returns>
    Task<AgentResponse> RunAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the agent loop with streaming output.
    /// </summary>
    /// <param name="prompt">User input prompt</param>
    /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
    /// <returns>Async enumerable of response chunks</returns>
    IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the conversation history with existing messages.
    /// Used for session restoration/resumption.
    /// </summary>
    /// <param name="messages">Messages to initialize the history with</param>
    void InitializeHistory(IEnumerable<ChatMessage> messages);

    /// <summary>
    /// Gets the current conversation history.
    /// </summary>
    IReadOnlyList<ChatMessage> History { get; }

    /// <summary>
    /// Clears the conversation history (keeps system prompt if configured).
    /// </summary>
    void ClearHistory();
}

/// <summary>
/// Agent response containing the result of a conversation turn.
/// </summary>
public record AgentResponse
{
    /// <summary>
    /// The text content of the response.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Tool calls made during this turn.
    /// </summary>
    public IReadOnlyList<ToolCallResult> ToolCalls { get; init; } = [];

    /// <summary>
    /// Token usage statistics.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Thinking/reasoning content extracted from the response (if available).
    /// </summary>
    public ThinkingContent? ThinkingContent { get; init; }
}

/// <summary>
/// Thinking/reasoning content from the LLM's chain-of-thought process.
/// </summary>
public record ThinkingContent
{
    /// <summary>
    /// The thinking/reasoning text content.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Estimated token count for the thinking content.
    /// </summary>
    public int? TokenCount { get; init; }
}

/// <summary>
/// Streaming response chunk.
/// </summary>
public record AgentResponseChunk
{
    /// <summary>
    /// Text content chunk.
    /// </summary>
    public string? TextDelta { get; init; }

    /// <summary>
    /// Thinking/reasoning content chunk.
    /// Only available when using models that support extended thinking.
    /// </summary>
    public string? ThinkingDelta { get; init; }

    /// <summary>
    /// Tool call in progress.
    /// </summary>
    public ToolCallChunk? ToolCallDelta { get; init; }

    /// <summary>
    /// Final token usage (only set on last chunk).
    /// </summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Result of a tool call execution.
/// </summary>
public record ToolCallResult
{
    /// <summary>
    /// Name of the tool that was called.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Arguments passed to the tool.
    /// </summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// Result returned by the tool.
    /// </summary>
    public required string Result { get; init; }

    /// <summary>
    /// Whether the tool call succeeded.
    /// </summary>
    public bool Success { get; init; } = true;
}

/// <summary>
/// Streaming tool call chunk.
/// </summary>
public record ToolCallChunk
{
    /// <summary>
    /// Tool call ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Tool name (may be partial during streaming).
    /// </summary>
    public string? NameDelta { get; init; }

    /// <summary>
    /// Arguments delta (partial JSON).
    /// </summary>
    public string? ArgumentsDelta { get; init; }
}

/// <summary>
/// Token usage statistics.
/// </summary>
public record TokenUsage
{
    /// <summary>
    /// Number of tokens in the input/prompt.
    /// </summary>
    public long InputTokens { get; init; }

    /// <summary>
    /// Number of tokens in the output/completion.
    /// </summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Total tokens used.
    /// </summary>
    public long TotalTokens => InputTokens + OutputTokens;
}
