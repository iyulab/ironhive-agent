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

    /// <summary>
    /// Asynchronously gets the current conversation history.
    /// Override this in implementations that use async storage backends.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(History);

    /// <summary>
    /// Asynchronously clears the conversation history.
    /// Override this in implementations that use async storage backends.
    /// </summary>
    Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        ClearHistory();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously initializes the conversation history with existing messages.
    /// Override this in implementations that use async storage backends.
    /// </summary>
    Task InitializeHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        InitializeHistory(messages);
        return Task.CompletedTask;
    }
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
/// A streaming tool-call chunk carried inside <see cref="AgentResponseChunk.ToolCallDelta"/>.
/// </summary>
/// <remarks>
/// <para>
/// The built-in <see cref="AgentLoop"/> and <see cref="ThinkingAgentLoop"/> always emit chunks
/// with <see cref="IsComplete"/> = <c>true</c>, because the underlying
/// <c>Microsoft.Extensions.AI</c> chat client has already accumulated streaming fragments into
/// the materialised <see cref="Microsoft.Extensions.AI.FunctionCallContent.Arguments"/> dictionary
/// before forwarding it. Consumers can therefore parse
/// <see cref="ArgumentsDelta"/> directly as JSON in that common case.
/// </para>
/// <para>
/// The <c>Delta</c> suffix on <see cref="NameDelta"/> and <see cref="ArgumentsDelta"/> is kept so
/// future loop implementations can forward true provider deltas; in that case they must set
/// <see cref="IsComplete"/> to <c>false</c> on intermediate chunks and emit a final chunk
/// (with the same <see cref="Id"/>) with <see cref="IsComplete"/> = <c>true</c>.
/// </para>
/// <para>
/// Use <see cref="ToolCallChunkFactory.FromFunctionCall"/> to build chunks from
/// provider-produced <see cref="Microsoft.Extensions.AI.FunctionCallContent"/>; it encodes the
/// canonical "complete-in-one-chunk, JSON-serialised arguments" contract.
/// </para>
/// </remarks>
public record ToolCallChunk
{
    /// <summary>
    /// Stable identifier for the tool call. All chunks that belong to the same tool call
    /// — including intermediate deltas and the final completion chunk — share the same
    /// <see cref="Id"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Tool name. When <see cref="IsComplete"/> is <c>true</c> (the default, and the only
    /// mode produced by the built-in loops) this is the complete tool name. When
    /// <see cref="IsComplete"/> is <c>false</c> this may be a partial fragment that consumers
    /// must accumulate across chunks that share the same <see cref="Id"/>.
    /// </summary>
    public string? NameDelta { get; init; }

    /// <summary>
    /// Tool arguments serialized as JSON.
    /// <para>
    /// When <see cref="IsComplete"/> is <c>true</c> (the default, and the only mode produced
    /// by the built-in loops) this is the complete, parseable JSON for the tool call
    /// — consumers may feed it straight into <c>JsonDocument.Parse</c>.
    /// </para>
    /// <para>
    /// When <see cref="IsComplete"/> is <c>false</c> this is an opaque fragment of the
    /// provider's raw stream and consumers must accumulate across subsequent chunks
    /// (sharing the same <see cref="Id"/>) before attempting to parse.
    /// </para>
    /// </summary>
    public string? ArgumentsDelta { get; init; }

    /// <summary>
    /// Whether this chunk carries the complete tool call. When <c>true</c>,
    /// <see cref="NameDelta"/> is the final name and <see cref="ArgumentsDelta"/> is valid,
    /// fully-formed JSON (or <c>null</c> for calls with no arguments).
    /// When <c>false</c>, the chunk carries a partial fragment and consumers must
    /// accumulate further chunks with the same <see cref="Id"/>.
    /// Defaults to <c>true</c> so existing emitters remain source-compatible.
    /// </summary>
    public bool IsComplete { get; init; } = true;
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
