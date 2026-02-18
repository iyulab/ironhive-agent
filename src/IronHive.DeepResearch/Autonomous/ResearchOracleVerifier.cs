using Ironbees.Autonomous.Abstractions;
using Ironbees.Autonomous.Models;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Autonomous;

/// <summary>
/// Adapts DeepResearch's sufficiency evaluation logic as an IOracleVerifier
/// for Autonomous orchestration.
/// Uses the research result's sufficiency metadata to determine completion.
/// </summary>
public partial class ResearchOracleVerifier : IOracleVerifier
{
    private readonly ILogger<ResearchOracleVerifier>? _logger;

    public ResearchOracleVerifier(ILogger<ResearchOracleVerifier>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public Task<OracleVerdict> VerifyAsync(
        string originalPrompt,
        string executionOutput,
        OracleConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        // Parse the research result metadata from the execution output
        // The ResearchTaskExecutor returns the report as output.
        // We evaluate based on the output quality heuristics.

        if (string.IsNullOrWhiteSpace(executionOutput))
        {
            if (_logger is not null)
            {
                LogResearchProducedEmptyOutput(_logger);
            }

            return Task.FromResult(OracleVerdict.ContinueToNextIteration(
                "Research produced empty output, retry needed.",
                confidence: 0.1));
        }

        // Heuristic evaluation based on report content
        var reportLength = executionOutput.Length;
        var hasSections = executionOutput.Contains('#');
        var hasReferences = executionOutput.Contains('[') && executionOutput.Contains(']');

        var confidence = CalculateConfidence(reportLength, hasSections, hasReferences);

        if (confidence >= 0.8)
        {
            if (_logger is not null)
            {
                LogResearchSufficient(_logger, confidence);
            }

            return Task.FromResult(OracleVerdict.GoalAchieved(
                $"Research report is comprehensive: {reportLength} chars, has sections: {hasSections}, has references: {hasReferences}",
                confidence));
        }

        if (confidence >= 0.5)
        {
            if (_logger is not null)
            {
                LogResearchPartiallySufficient(_logger, confidence);
            }

            return Task.FromResult(OracleVerdict.ContinueToNextIteration(
                $"Research partially complete: {reportLength} chars. Need more depth or sources.",
                confidence));
        }

        if (_logger is not null)
        {
            LogResearchInsufficient(_logger, confidence);
        }

        return Task.FromResult(OracleVerdict.ContinueToNextIteration(
            $"Research insufficient: short report ({reportLength} chars). Need significantly more content.",
            confidence));
    }

    /// <inheritdoc />
    public string BuildVerificationPrompt(string originalPrompt, string executionOutput, OracleConfig? config = null)
    {
        return $"""
            Evaluate whether this research adequately answers the query.

            Query: {originalPrompt}

            Report length: {executionOutput?.Length ?? 0} characters
            Has structured sections: {executionOutput?.Contains('#') ?? false}
            Has citations: {executionOutput?.Contains('[') ?? false}
            """;
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Warning, Message = "Research produced empty output")]
    private static partial void LogResearchProducedEmptyOutput(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research sufficient (confidence: {Confidence:P0})")]
    private static partial void LogResearchSufficient(ILogger logger, double confidence);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research partially sufficient (confidence: {Confidence:P0}), continuing")]
    private static partial void LogResearchPartiallySufficient(ILogger logger, double confidence);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research insufficient (confidence: {Confidence:P0}), continuing")]
    private static partial void LogResearchInsufficient(ILogger logger, double confidence);

    #endregion

    private static double CalculateConfidence(int reportLength, bool hasSections, bool hasReferences)
    {
        var score = 0.0;

        // Length scoring
        score += reportLength switch
        {
            > 5000 => 0.4,
            > 2000 => 0.3,
            > 500 => 0.2,
            > 100 => 0.1,
            _ => 0.0
        };

        // Structure scoring
        if (hasSections)
        {
            score += 0.3;
        }

        // References scoring
        if (hasReferences)
        {
            score += 0.3;
        }

        return Math.Min(score, 1.0);
    }
}
