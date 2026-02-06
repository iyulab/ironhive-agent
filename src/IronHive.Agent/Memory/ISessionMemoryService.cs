namespace IronHive.Agent.Memory;

/// <summary>
/// Session-aware memory service for agents.
/// Wraps MemoryIndexer's IMemoryService with session management.
/// </summary>
public interface ISessionMemoryService
{
    /// <summary>
    /// Gets the current session ID.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Gets the current user ID.
    /// </summary>
    string UserId { get; }

    /// <summary>
    /// Remembers a user message in the current session.
    /// </summary>
    /// <param name="content">The message content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RememberUserMessageAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers an assistant message in the current session.
    /// </summary>
    /// <param name="content">The message content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RememberAssistantMessageAsync(string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalls relevant memories for a query.
    /// </summary>
    /// <param name="query">The query to search for</param>
    /// <param name="limit">Maximum number of memories to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Relevant memory context</returns>
    Task<MemoryRecallResult> RecallAsync(string query, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a new session.
    /// </summary>
    /// <param name="sessionId">Optional session ID. If not provided, a new ID is generated.</param>
    void StartSession(string? sessionId = null);

    /// <summary>
    /// Ends the current session and triggers memory consolidation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EndSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all memories for the current session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a memory recall operation.
/// </summary>
public record MemoryRecallResult
{
    /// <summary>
    /// User-scoped memories (cross-session facts).
    /// </summary>
    public IReadOnlyList<MemoryItem> UserMemories { get; init; } = [];

    /// <summary>
    /// Session-scoped memories (current session context).
    /// </summary>
    public IReadOnlyList<MemoryItem> SessionMemories { get; init; } = [];

    /// <summary>
    /// Total number of memories found.
    /// </summary>
    public int TotalCount => UserMemories.Count + SessionMemories.Count;

    /// <summary>
    /// Formats all memories as context for the LLM.
    /// </summary>
    public string FormatAsContext()
    {
        if (TotalCount == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (UserMemories.Count > 0)
        {
            parts.Add("## User Knowledge\n" + string.Join("\n", UserMemories.Select(m => $"- {m.Content}")));
        }

        if (SessionMemories.Count > 0)
        {
            parts.Add("## Session Context\n" + string.Join("\n", SessionMemories.Select(m => $"- [{m.Role}] {m.Content}")));
        }

        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// A single memory item.
/// </summary>
public record MemoryItem
{
    /// <summary>
    /// The memory content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The role (user, assistant, system).
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// Relevance score (0-1).
    /// </summary>
    public float Score { get; init; }

    /// <summary>
    /// When the memory was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
