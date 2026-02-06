namespace IronHive.Agent.SubAgent;

/// <summary>
/// Configuration for sub-agents.
/// </summary>
public class SubAgentConfig
{
    /// <summary>
    /// Maximum nesting depth for sub-agents.
    /// Prevents infinite recursion.
    /// </summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>
    /// Maximum number of concurrent sub-agents.
    /// </summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>
    /// Configuration for Explore sub-agent.
    /// </summary>
    public ExploreAgentConfig Explore { get; set; } = new();

    /// <summary>
    /// Configuration for General sub-agent.
    /// </summary>
    public GeneralAgentConfig General { get; set; } = new();
}

/// <summary>
/// Configuration for Explore sub-agent (read-only).
/// </summary>
public class ExploreAgentConfig
{
    /// <summary>
    /// Maximum number of turns (API round-trips).
    /// </summary>
    public int MaxTurns { get; set; } = 10;

    /// <summary>
    /// Maximum context tokens.
    /// </summary>
    public int MaxTokens { get; set; } = 16_000;

    /// <summary>
    /// Tools allowed for Explore agent (read-only tools).
    /// </summary>
    public List<string> AllowedTools { get; set; } =
    [
        "read_file",
        "list_directory",
        "glob",
        "grep"
    ];
}

/// <summary>
/// Configuration for General sub-agent (full access).
/// </summary>
public class GeneralAgentConfig
{
    /// <summary>
    /// Maximum number of turns (API round-trips).
    /// </summary>
    public int MaxTurns { get; set; } = 30;

    /// <summary>
    /// Maximum context tokens.
    /// </summary>
    public int MaxTokens { get; set; } = 64_000;
}
