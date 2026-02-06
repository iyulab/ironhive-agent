namespace IronHive.Agent.SubAgent;

/// <summary>
/// Service for spawning and managing sub-agents.
/// </summary>
public interface ISubAgentService
{
    /// <summary>
    /// Gets the current nesting depth (0 = top-level).
    /// </summary>
    int CurrentDepth { get; }

    /// <summary>
    /// Gets the number of currently running sub-agents.
    /// </summary>
    int RunningCount { get; }

    /// <summary>
    /// Checks if a new sub-agent can be spawned (respects depth and concurrency limits).
    /// </summary>
    bool CanSpawn(SubAgentType type);

    /// <summary>
    /// Spawns an Explore sub-agent for read-only tasks.
    /// </summary>
    /// <param name="task">The task to perform.</param>
    /// <param name="context">Optional additional context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the sub-agent execution.</returns>
    Task<SubAgentResult> ExploreAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns a General sub-agent for complex multi-step tasks.
    /// </summary>
    /// <param name="task">The task to perform.</param>
    /// <param name="context">Optional additional context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the sub-agent execution.</returns>
    Task<SubAgentResult> GeneralAsync(
        string task,
        string? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns a sub-agent of the specified type.
    /// </summary>
    /// <param name="agentContext">The sub-agent context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the sub-agent execution.</returns>
    Task<SubAgentResult> SpawnAsync(
        SubAgentContext agentContext,
        CancellationToken cancellationToken = default);
}
