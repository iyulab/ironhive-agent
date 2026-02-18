using Ironbees.Autonomous.Abstractions;
using IronHive.DeepResearch.Orchestration;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Wraps ResearchOrchestrator as an ITaskExecutor for Autonomous orchestration.
/// Each ExecuteAsync call runs a full research pipeline (single iteration mode).
/// </summary>
public partial class ResearchTaskExecutor : ITaskExecutor<AutonomousResearchRequest, AutonomousResearchResult>
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
            if (_logger is not null)
            {
                LogResearchTaskExecutorStarting(_logger, request.Prompt);
            }

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
            if (_logger is not null)
            {
                LogResearchTaskExecutorFailed(_logger, ex);
            }

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
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "Research task executor starting: {Query}")]
    private static partial void LogResearchTaskExecutorStarting(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Error, Message = "Research task executor failed")]
    private static partial void LogResearchTaskExecutorFailed(ILogger logger, Exception? exception);

    #endregion
}
