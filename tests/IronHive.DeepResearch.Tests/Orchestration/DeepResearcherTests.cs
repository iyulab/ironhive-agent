using FluentAssertions;
using IronHive.DeepResearch;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Content;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Report;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using IronHive.DeepResearch.Search;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Orchestration;

public class DeepResearcherTests
{
    private readonly DeepResearcher _researcher;
    private readonly MockResearchOrchestratorForFacade _mockOrchestrator;
    private readonly DeepResearchOptions _options;

    public DeepResearcherTests()
    {
        _options = new DeepResearchOptions();
        _mockOrchestrator = new MockResearchOrchestratorForFacade();

        _researcher = new DeepResearcher(
            _mockOrchestrator,
            _options,
            NullLogger<DeepResearcher>.Instance);
    }

    [Fact]
    public async Task ResearchAsync_CallsOrchestrator()
    {
        // Arrange
        var request = CreateTestRequest();
        _mockOrchestrator.SetupResult(CreateTestResult());

        // Act
        var result = await _researcher.ResearchAsync(request);

        // Assert
        result.Should().NotBeNull();
        _mockOrchestrator.ExecuteCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ResearchAsync_ReturnsOrchestratorResult()
    {
        // Arrange
        var request = CreateTestRequest();
        var expectedResult = CreateTestResult();
        _mockOrchestrator.SetupResult(expectedResult);

        // Act
        var result = await _researcher.ResearchAsync(request);

        // Assert
        result.SessionId.Should().Be(expectedResult.SessionId);
        result.Report.Should().Be(expectedResult.Report);
    }

    [Fact]
    public async Task ResearchStreamAsync_YieldsProgress()
    {
        // Arrange
        var request = CreateTestRequest();
        _mockOrchestrator.SetupStreamResult(CreateTestProgressSequence());

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _researcher.ResearchStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        progressList.Should().NotBeEmpty();
        _mockOrchestrator.ExecuteStreamCalled.Should().BeTrue();
    }

    [Fact]
    public async Task StartInteractiveAsync_ReturnsSession()
    {
        // Arrange
        var request = CreateTestRequest();

        // Act
        var session = await _researcher.StartInteractiveAsync(request);

        // Assert
        session.Should().NotBeNull();
        session.SessionId.Should().NotBeNullOrEmpty();
        session.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task StartInteractiveAsync_SessionHasInitialState()
    {
        // Arrange
        var request = CreateTestRequest();

        // Act
        var session = await _researcher.StartInteractiveAsync(request);

        // Assert
        session.CurrentState.Should().NotBeNull();
        session.CurrentState.Request.Query.Should().Be(request.Query);
    }

    [Fact]
    public async Task ResumeAsync_ThrowsForUnknownSession()
    {
        // Arrange
        var unknownSessionId = "unknown-session-id";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _researcher.ResumeAsync(unknownSessionId));
    }

    [Fact]
    public async Task ResumeAsync_ResumesExistingSession()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        _mockOrchestrator.SetupResult(CreateTestResult());

        // Act
        var result = await _researcher.ResumeAsync(session.SessionId);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Session_GetCheckpointAsync_ReturnsCheckpoint()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);

        // Act
        var checkpoint = await session.GetCheckpointAsync();

        // Assert
        checkpoint.Should().NotBeNull();
        checkpoint.SessionId.Should().Be(session.SessionId);
        checkpoint.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Session_AddQueryAsync_AddsQuery()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        var customQuery = "Custom follow-up query";

        // Act
        await session.AddQueryAsync(customQuery);

        // Assert
        session.CurrentState.ExecutedQueries.Should().Contain(q => q.Query == customQuery);
    }

    [Fact]
    public async Task Session_ContinueAsync_ThrowsWhenComplete()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        _mockOrchestrator.SetupResult(CreateTestResult());

        // Complete the session
        await session.FinalizeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ContinueAsync());
    }

    [Fact]
    public async Task Session_FinalizeAsync_ReturnsResult()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        _mockOrchestrator.SetupResult(CreateTestResult());

        // Act
        var result = await session.FinalizeAsync();

        // Assert
        result.Should().NotBeNull();
        session.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Session_FinalizeAsync_ThrowsWhenAlreadyComplete()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        _mockOrchestrator.SetupResult(CreateTestResult());

        await session.FinalizeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.FinalizeAsync());
    }

    [Fact]
    public async Task Session_DisposeAsync_DisposesSession()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);

        // Act
        await session.DisposeAsync();

        // Assert - 두 번 dispose해도 오류 없이 작동
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Session_AddQueryAsync_ThrowsWhenComplete()
    {
        // Arrange
        var request = CreateTestRequest();
        var session = await _researcher.StartInteractiveAsync(request);
        _mockOrchestrator.SetupResult(CreateTestResult());

        await session.FinalizeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.AddQueryAsync("New query"));
    }

    private static ResearchRequest CreateTestRequest()
    {
        return new ResearchRequest
        {
            Query = "Test research query",
            Depth = ResearchDepth.Standard,
            Language = "ko"
        };
    }

    private static ResearchResult CreateTestResult()
    {
        return new ResearchResult
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Query = "Test research query",
            Report = "# Test Report\n\nContent",
            Sections =
            [
                new ReportSection
                {
                    Title = "Introduction",
                    Content = "Content",
                    Order = 1
                }
            ],
            CitedSources = [],
            UncitedSources = [],
            Citations = [],
            ThinkingProcess = [],
            Metadata = new ResearchMetadata
            {
                IterationCount = 1,
                TotalQueriesExecuted = 1,
                TotalSourcesAnalyzed = 1,
                Duration = TimeSpan.FromSeconds(5),
                TokenUsage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
                EstimatedCost = 0.01m,
                FinalSufficiencyScore = new SufficiencyScore
                {
                    OverallScore = 0.9m,
                    CoverageScore = 0.9m,
                    SourceDiversityScore = 0.85m,
                    QualityScore = 0.9m,
                    FreshnessScore = 0.85m,
                    EvaluatedAt = DateTimeOffset.UtcNow
                }
            },
            IsPartial = false
        };
    }

    private static IEnumerable<ResearchProgress> CreateTestProgressSequence()
    {
        yield return new ResearchProgress
        {
            Type = ProgressType.Started,
            CurrentIteration = 1,
            MaxIterations = 3,
            Timestamp = DateTimeOffset.UtcNow
        };

        yield return new ResearchProgress
        {
            Type = ProgressType.PlanGenerated,
            CurrentIteration = 1,
            MaxIterations = 3,
            Timestamp = DateTimeOffset.UtcNow,
            Plan = new PlanProgress
            {
                GeneratedQueries = ["Query 1"],
                ResearchAngles = ["Angle 1"]
            }
        };

        yield return new ResearchProgress
        {
            Type = ProgressType.Completed,
            CurrentIteration = 1,
            MaxIterations = 3,
            Timestamp = DateTimeOffset.UtcNow,
            Result = CreateTestResult()
        };
    }
}

#region Mock Orchestrator

internal class MockResearchOrchestratorForFacade : ResearchOrchestrator
{
    private ResearchResult? _result;
    private IEnumerable<ResearchProgress>? _streamResult;

    public bool ExecuteCalled { get; private set; }
    public bool ExecuteStreamCalled { get; private set; }

    public MockResearchOrchestratorForFacade() : base(
        CreateMockQueryPlanner(),
        CreateMockSearchCoordinator(),
        CreateMockContentEnrichment(),
        CreateMockAnalysisAgent(),
        CreateMockReportGenerator(),
        new DeepResearchOptions(),
        NullLogger<ResearchOrchestrator>.Instance)
    {
    }

    public void SetupResult(ResearchResult result) => _result = result;
    public void SetupStreamResult(IEnumerable<ResearchProgress> result) => _streamResult = result;

    public override Task<ResearchResult> ExecuteAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ExecuteCalled = true;

        return Task.FromResult(_result ?? new ResearchResult
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Query = "Default query",
            Report = "Default report",
            Sections = [],
            CitedSources = [],
            UncitedSources = [],
            Citations = [],
            ThinkingProcess = [],
            Metadata = new ResearchMetadata
            {
                IterationCount = 1,
                TotalQueriesExecuted = 1,
                TotalSourcesAnalyzed = 1,
                Duration = TimeSpan.FromSeconds(1),
                TokenUsage = new TokenUsage(),
                EstimatedCost = 0,
                FinalSufficiencyScore = new SufficiencyScore()
            },
            IsPartial = false
        });
    }

    public override async IAsyncEnumerable<ResearchProgress> ExecuteStreamAsync(
        ResearchRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ExecuteStreamCalled = true;

        if (_streamResult != null)
        {
            foreach (var progress in _streamResult)
            {
                yield return progress;
            }
        }
        else
        {
            yield return new ResearchProgress
            {
                Type = ProgressType.Started,
                CurrentIteration = 1,
                MaxIterations = 1,
                Timestamp = DateTimeOffset.UtcNow
            };

            yield return new ResearchProgress
            {
                Type = ProgressType.Completed,
                CurrentIteration = 1,
                MaxIterations = 1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        await Task.CompletedTask;
    }

    private static QueryPlannerAgent CreateMockQueryPlanner()
    {
        return new QueryPlannerAgent(
            new MockQueryExpanderForFacade(),
            NullLogger<QueryPlannerAgent>.Instance);
    }

    private static SearchCoordinatorAgent CreateMockSearchCoordinator()
    {
        return new SearchCoordinatorAgent(
            new SearchProviderFactory([], new DeepResearchOptions(), NullLogger<SearchProviderFactory>.Instance),
            new DeepResearchOptions(),
            NullLogger<SearchCoordinatorAgent>.Instance);
    }

    private static ContentEnrichmentAgent CreateMockContentEnrichment()
    {
        return new ContentEnrichmentAgent(
            new MockContentExtractorForFacade(),
            new ContentChunker(NullLogger<ContentChunker>.Instance),
            new DeepResearchOptions(),
            NullLogger<ContentEnrichmentAgent>.Instance);
    }

    private static AnalysisAgent CreateMockAnalysisAgent()
    {
        return new AnalysisAgent(
            new MockTextGenerationServiceForFacade(),
            new DeepResearchOptions(),
            NullLogger<AnalysisAgent>.Instance);
    }

    private static ReportGeneratorAgent CreateMockReportGenerator()
    {
        return new ReportGeneratorAgent(
            new MockTextGenerationServiceForFacade(),
            new DeepResearchOptions(),
            NullLogger<ReportGeneratorAgent>.Instance);
    }
}

internal class MockQueryExpanderForFacade : IQueryExpander
{
    public Task<IReadOnlyList<SubQuestion>> DecomposeQueryAsync(
        string query, QueryExpansionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SubQuestion>>([]);
    }

    public Task<IReadOnlyList<ResearchPerspective>> DiscoverPerspectivesAsync(
        string query, QueryExpansionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ResearchPerspective>>([]);
    }

    public Task<IReadOnlyList<ExpandedQuery>> ExpandQueriesAsync(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions,
        IReadOnlyList<ResearchPerspective> perspectives,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ExpandedQuery>>([]);
    }
}

internal class MockContentExtractorForFacade : IContentExtractor
{
    public Task<ExtractedContent> ExtractAsync(
        string url, ContentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ExtractedContent
        {
            Url = url,
            Title = "Test",
            Content = "Content",
            Success = true,
            ExtractedAt = DateTimeOffset.UtcNow
        });
    }

    public Task<IReadOnlyList<ExtractedContent>> ExtractBatchAsync(
        IEnumerable<string> urls, ContentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var results = urls.Select(url => new ExtractedContent
        {
            Url = url,
            Title = "Test",
            Content = "Content",
            Success = true,
            ExtractedAt = DateTimeOffset.UtcNow
        }).ToList();

        return Task.FromResult<IReadOnlyList<ExtractedContent>>(results);
    }
}

internal class MockTextGenerationServiceForFacade : ITextGenerationService
{
    public Task<TextGenerationResult> GenerateAsync(
        string prompt, TextGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TextGenerationResult { Text = "Generated" });
    }

    public Task<T?> GenerateStructuredAsync<T>(
        string prompt, TextGenerationOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        return Task.FromResult<T?>(null);
    }
}

#endregion
