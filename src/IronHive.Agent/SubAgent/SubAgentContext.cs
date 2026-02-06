namespace IronHive.Agent.SubAgent;

/// <summary>
/// Execution context for a sub-agent.
/// </summary>
public record SubAgentContext
{
    /// <summary>
    /// Unique identifier for this sub-agent execution.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Type of sub-agent.
    /// </summary>
    public required SubAgentType Type { get; init; }

    /// <summary>
    /// The task to perform.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional additional context provided by the parent agent.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// Current nesting depth (0 = top-level agent, 1 = first sub-agent, etc.).
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// Parent agent ID (null for top-level).
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Working directory for this sub-agent.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Maximum number of turns allowed.
    /// </summary>
    public int MaxTurns { get; init; }

    /// <summary>
    /// Maximum tokens for context window.
    /// </summary>
    public int MaxTokens { get; init; }

    /// <summary>
    /// Creates a new sub-agent context with a generated ID.
    /// </summary>
    public static SubAgentContext Create(
        SubAgentType type,
        string task,
        string? additionalContext = null,
        int depth = 1,
        string? parentId = null,
        string? workingDirectory = null)
    {
        var (maxTurns, maxTokens) = type switch
        {
            SubAgentType.Explore => (10, 16_000),
            SubAgentType.General => (30, 64_000),
            _ => (10, 16_000)
        };

        return new SubAgentContext
        {
            Id = $"subagent-{Guid.NewGuid():N}",
            Type = type,
            Task = task,
            AdditionalContext = additionalContext,
            Depth = depth,
            ParentId = parentId,
            WorkingDirectory = workingDirectory,
            MaxTurns = maxTurns,
            MaxTokens = maxTokens
        };
    }
}
