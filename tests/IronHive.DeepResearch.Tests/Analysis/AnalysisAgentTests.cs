using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Analysis;

public class AnalysisAgentTests
{
    private readonly AnalysisAgent _agent;
    private readonly MockTextGenerationService _mockTextService;
    private readonly DeepResearchOptions _options;

    public AnalysisAgentTests()
    {
        _mockTextService = new MockTextGenerationService();
        _options = new DeepResearchOptions
        {
            SufficiencyThreshold = 0.8m
        };

        _agent = new AnalysisAgent(
            _mockTextService,
            _options,
            NullLogger<AnalysisAgent>.Instance);
    }

    [Fact]
    public async Task AnalyzeAsync_WithSources_ExtractsFindings()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));
        state.CollectedSources.Add(CreateTestSource("src2", "https://example.com/2"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse
            {
                Findings =
                [
                    new ExtractedFinding
                    {
                        Claim = "Test finding 1",
                        Evidence = "Evidence quote",
                        Confidence = 0.8m
                    },
                    new ExtractedFinding
                    {
                        Claim = "Test finding 2",
                        Evidence = "Another evidence",
                        Confidence = 0.9m
                    }
                ]
            });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [], CoverageEstimate = 0.8m });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse
            {
                OverallScore = 0.85m,
                CoverageScore = 0.9m,
                QualityScore = 0.8m,
                SourceDiversityScore = 0.7m
            });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.Findings.Should().NotBeEmpty();
        result.SourcesAnalyzed.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_IdentifiesGaps()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse
            {
                Findings =
                [
                    new ExtractedFinding { Claim = "Some finding", Confidence = 0.7m }
                ]
            });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse
            {
                Gaps =
                [
                    new IdentifiedGap
                    {
                        Description = "Missing information about X",
                        SuggestedQuery = "X topic details",
                        Priority = "high"
                    },
                    new IdentifiedGap
                    {
                        Description = "Need more data on Y",
                        SuggestedQuery = "Y statistics",
                        Priority = "medium"
                    }
                ],
                CoverageEstimate = 0.5m
            });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.5m, CoverageScore = 0.5m, QualityScore = 0.6m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.Gaps.Should().HaveCount(2);
        result.Gaps[0].Priority.Should().Be(GapPriority.High);
        result.Gaps[0].SuggestedQuery.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_EvaluatesSufficiency()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse { Findings = [] });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [], CoverageEstimate = 0.9m });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse
            {
                OverallScore = 0.85m,
                CoverageScore = 0.9m,
                QualityScore = 0.8m,
                SourceDiversityScore = 0.85m,
                Reasoning = "Good coverage of topic",
                StrengthAreas = ["Topic A", "Topic B"],
                WeakAreas = ["Topic C"]
            });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.SufficiencyScore.Should().NotBeNull();
        result.SufficiencyScore.CoverageScore.Should().BeGreaterThan(0);
        result.SufficiencyScore.QualityScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeAsync_UpdatesState()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse
            {
                Findings =
                [
                    new ExtractedFinding { Claim = "New finding", Confidence = 0.85m }
                ]
            });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse
            {
                Gaps =
                [
                    new IdentifiedGap { Description = "Gap", SuggestedQuery = "query", Priority = "low" }
                ]
            });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.7m, CoverageScore = 0.7m, QualityScore = 0.7m });

        // Act
        await _agent.AnalyzeAsync(state);

        // Assert
        state.Findings.Should().NotBeEmpty();
        state.IdentifiedGaps.Should().NotBeEmpty();
        state.LastSufficiencyScore.Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_NeedsMoreResearch_WhenScoreIsBelowThreshold()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse { Findings = [] });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse
            {
                Gaps =
                [
                    new IdentifiedGap { Description = "Critical gap", SuggestedQuery = "query", Priority = "high" }
                ],
                CoverageEstimate = 0.3m
            });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.4m, CoverageScore = 0.3m, QualityScore = 0.5m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.NeedsMoreResearch.Should().BeTrue();
        result.SufficiencyScore.IsSufficient.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotNeedMoreResearch_WhenScoreIsHigh()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example1.com/1"));
        state.CollectedSources.Add(CreateTestSource("src2", "https://example2.com/2"));
        state.CollectedSources.Add(CreateTestSource("src3", "https://example3.com/3"));
        state.CollectedSources.Add(CreateTestSource("src4", "https://example4.com/4"));
        state.CollectedSources.Add(CreateTestSource("src5", "https://example5.com/5"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse
            {
                Findings =
                [
                    new ExtractedFinding { Claim = "Finding 1", Confidence = 0.9m },
                    new ExtractedFinding { Claim = "Finding 2", Confidence = 0.85m }
                ]
            });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [], CoverageEstimate = 0.95m });

        // 고득점 설정 (CalculateOverallScore가 가중 평균으로 계산)
        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.95m, CoverageScore = 0.95m, QualityScore = 0.95m, SourceDiversityScore = 0.9m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        // NeedsMoreResearch = !IsSufficient && Gaps.Count > 0
        // Gaps가 비어있으므로 NeedsMoreResearch는 항상 false
        result.NeedsMoreResearch.Should().BeFalse();
        // 점수가 충분히 높으면 IsSufficient도 true
        result.SufficiencyScore.OverallScore.Should().BeGreaterThanOrEqualTo(0.8m);
    }

    [Fact]
    public async Task AnalyzeAsync_LimitsSourcesAnalyzed()
    {
        // Arrange
        var state = CreateTestState();
        for (int i = 0; i < 30; i++)
        {
            state.CollectedSources.Add(CreateTestSource($"src{i}", $"https://example.com/{i}"));
        }

        var options = new AnalysisOptions { MaxSourcesToAnalyze = 10 };

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse { Findings = [] });
        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [] });
        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.8m, CoverageScore = 0.8m, QualityScore = 0.8m });

        // Act
        var result = await _agent.AnalyzeAsync(state, options);

        // Assert
        result.SourcesAnalyzed.Should().Be(10);
    }

    [Fact]
    public async Task AnalyzeAsync_HandlesLLMFailure_Gracefully()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupFailure(new InvalidOperationException("LLM unavailable"));

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.Findings.Should().BeEmpty();
        result.Gaps.Should().BeEmpty();
        result.SufficiencyScore.Should().NotBeNull(); // 기본 계산된 점수
    }

    [Fact]
    public async Task AnalyzeAsync_DeduplicatesFindings()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));
        state.CollectedSources.Add(CreateTestSource("src2", "https://example.com/2"));

        // 두 소스에서 처음 50자가 동일한 Finding 반환
        // 중복 제거 로직: 처음 50자로 판단
        var longClaim = "This is a very long finding claim that exceeds fifty characters and should be used for deduplication testing in the analysis agent";
        _mockTextService.SetupStructuredResponses<FindingExtractionResponse>(
        [
            new FindingExtractionResponse
            {
                Findings =
                [
                    new ExtractedFinding { Claim = longClaim, Confidence = 0.7m }
                ]
            },
            new FindingExtractionResponse
            {
                Findings =
                [
                    // 처음 50자가 동일 (중복으로 처리됨)
                    new ExtractedFinding { Claim = longClaim + " - variant from source 2", Confidence = 0.9m }
                ]
            }
        ]);

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [] });
        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.8m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        // 처음 50자가 동일하므로 중복 제거되어 1개만 남음 (높은 신뢰도가 우선)
        result.Findings.Should().HaveCount(1);
        result.Findings[0].VerificationScore.Should().Be(0.9m);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsCorrectDuration()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse { Findings = [] });
        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse { Gaps = [] });
        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.8m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.StartedAt.Should().BeBefore(result.CompletedAt);
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesGapPriority_Correctly()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src1", "https://example.com/1"));

        _mockTextService.SetupStructuredResponse<FindingExtractionResponse>(
            new FindingExtractionResponse { Findings = [] });

        _mockTextService.SetupStructuredResponse<GapAnalysisResponse>(
            new GapAnalysisResponse
            {
                Gaps =
                [
                    new IdentifiedGap { Description = "High priority", SuggestedQuery = "q1", Priority = "HIGH" },
                    new IdentifiedGap { Description = "Medium priority", SuggestedQuery = "q2", Priority = "Medium" },
                    new IdentifiedGap { Description = "Low priority", SuggestedQuery = "q3", Priority = "low" },
                    new IdentifiedGap { Description = "Unknown priority", SuggestedQuery = "q4", Priority = "unknown" }
                ]
            });

        _mockTextService.SetupStructuredResponse<SufficiencyEvaluationResponse>(
            new SufficiencyEvaluationResponse { OverallScore = 0.5m });

        // Act
        var result = await _agent.AnalyzeAsync(state);

        // Assert
        result.Gaps.Should().HaveCount(4);
        result.Gaps[0].Priority.Should().Be(GapPriority.High);
        result.Gaps[1].Priority.Should().Be(GapPriority.Medium);
        result.Gaps[2].Priority.Should().Be(GapPriority.Low);
        result.Gaps[3].Priority.Should().Be(GapPriority.Medium); // 알 수 없는 값은 Medium
    }

    private static ResearchState CreateTestState()
    {
        return new ResearchState
        {
            SessionId = Guid.NewGuid().ToString(),
            Request = new ResearchRequest
            {
                Query = "test query",
                Depth = ResearchDepth.Standard,
                Language = "ko"
            },
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    private static SourceDocument CreateTestSource(string id, string url)
    {
        return new SourceDocument
        {
            Id = id,
            Url = url,
            Title = $"Test Source {id}",
            Content = "This is test content for the source document.",
            RelevanceScore = 0.8,
            TrustScore = 0.7,
            Provider = "test",
            ExtractedAt = DateTimeOffset.UtcNow,
            PublishedDate = DateTimeOffset.UtcNow.AddDays(-7)
        };
    }
}

/// <summary>
/// 테스트용 Mock TextGenerationService
/// </summary>
internal class MockTextGenerationService : ITextGenerationService
{
    private readonly Dictionary<Type, Queue<object>> _structuredResponses = new();
    private Exception? _failure;
    private int _callCount;

    public int CallCount => _callCount;

    public void SetupStructuredResponse<T>(T response) where T : class
    {
        if (!_structuredResponses.ContainsKey(typeof(T)))
        {
            _structuredResponses[typeof(T)] = new Queue<object>();
        }
        _structuredResponses[typeof(T)].Enqueue(response);
    }

    public void SetupStructuredResponses<T>(IEnumerable<T> responses) where T : class
    {
        foreach (var response in responses)
        {
            SetupStructuredResponse(response);
        }
    }

    public void SetupFailure(Exception exception)
    {
        _failure = exception;
    }

    public Task<TextGenerationResult> GenerateAsync(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callCount++;

        if (_failure != null)
        {
            throw _failure;
        }

        return Task.FromResult(new TextGenerationResult
        {
            Text = "Generated text"
        });
    }

    public Task<T?> GenerateStructuredAsync<T>(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        _callCount++;

        if (_failure != null)
        {
            throw _failure;
        }

        if (_structuredResponses.TryGetValue(typeof(T), out var queue) && queue.Count > 0)
        {
            return Task.FromResult((T?)queue.Dequeue());
        }

        return Task.FromResult<T?>(null);
    }
}
