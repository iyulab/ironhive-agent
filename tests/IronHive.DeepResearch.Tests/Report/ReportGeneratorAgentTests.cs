using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Report;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Report;

public class ReportGeneratorAgentTests
{
    private readonly ReportGeneratorAgent _agent;
    private readonly MockTextGenerationServiceForReport _mockTextService;
    private readonly DeepResearchOptions _options;

    public ReportGeneratorAgentTests()
    {
        _mockTextService = new MockTextGenerationServiceForReport();
        _options = new DeepResearchOptions();

        _agent = new ReportGeneratorAgent(
            _mockTextService,
            _options,
            NullLogger<ReportGeneratorAgent>.Instance);
    }

    [Fact]
    public async Task GenerateReportAsync_GeneratesOutline()
    {
        // Arrange
        var state = CreateTestState();

        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Test Report",
                Sections =
                [
                    new OutlineSectionDto { Title = "Introduction", Purpose = "Introduce the topic", KeyPoints = ["Point 1"] },
                    new OutlineSectionDto { Title = "Main Findings", Purpose = "Present findings", KeyPoints = ["Finding 1", "Finding 2"] }
                ]
            });

        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse { Content = "Section content here." });

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Outline.Should().NotBeNull();
        result.Outline.Title.Should().Be("Test Report");
        result.Outline.Sections.Should().HaveCount(2);
    }

    [Fact]
    public async Task GenerateReportAsync_GeneratesSections()
    {
        // Arrange
        var state = CreateTestState();
        SetupDefaultMocks();

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Sections.Should().NotBeEmpty();
        result.Sections.All(s => !string.IsNullOrEmpty(s.Content)).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateReportAsync_AssemblesReport()
    {
        // Arrange
        var state = CreateTestState();
        SetupDefaultMocks();

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Report.Should().NotBeNullOrEmpty();
        result.Report.Should().Contain("# Test Report");
        result.Report.Should().Contain("## Introduction");
    }

    [Fact]
    public async Task GenerateReportAsync_ProcessesCitations()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src_1", "https://example.com/source1"));

        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Report with Citations",
                Sections =
                [
                    new OutlineSectionDto { Title = "Section", Purpose = "Purpose" }
                ]
            });

        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse
            {
                Content = "This is content with citation [src_1].",
                Citations =
                [
                    new CitationReference { SourceId = "src_1", Quote = "Quote from source" }
                ]
            });

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Citations.Should().NotBeEmpty();
        result.Citations[0].SourceId.Should().Be("src_1");
        result.Citations[0].Number.Should().Be(1);
    }

    [Fact]
    public async Task GenerateReportAsync_IncludesReferencesSection()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src_1", "https://example.com/source1"));

        SetupDefaultMocksWithCitations();

        var options = new ReportGenerationOptions
        {
            IncludeReferences = true
        };

        // Act
        var result = await _agent.GenerateReportAsync(state, options);

        // Assert
        result.Report.Should().Contain("참고문헌");
    }

    [Fact]
    public async Task GenerateReportAsync_ReportsProgress()
    {
        // Arrange
        var state = CreateTestState();
        SetupDefaultMocks();

        var progressReports = new List<ReportGenerationProgress>();
        var progress = new Progress<ReportGenerationProgress>(p => progressReports.Add(p));

        // Act
        await _agent.GenerateReportAsync(state, progress: progress);

        // Assert
        await Task.Delay(100);
        progressReports.Should().NotBeEmpty();
        progressReports.Should().Contain(p => p.Phase == ReportGenerationPhase.GeneratingOutline);
        progressReports.Should().Contain(p => p.Phase == ReportGenerationPhase.GeneratingSections);
        progressReports.Should().Contain(p => p.Phase == ReportGenerationPhase.Completed);
    }

    [Fact]
    public async Task GenerateReportAsync_UpdatesState()
    {
        // Arrange
        var state = CreateTestState();
        SetupDefaultMocks();

        // Act
        await _agent.GenerateReportAsync(state);

        // Assert
        state.Outline.Should().NotBeNull();
        state.GeneratedSections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateReportAsync_HandlesLLMFailure_UsesDefaultOutline()
    {
        // Arrange
        var state = CreateTestState();
        _mockTextService.SetupFailure(new InvalidOperationException("LLM unavailable"));

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Outline.Should().NotBeNull();
        result.Outline.Sections.Should().NotBeEmpty();
        // 기본 아웃라인이 사용됨
        result.Outline.Sections.Should().Contain(s => s.Title == "요약" || s.Title == "개요");
    }

    [Fact]
    public async Task GenerateReportAsync_LimitsSections()
    {
        // Arrange
        var state = CreateTestState();

        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Report",
                Sections = Enumerable.Range(1, 15)
                    .Select(i => new OutlineSectionDto { Title = $"Section {i}", Purpose = "Purpose" })
                    .ToList()
            });

        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse { Content = "Content" });

        var options = new ReportGenerationOptions { MaxSections = 5 };

        // Act
        var result = await _agent.GenerateReportAsync(state, options);

        // Assert
        result.Sections.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task GenerateReportAsync_ReturnsCorrectDuration()
    {
        // Arrange
        var state = CreateTestState();
        SetupDefaultMocks();

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.StartedAt.Should().BeBefore(result.CompletedAt);
    }

    [Fact]
    public async Task GenerateReportAsync_WithDifferentCitationStyles()
    {
        // Arrange
        var state = CreateTestState();
        state.CollectedSources.Add(CreateTestSource("src_1", "https://example.com/source1", "John Doe"));

        SetupDefaultMocksWithCitations();

        // Test numbered style
        var numberedOptions = new ReportGenerationOptions
        {
            CitationStyle = CitationStyle.Numbered,
            IncludeReferences = true
        };

        // Act
        var result = await _agent.GenerateReportAsync(state, numberedOptions);

        // Assert
        result.Report.Should().Contain("[1]");
    }

    [Fact]
    public async Task GenerateReportAsync_WithFindingsAndSources_IncludesRelatedFindings()
    {
        // Arrange
        var state = CreateTestState();
        state.Findings.Add(new Finding
        {
            Id = "find_1",
            Claim = "Important finding about topic",
            SourceId = "src_1",
            VerificationScore = 0.9m,
            IsVerified = true,
            IterationDiscovered = 1,
            DiscoveredAt = DateTimeOffset.UtcNow
        });

        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Report",
                Sections =
                [
                    new OutlineSectionDto { Title = "Finding Analysis", Purpose = "Analyze finding about topic" }
                ]
            });

        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse
            {
                Content = "Analysis content.",
                UsedFindings = ["find_1"]
            });

        // Act
        var result = await _agent.GenerateReportAsync(state);

        // Assert
        result.Sections[0].RelatedFindings.Should().Contain("find_1");
    }

    private void SetupDefaultMocks()
    {
        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Test Report",
                Sections =
                [
                    new OutlineSectionDto { Title = "Introduction", Purpose = "Introduction" },
                    new OutlineSectionDto { Title = "Findings", Purpose = "Findings" }
                ]
            });

        // 각 섹션에 대한 응답 제공
        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse { Content = "Introduction content." });
        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse { Content = "Findings content." });
    }

    private void SetupDefaultMocksWithCitations()
    {
        _mockTextService.SetupStructuredResponse<OutlineGenerationResponse>(
            new OutlineGenerationResponse
            {
                Title = "Test Report",
                Sections =
                [
                    new OutlineSectionDto { Title = "Section", Purpose = "Purpose" }
                ]
            });

        _mockTextService.SetupStructuredResponse<SectionContentResponse>(
            new SectionContentResponse
            {
                Content = "Content with citation [src_1].",
                Citations =
                [
                    new CitationReference { SourceId = "src_1", Quote = "Quote" }
                ]
            });
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

    private static SourceDocument CreateTestSource(string id, string url, string? author = null)
    {
        return new SourceDocument
        {
            Id = id,
            Url = url,
            Title = $"Test Source {id}",
            Content = "Test content",
            Author = author,
            RelevanceScore = 0.8,
            TrustScore = 0.7,
            Provider = "test",
            ExtractedAt = DateTimeOffset.UtcNow,
            PublishedDate = DateTimeOffset.UtcNow.AddDays(-7)
        };
    }
}

/// <summary>
/// 테스트용 Mock TextGenerationService (Report 전용)
/// </summary>
internal class MockTextGenerationServiceForReport : ITextGenerationService
{
    private readonly Dictionary<Type, Queue<object>> _structuredResponses = new();
    private Exception? _failure;

    public void SetupStructuredResponse<T>(T response) where T : class
    {
        if (!_structuredResponses.ContainsKey(typeof(T)))
        {
            _structuredResponses[typeof(T)] = new Queue<object>();
        }
        _structuredResponses[typeof(T)].Enqueue(response);
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
        if (_failure != null)
            throw _failure;

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
        if (_failure != null)
            throw _failure;

        if (_structuredResponses.TryGetValue(typeof(T), out var queue) && queue.Count > 0)
        {
            return Task.FromResult((T?)queue.Dequeue());
        }

        return Task.FromResult<T?>(null);
    }
}
