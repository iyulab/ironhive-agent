using Ironbees.Autonomous;
using Ironbees.Autonomous.Abstractions;
using IronHive.DeepResearch.Models.Research;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Wires ResearchTaskExecutor + ResearchOracleVerifier into an AutonomousOrchestrator
/// for oracle-driven iterative research with automatic sufficiency checking.
/// </summary>
public partial class AutonomousResearchRunner
{
    private readonly ResearchTaskExecutor _executor;
    private readonly ResearchOracleVerifier _verifier;
    private readonly ILogger<AutonomousResearchRunner>? _logger;

    public AutonomousResearchRunner(
        ResearchTaskExecutor executor,
        ResearchOracleVerifier verifier,
        ILogger<AutonomousResearchRunner>? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _logger = logger;
    }

    /// <summary>
    /// Executes research using AutonomousOrchestrator with oracle-driven iteration.
    /// The orchestrator calls ResearchTaskExecutor (single iteration) repeatedly,
    /// with ResearchOracleVerifier evaluating sufficiency after each iteration.
    /// </summary>
    public async Task<ResearchResult> ExecuteAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var maxIterations = GetMaxIterations(request);
        if (_logger is not null)
        {
            LogAutonomousResearchStarting(_logger, request.Query, maxIterations);
        }

        // Wrap executor to capture typed results (AutonomousOrchestrator only exposes string output)
        var capturingExecutor = new ResultCapturingExecutor(_executor);

        var orchestrator = new AutonomousOrchestratorBuilder<AutonomousResearchRequest, AutonomousResearchResult>()
            .WithExecutor(capturingExecutor)
            .WithRequestFactory((requestId, prompt) => new AutonomousResearchRequest
            {
                RequestId = requestId,
                Prompt = prompt,
                InnerRequest = request
            })
            .WithOracle(_verifier)
            .WithMaxIterations(maxIterations)
            .WithAutoContinue()
            .WithoutContext()
            .Build();

        orchestrator.EnqueuePrompt(request.Query);
        await orchestrator.StartAsync(cancellationToken: cancellationToken);

        var iterationCount = orchestrator.GetHistory().Count;
        if (_logger is not null)
        {
            LogAutonomousResearchCompleted(_logger, iterationCount);
        }

        // Return the last captured typed result
        if (capturingExecutor.LastResult?.InnerResult is { } innerResult)
        {
            return innerResult;
        }

        if (_logger is not null)
        {
            LogNoTypedResultCaptured(_logger);
        }

        return CreateEmptyResult(request);
    }

    private static int GetMaxIterations(ResearchRequest request)
    {
        return request.Depth switch
        {
            ResearchDepth.Quick => Math.Min(request.MaxIterations, 2),
            ResearchDepth.Standard => Math.Min(request.MaxIterations, 5),
            ResearchDepth.Comprehensive => Math.Min(request.MaxIterations, 10),
            _ => request.MaxIterations
        };
    }

    private static ResearchResult CreateEmptyResult(ResearchRequest request)
    {
        return new ResearchResult
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Query = request.Query,
            Report = "Research could not be completed.",
            Sections = [],
            CitedSources = [],
            UncitedSources = [],
            Citations = [],
            ThinkingProcess = [],
            Metadata = new ResearchMetadata
            {
                IterationCount = 0,
                TotalQueriesExecuted = 0,
                TotalSourcesAnalyzed = 0,
                Duration = TimeSpan.Zero,
                TokenUsage = new TokenUsage(),
                EstimatedCost = 0m,
                FinalSufficiencyScore = new SufficiencyScore()
            },
            Errors = [],
            IsPartial = true
        };
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "Autonomous research starting: {Query}, max iterations: {Max}")]
    private static partial void LogAutonomousResearchStarting(ILogger logger, string query, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autonomous research completed after {Iterations} iteration(s)")]
    private static partial void LogAutonomousResearchCompleted(ILogger logger, int iterations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No typed result captured, returning empty result")]
    private static partial void LogNoTypedResultCaptured(ILogger logger);

    #endregion

    /// <summary>
    /// Wraps ResearchTaskExecutor to capture the last typed result,
    /// since AutonomousOrchestrator only passes string output to the oracle.
    /// </summary>
    private sealed class ResultCapturingExecutor : ITaskExecutor<AutonomousResearchRequest, AutonomousResearchResult>
    {
        private readonly ResearchTaskExecutor _inner;

        public AutonomousResearchResult? LastResult { get; private set; }

        public ResultCapturingExecutor(ResearchTaskExecutor inner)
        {
            _inner = inner;
        }

        public async Task<AutonomousResearchResult> ExecuteAsync(
            AutonomousResearchRequest request,
            Action<TaskOutput>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.ExecuteAsync(request, onOutput, cancellationToken);
            LastResult = result;
            return result;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
