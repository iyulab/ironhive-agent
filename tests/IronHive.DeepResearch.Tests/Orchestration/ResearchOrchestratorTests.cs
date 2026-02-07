using FluentAssertions;
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

public class ResearchOrchestratorTests
{
    private readonly ResearchOrchestrator _orchestrator;
    private readonly MockQueryPlannerAgentForOrchestrator _mockQueryPlanner;
    private readonly MockSearchCoordinatorAgentForOrchestrator _mockSearchCoordinator;
    private readonly MockContentEnrichmentAgentForOrchestrator _mockContentEnrichment;
    private readonly MockAnalysisAgentForOrchestrator _mockAnalysisAgent;
    private readonly MockReportGeneratorAgentForOrchestrator _mockReportGenerator;
    private readonly DeepResearchOptions _options;

    public ResearchOrchestratorTests()
    {
        _options = new DeepResearchOptions();
        _mockQueryPlanner = new MockQueryPlannerAgentForOrchestrator();
        _mockSearchCoordinator = new MockSearchCoordinatorAgentForOrchestrator();
        _mockContentEnrichment = new MockContentEnrichmentAgentForOrchestrator();
        _mockAnalysisAgent = new MockAnalysisAgentForOrchestrator();
        _mockReportGenerator = new MockReportGeneratorAgentForOrchestrator();

        _orchestrator = new ResearchOrchestrator(
            _mockQueryPlanner,
            _mockSearchCoordinator,
            _mockContentEnrichment,
            _mockAnalysisAgent,
            _mockReportGenerator,
            _options,
            NullLogger<ResearchOrchestrator>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResult()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.SessionId.Should().NotBeNullOrEmpty();
        result.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ExecutesAllPhases()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        await _orchestrator.ExecuteAsync(request);

        // Assert
        _mockQueryPlanner.PlanCalled.Should().BeTrue();
        _mockSearchCoordinator.ExecuteSearchesCalled.Should().BeTrue();
        _mockContentEnrichment.EnrichCalled.Should().BeTrue();
        _mockAnalysisAgent.AnalyzeCalled.Should().BeTrue();
        _mockReportGenerator.GenerateReportCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_IteratesUntilSufficient()
    {
        // Arrange
        var request = CreateTestRequest();
        _mockQueryPlanner.SetupPlan(CreateTestPlanResult());
        _mockSearchCoordinator.SetupSearchResult(CreateTestSearchResult());
        _mockContentEnrichment.SetupEnrichmentResult(CreateTestEnrichmentResult());
        _mockReportGenerator.SetupReportResult(CreateTestReportResult());

        // 첫 번째 반복: 부족, 두 번째 반복: 충분
        _mockAnalysisAgent.SetupAnalysisResults([
            CreateAnalysisResult(needsMore: true, score: 0.5m),
            CreateAnalysisResult(needsMore: false, score: 0.9m)
        ]);

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        result.Metadata.IterationCount.Should().Be(2);
        _mockAnalysisAgent.AnalyzeCallCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsMaxIterations_Quick()
    {
        // Arrange
        var request = CreateTestRequest() with { Depth = ResearchDepth.Quick, MaxIterations = 10 };
        _mockQueryPlanner.SetupPlan(CreateTestPlanResult());
        _mockSearchCoordinator.SetupSearchResult(CreateTestSearchResult());
        _mockContentEnrichment.SetupEnrichmentResult(CreateTestEnrichmentResult());
        _mockReportGenerator.SetupReportResult(CreateTestReportResult());

        // 항상 부족하다고 응답
        _mockAnalysisAgent.SetupInfiniteResults(CreateAnalysisResult(needsMore: true, score: 0.5m));

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        // Quick 모드는 최대 2회 반복
        result.Metadata.IterationCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsMaxIterations_Standard()
    {
        // Arrange
        var request = CreateTestRequest() with { Depth = ResearchDepth.Standard, MaxIterations = 10 };
        _mockQueryPlanner.SetupPlan(CreateTestPlanResult());
        _mockSearchCoordinator.SetupSearchResult(CreateTestSearchResult());
        _mockContentEnrichment.SetupEnrichmentResult(CreateTestEnrichmentResult());
        _mockReportGenerator.SetupReportResult(CreateTestReportResult());

        _mockAnalysisAgent.SetupInfiniteResults(CreateAnalysisResult(needsMore: true, score: 0.5m));

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        // Standard 모드는 최대 5회 반복
        result.Metadata.IterationCount.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesCancellation()
    {
        // Arrange
        var request = CreateTestRequest();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var result = await _orchestrator.ExecuteAsync(request, cts.Token);

        // Assert
        result.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_HandlesException_ReturnsPartialResult()
    {
        // Arrange
        var request = CreateTestRequest();
        _mockQueryPlanner.SetupFailure(new InvalidOperationException("Planning failed"));

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        result.IsPartial.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Type.Should().Be(ResearchErrorType.Unknown);
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsProgressEvents()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        progressList.Should().NotBeEmpty();
        progressList.Should().Contain(p => p.Type == ProgressType.Started);
        progressList.Should().Contain(p => p.Type == ProgressType.PlanGenerated);
        progressList.Should().Contain(p => p.Type == ProgressType.SearchStarted);
        progressList.Should().Contain(p => p.Type == ProgressType.Completed);
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsSearchProgress()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        var searchProgress = progressList.Where(p => p.Type == ProgressType.SearchCompleted).ToList();
        searchProgress.Should().NotBeEmpty();
        searchProgress[0].Search.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsAnalysisProgress()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        var analysisProgress = progressList.FirstOrDefault(p => p.Type == ProgressType.AnalysisCompleted);
        analysisProgress.Should().NotBeNull();
        analysisProgress!.Analysis.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsReportSections()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        var reportSections = progressList.Where(p => p.Type == ProgressType.ReportSection).ToList();
        reportSections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteStreamAsync_YieldsFinalResult()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        var completed = progressList.FirstOrDefault(p => p.Type == ProgressType.Completed);
        completed.Should().NotBeNull();
        completed!.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteStreamAsync_HandlesException_YieldsFailedEvent()
    {
        // Arrange
        var request = CreateTestRequest();
        _mockQueryPlanner.SetupFailure(new InvalidOperationException("Failed"));

        // Act
        var progressList = new List<ResearchProgress>();
        await foreach (var progress in _orchestrator.ExecuteStreamAsync(request))
        {
            progressList.Add(progress);
        }

        // Assert
        progressList.Should().Contain(p => p.Type == ProgressType.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_CollectsSources()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        result.AllSources.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RecordsMetadata()
    {
        // Arrange
        var request = CreateTestRequest();
        SetupDefaultMocks(needsMoreResearch: false);

        // Act
        var result = await _orchestrator.ExecuteAsync(request);

        // Assert
        result.Metadata.Should().NotBeNull();
        result.Metadata.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Metadata.TotalQueriesExecuted.Should().BeGreaterThan(0);
    }

    private void SetupDefaultMocks(bool needsMoreResearch)
    {
        _mockQueryPlanner.SetupPlan(CreateTestPlanResult());
        _mockSearchCoordinator.SetupSearchResult(CreateTestSearchResult());
        _mockContentEnrichment.SetupEnrichmentResult(CreateTestEnrichmentResult());
        _mockAnalysisAgent.SetupAnalysisResult(CreateAnalysisResult(needsMore: needsMoreResearch, score: 0.9m));
        _mockReportGenerator.SetupReportResult(CreateTestReportResult());
    }

    private static ResearchRequest CreateTestRequest()
    {
        return new ResearchRequest
        {
            Query = "Test query",
            Depth = ResearchDepth.Standard,
            MaxIterations = 5,
            Language = "ko"
        };
    }

    private static QueryPlanResult CreateTestPlanResult()
    {
        return new QueryPlanResult
        {
            InitialQueries =
            [
                new ExpandedQuery
                {
                    Query = "Expanded query 1",
                    Intent = "Search",
                    Priority = 1,
                    SearchType = QuerySearchType.Web
                }
            ],
            Perspectives =
            [
                new ResearchPerspective
                {
                    Id = "p1",
                    Name = "Perspective 1",
                    Description = "Test perspective"
                }
            ],
            SubQuestions =
            [
                new SubQuestion
                {
                    Id = "q1",
                    Question = "Sub question 1",
                    Purpose = "Purpose",
                    Priority = 1
                }
            ],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static SearchExecutionResult CreateTestSearchResult()
    {
        return new SearchExecutionResult
        {
            SuccessfulResults =
            [
                new SearchResult
                {
                    Query = new SearchQuery { Query = "Test query", Type = SearchType.Web },
                    Provider = "test",
                    Sources =
                    [
                        new SearchSource
                        {
                            Title = "Source 1",
                            Url = "https://example.com/1",
                            Snippet = "Snippet 1"
                        }
                    ],
                    SearchedAt = DateTimeOffset.UtcNow
                }
            ],
            FailedSearches = [],
            TotalQueriesExecuted = 1,
            UniqueSourcesCollected = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1)
        };
    }

    private static ContentEnrichmentResult CreateTestEnrichmentResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new ContentEnrichmentResult
        {
            Documents =
            [
                new SourceDocument
                {
                    Id = "doc_1",
                    Url = "https://example.com/1",
                    Title = "Test Document",
                    Content = "Test content",
                    RelevanceScore = 0.8,
                    TrustScore = 0.7,
                    Provider = "test",
                    ExtractedAt = now
                }
            ],
            FailedExtractions = [],
            TotalUrlsProcessed = 1,
            TotalChunksCreated = 0,
            StartedAt = now,
            CompletedAt = now.AddSeconds(1)
        };
    }

    private static AnalysisResult CreateAnalysisResult(bool needsMore, decimal score)
    {
        var now = DateTimeOffset.UtcNow;
        // NeedsMoreResearch는 계산 프로퍼티: !SufficiencyScore.IsSufficient && Gaps.Count > 0
        // needsMore가 true이면 score를 낮게(0.5), gaps를 추가
        // needsMore가 false이면 score를 높게(0.9), gaps를 비움
        return new AnalysisResult
        {
            Findings =
            [
                new Finding
                {
                    Id = "find_1",
                    Claim = "Test finding",
                    SourceId = "doc_1",
                    VerificationScore = 0.9m,
                    IsVerified = true,
                    IterationDiscovered = 1,
                    DiscoveredAt = now
                }
            ],
            Gaps = needsMore
                ? [
                    new InformationGap
                    {
                        Description = "Missing information",
                        Priority = GapPriority.High,
                        SuggestedQuery = "Follow-up query",
                        IdentifiedAt = now
                    }
                ]
                : [],
            SufficiencyScore = new SufficiencyScore
            {
                OverallScore = score,
                CoverageScore = score,
                SourceDiversityScore = score,
                QualityScore = score,
                FreshnessScore = score,
                EvaluatedAt = now
            },
            SourcesAnalyzed = 1,
            StartedAt = now,
            CompletedAt = now.AddSeconds(1)
        };
    }

    private static ReportGenerationResult CreateTestReportResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new ReportGenerationResult
        {
            Report = "# Test Report\n\nContent",
            Sections =
            [
                new ReportSection
                {
                    Title = "Introduction",
                    Content = "Introduction content",
                    Order = 1
                }
            ],
            Citations = [],
            Outline = new ReportOutline
            {
                Title = "Test Report",
                Sections =
                [
                    new OutlineSection
                    {
                        Title = "Introduction",
                        Purpose = "Introduce",
                        Order = 1
                    }
                ]
            },
            StartedAt = now,
            CompletedAt = now.AddSeconds(1)
        };
    }
}

#region Mock Agents

internal class MockQueryPlannerAgentForOrchestrator : QueryPlannerAgent
{
    private QueryPlanResult? _planResult;
    private Exception? _failure;

    public bool PlanCalled { get; private set; }

    public MockQueryPlannerAgentForOrchestrator() : base(
        new MockQueryExpanderForOrchestrator(),
        NullLogger<QueryPlannerAgent>.Instance)
    {
    }

    public void SetupPlan(QueryPlanResult result) => _planResult = result;
    public void SetupFailure(Exception ex) => _failure = ex;

    public override Task<QueryPlanResult> PlanAsync(
        ResearchState state,
        CancellationToken cancellationToken = default)
    {
        PlanCalled = true;

        if (_failure != null)
            throw _failure;

        return Task.FromResult(_planResult ?? new QueryPlanResult
        {
            InitialQueries = [],
            Perspectives = [],
            SubQuestions = [],
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}

internal class MockQueryExpanderForOrchestrator : IQueryExpander
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

internal class MockSearchCoordinatorAgentForOrchestrator : SearchCoordinatorAgent
{
    private SearchExecutionResult? _result;

    public bool ExecuteSearchesCalled { get; private set; }

    public MockSearchCoordinatorAgentForOrchestrator() : base(
        new SearchProviderFactory([], new DeepResearchOptions(), NullLogger<SearchProviderFactory>.Instance),
        new DeepResearchOptions(),
        NullLogger<SearchCoordinatorAgent>.Instance)
    {
    }

    public void SetupSearchResult(SearchExecutionResult result) => _result = result;

    public override Task<SearchExecutionResult> ExecuteSearchesAsync(
        IReadOnlyList<ExpandedQuery> queries,
        SearchExecutionOptions? options = null,
        IProgress<SearchBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ExecuteSearchesCalled = true;

        return Task.FromResult(_result ?? new SearchExecutionResult
        {
            SuccessfulResults = [],
            FailedSearches = [],
            TotalQueriesExecuted = 0,
            UniqueSourcesCollected = 0,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
    }
}

internal class MockContentEnrichmentAgentForOrchestrator : ContentEnrichmentAgent
{
    private ContentEnrichmentResult? _result;

    public bool EnrichCalled { get; private set; }

    public MockContentEnrichmentAgentForOrchestrator() : base(
        new MockContentExtractorForOrchestrator(),
        new ContentChunker(NullLogger<ContentChunker>.Instance),
        new DeepResearchOptions(),
        NullLogger<ContentEnrichmentAgent>.Instance)
    {
    }

    public void SetupEnrichmentResult(ContentEnrichmentResult result) => _result = result;

    public override Task<ContentEnrichmentResult> EnrichSearchResultsAsync(
        IReadOnlyList<SearchResult> searchResults,
        ContentEnrichmentOptions? options = null,
        IProgress<ContentEnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnrichCalled = true;

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(_result ?? new ContentEnrichmentResult
        {
            Documents = [],
            FailedExtractions = [],
            TotalUrlsProcessed = 0,
            TotalChunksCreated = 0,
            StartedAt = now,
            CompletedAt = now
        });
    }
}

internal class MockContentExtractorForOrchestrator : IContentExtractor
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

internal class MockTextGenerationServiceForOrchestrator : ITextGenerationService
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

internal class MockAnalysisAgentForOrchestrator : AnalysisAgent
{
    private readonly Queue<AnalysisResult> _results = new();
    private AnalysisResult? _infiniteResult;

    public bool AnalyzeCalled { get; private set; }
    public int AnalyzeCallCount { get; private set; }

    public MockAnalysisAgentForOrchestrator() : base(
        new MockTextGenerationServiceForOrchestrator(),
        new DeepResearchOptions(),
        NullLogger<AnalysisAgent>.Instance)
    {
    }

    public void SetupAnalysisResult(AnalysisResult result)
    {
        _results.Enqueue(result);
    }

    public void SetupAnalysisResults(IEnumerable<AnalysisResult> results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public void SetupInfiniteResults(AnalysisResult result)
    {
        _infiniteResult = result;
    }

    public override Task<AnalysisResult> AnalyzeAsync(
        ResearchState state,
        AnalysisOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AnalyzeCalled = true;
        AnalyzeCallCount++;

        if (_results.Count > 0)
        {
            var result = _results.Dequeue();
            state.Findings.AddRange(result.Findings);
            state.IdentifiedGaps.AddRange(result.Gaps);
            state.LastSufficiencyScore = result.SufficiencyScore;
            return Task.FromResult(result);
        }

        if (_infiniteResult != null)
        {
            state.Findings.AddRange(_infiniteResult.Findings);
            state.IdentifiedGaps.AddRange(_infiniteResult.Gaps);
            state.LastSufficiencyScore = _infiniteResult.SufficiencyScore;
            return Task.FromResult(_infiniteResult);
        }

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new AnalysisResult
        {
            Findings = [],
            Gaps = [],
            SufficiencyScore = new SufficiencyScore
            {
                OverallScore = 1m,
                CoverageScore = 1m,
                SourceDiversityScore = 1m,
                QualityScore = 1m,
                FreshnessScore = 1m,
                EvaluatedAt = now
            },
            SourcesAnalyzed = 0,
            StartedAt = now,
            CompletedAt = now
        });
    }
}

internal class MockReportGeneratorAgentForOrchestrator : ReportGeneratorAgent
{
    private ReportGenerationResult? _result;

    public bool GenerateReportCalled { get; private set; }

    public MockReportGeneratorAgentForOrchestrator() : base(
        new MockTextGenerationServiceForOrchestrator(),
        new DeepResearchOptions(),
        NullLogger<ReportGeneratorAgent>.Instance)
    {
    }

    public void SetupReportResult(ReportGenerationResult result) => _result = result;

    public override Task<ReportGenerationResult> GenerateReportAsync(
        ResearchState state,
        ReportGenerationOptions? options = null,
        IProgress<ReportGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GenerateReportCalled = true;

        if (_result != null)
        {
            state.Outline = _result.Outline;
            state.GeneratedSections.AddRange(_result.Sections);
            return Task.FromResult(_result);
        }

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new ReportGenerationResult
        {
            Report = "Report",
            Sections = [],
            Citations = [],
            Outline = new ReportOutline { Title = "Report", Sections = [] },
            StartedAt = now,
            CompletedAt = now
        });
    }
}

#endregion
