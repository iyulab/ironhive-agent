namespace IronHive.Agent.SubAgent;

/// <summary>
/// Types of sub-agents available.
/// </summary>
public enum SubAgentType
{
    /// <summary>
    /// Explore sub-agent: read-only, for searching and understanding code.
    /// Limited tools: read_file, list_directory, glob, grep.
    /// </summary>
    Explore,

    /// <summary>
    /// General sub-agent: full tool access for complex multi-step tasks.
    /// </summary>
    General
}
