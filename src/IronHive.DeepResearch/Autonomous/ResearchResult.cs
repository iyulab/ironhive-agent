using Ironbees.Autonomous.Abstractions;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Wraps DeepResearch's ResearchResult as an ITaskResult for Autonomous orchestration.
/// </summary>
public record AutonomousResearchResult : ITaskResult
{
    /// <inheritdoc />
    public string RequestId { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Success { get; init; }

    /// <inheritdoc />
    public string Output { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? ErrorOutput { get; init; }

    /// <summary>
    /// The underlying DeepResearch result.
    /// </summary>
    public Models.Research.ResearchResult? InnerResult { get; init; }

    /// <summary>
    /// Creates an AutonomousResearchResult from a DeepResearch result.
    /// </summary>
    public static AutonomousResearchResult FromResearchResult(
        string requestId,
        Models.Research.ResearchResult result)
    {
        return new AutonomousResearchResult
        {
            RequestId = requestId,
            Success = !result.IsPartial,
            Output = result.Report,
            InnerResult = result
        };
    }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static AutonomousResearchResult Failed(string requestId, string error)
    {
        return new AutonomousResearchResult
        {
            RequestId = requestId,
            Success = false,
            Output = string.Empty,
            ErrorOutput = error
        };
    }
}
