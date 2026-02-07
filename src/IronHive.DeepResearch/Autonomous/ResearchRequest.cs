using Ironbees.Autonomous.Abstractions;
using IronHive.DeepResearch.Models.Research;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Wraps DeepResearch's ResearchRequest as an ITaskRequest for Autonomous orchestration.
/// </summary>
public record AutonomousResearchRequest : ITaskRequest
{
    /// <inheritdoc />
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public string Prompt { get; init; } = string.Empty;

    /// <summary>
    /// The underlying DeepResearch request.
    /// </summary>
    public required Models.Research.ResearchRequest InnerRequest { get; init; }

    /// <summary>
    /// Creates an AutonomousResearchRequest from a DeepResearch request.
    /// </summary>
    public static AutonomousResearchRequest FromResearchRequest(Models.Research.ResearchRequest request)
    {
        return new AutonomousResearchRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Prompt = request.Query,
            InnerRequest = request
        };
    }
}
