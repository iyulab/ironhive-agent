using Ironbees.Autonomous.Abstractions;
using IronHive.DeepResearch.Orchestration;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Wraps ResearchOrchestrator as an ITaskExecutor for Autonomous orchestration.
/// Each ExecuteAsync call runs a full research pipeline (single iteration mode).
/// </summary>
public class ResearchTaskExecutor : ITaskExecutor<AutonomousResearchRequest, AutonomousResearchResult>
{
    private readonly ResearchOrchestrator _orchestrator;
    private readonly ILogger<ResearchTaskExecutor>? _logger;

    public ResearchTaskExecutor(
        ResearchOrchestrator orchestrator,
        ILogger<ResearchTaskExecutor>? logger = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = logger;
    }

    /// <summary>
    /// Executes a single research pipeline run.
    /// The Autonomous orchestrator handles iteration/retry logic.
    /// </summary>
    public async Task<AutonomousResearchResult> ExecuteAsync(
        AutonomousResearchRequest request,
        Action<TaskOutput>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger?.LogInformation("Research task executor starting: {Query}", request.Prompt);

            onOutput?.Invoke(new TaskOutput
            {
                RequestId = request.RequestId,
                Type = TaskOutputType.System,
                Content = $"Starting research: {request.Prompt}"
            });

            // Force single iteration for autonomous orchestrator control
            var singleIterationRequest = request.InnerRequest with { MaxIterations = 1 };
            var result = await _orchestrator.ExecuteAsync(singleIterationRequest, cancellationToken);

            onOutput?.Invoke(new TaskOutput
            {
                RequestId = request.RequestId,
                Type = TaskOutputType.Output,
                Content = $"Research completed: {result.Metadata.TotalSourcesAnalyzed} sources analyzed"
            });

            return AutonomousResearchResult.FromResearchResult(request.RequestId, result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Research task executor failed");

            onOutput?.Invoke(new TaskOutput
            {
                RequestId = request.RequestId,
                Type = TaskOutputType.Error,
                Content = $"Research failed: {ex.Message}"
            });

            return AutonomousResearchResult.Failed(request.RequestId, ex.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
