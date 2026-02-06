namespace IronHive.Agent.SubAgent;

/// <summary>
/// Result of a sub-agent execution.
/// </summary>
public record SubAgentResult
{
    /// <summary>
    /// Context of the sub-agent that produced this result.
    /// </summary>
    public required SubAgentContext Context { get; init; }

    /// <summary>
    /// Whether the sub-agent completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The final response/output from the sub-agent.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Error message if the sub-agent failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Number of turns taken.
    /// </summary>
    public int TurnsUsed { get; init; }

    /// <summary>
    /// Number of tokens used.
    /// </summary>
    public int TokensUsed { get; init; }

    /// <summary>
    /// Execution duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static SubAgentResult Succeeded(
        SubAgentContext context,
        string output,
        int turnsUsed = 0,
        int tokensUsed = 0,
        TimeSpan duration = default)
    {
        return new SubAgentResult
        {
            Context = context,
            Success = true,
            Output = output,
            TurnsUsed = turnsUsed,
            TokensUsed = tokensUsed,
            Duration = duration
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static SubAgentResult Failed(
        SubAgentContext context,
        string error,
        int turnsUsed = 0,
        int tokensUsed = 0,
        TimeSpan duration = default)
    {
        return new SubAgentResult
        {
            Context = context,
            Success = false,
            Error = error,
            TurnsUsed = turnsUsed,
            TokensUsed = tokensUsed,
            Duration = duration
        };
    }
}
