using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Planning;

public class LLMQueryExpanderTests
{
    private static readonly string[] s_techKeyTopics = ["배터리 밀도", "충전 인프라"];
    private static readonly string[] s_econKeyTopics = ["시장 점유율", "투자 규모"];

    private readonly LLMQueryExpander _expander;
    private readonly MockTextGenerationService _mockService;

    public LLMQueryExpanderTests()
    {
        _mockService = new MockTextGenerationService();
        _expander = new LLMQueryExpander(
            _mockService,
            NullLogger<LLMQueryExpander>.Instance);
    }

    [Fact]
    public async Task DecomposeQueryAsync_ValidResponse_ReturnsSubQuestions()
    {
        // Arrange
        var query = "AI가 2026년 의료 산업에 미치는 영향은?";
        _mockService.SetStructuredResponse(new
        {
            questions = new[]
            {
                new { question = "AI 진단 기술의 현황은?", intent = "기술 현황 파악", priority = 1 },
                new { question = "의료 AI의 규제 동향은?", intent = "규제 현황 파악", priority = 2 }
            }
        });

        // Act
        var result = await _expander.DecomposeQueryAsync(query);

        // Assert
        result.Should().HaveCount(2);
        result[0].Question.Should().Be("AI 진단 기술의 현황은?");
        result[0].Priority.Should().Be(1);
        result[1].Question.Should().Be("의료 AI의 규제 동향은?");
    }

    [Fact]
    public async Task DecomposeQueryAsync_EmptyResponse_ReturnsFallback()
    {
        // Arrange
        var query = "테스트 질문";
        _mockService.SetStructuredResponse<object>(null);

        // Act
        var result = await _expander.DecomposeQueryAsync(query);

        // Assert
        result.Should().HaveCount(1);
        result[0].Question.Should().Be(query);
        result[0].Purpose.Should().Be("원본 질문 탐색");
    }

    [Fact]
    public async Task DecomposeQueryAsync_Exception_ReturnsFallback()
    {
        // Arrange
        var query = "테스트 질문";
        _mockService.SetException(new InvalidOperationException("LLM 오류"));

        // Act
        var result = await _expander.DecomposeQueryAsync(query);

        // Assert
        result.Should().HaveCount(1);
        result[0].Question.Should().Be(query);
    }

    [Fact]
    public async Task DiscoverPerspectivesAsync_ValidResponse_ReturnsPerspectives()
    {
        // Arrange
        var query = "전기차 시장 전망";
        _mockService.SetStructuredResponse(new
        {
            perspectives = new[]
            {
                new
                {
                    name = "기술적 관점",
                    description = "배터리 및 충전 기술 발전",
                    keyTopics = s_techKeyTopics
                },
                new
                {
                    name = "경제적 관점",
                    description = "시장 규모 및 투자 동향",
                    keyTopics = s_econKeyTopics
                }
            }
        });

        // Act
        var result = await _expander.DiscoverPerspectivesAsync(query);

        // Assert
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("기술적 관점");
        result[0].KeyTopics.Should().Contain("배터리 밀도");
        result[1].Name.Should().Be("경제적 관점");
    }

    [Fact]
    public async Task DiscoverPerspectivesAsync_EmptyResponse_ReturnsFallback()
    {
        // Arrange
        var query = "테스트 주제";
        _mockService.SetStructuredResponse<object>(null);

        // Act
        var result = await _expander.DiscoverPerspectivesAsync(query);

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("일반적 관점");
    }

    [Fact]
    public async Task ExpandQueriesAsync_ValidResponse_ReturnsExpandedQueries()
    {
        // Arrange
        var originalQuery = "AI 의료 영향";
        var subQuestions = new List<SubQuestion>
        {
            new() { Id = "sq_1", Question = "AI 진단 기술?", Purpose = "기술 파악", Priority = 1 }
        };
        var perspectives = new List<ResearchPerspective>
        {
            new() { Id = "persp_1", Name = "기술적", Description = "기술 분석" }
        };

        _mockService.SetStructuredResponse(new
        {
            queries = new object[]
            {
                new
                {
                    query = "AI medical diagnosis technology 2026",
                    intent = "기술 동향 검색",
                    priority = 1,
                    searchType = "web",
                    perspectiveId = "persp_1",
                    subQuestionId = "sq_1"
                },
                new
                {
                    query = "AI healthcare regulations",
                    intent = "규제 검색",
                    priority = 2,
                    searchType = "news",
                    perspectiveId = (string?)null,
                    subQuestionId = (string?)null
                }
            }
        });

        // Act
        var result = await _expander.ExpandQueriesAsync(
            originalQuery, subQuestions, perspectives);

        // Assert
        result.Should().HaveCount(2);
        result[0].Query.Should().Be("AI medical diagnosis technology 2026");
        result[0].Priority.Should().Be(1);
        result[0].SearchType.Should().Be(QuerySearchType.Web);
        result[1].SearchType.Should().Be(QuerySearchType.News);
    }

    [Fact]
    public async Task ExpandQueriesAsync_EmptyResponse_ReturnsFallback()
    {
        // Arrange
        var originalQuery = "테스트 쿼리";
        var subQuestions = new List<SubQuestion>
        {
            new() { Id = "sq_1", Question = "하위 질문?", Purpose = "테스트", Priority = 1 }
        };
        var perspectives = new List<ResearchPerspective>();

        _mockService.SetStructuredResponse<object>(null);

        // Act
        var result = await _expander.ExpandQueriesAsync(
            originalQuery, subQuestions, perspectives);

        // Assert
        result.Should().HaveCountGreaterThan(0);
        result[0].Query.Should().Be(originalQuery);
    }

    [Fact]
    public async Task ExpandQueriesAsync_SortsResultsByPriority()
    {
        // Arrange
        var originalQuery = "테스트";
        var subQuestions = new List<SubQuestion>();
        var perspectives = new List<ResearchPerspective>();

        _mockService.SetStructuredResponse(new
        {
            queries = new[]
            {
                new { query = "낮은 우선순위", intent = "", priority = 3, searchType = "web" },
                new { query = "높은 우선순위", intent = "", priority = 1, searchType = "web" },
                new { query = "중간 우선순위", intent = "", priority = 2, searchType = "web" }
            }
        });

        // Act
        var result = await _expander.ExpandQueriesAsync(
            originalQuery, subQuestions, perspectives);

        // Assert
        result[0].Priority.Should().Be(1);
        result[1].Priority.Should().Be(2);
        result[2].Priority.Should().Be(3);
    }

    [Fact]
    public async Task DecomposeQueryAsync_RespectsMaxSubQuestions()
    {
        // Arrange
        var query = "테스트";
        var options = new QueryExpansionOptions { MaxSubQuestions = 2 };

        _mockService.SetStructuredResponse(new
        {
            questions = new[]
            {
                new { question = "Q1", intent = "", priority = 1 },
                new { question = "Q2", intent = "", priority = 2 },
                new { question = "Q3", intent = "", priority = 3 },
                new { question = "Q4", intent = "", priority = 4 }
            }
        });

        // Act
        var result = await _expander.DecomposeQueryAsync(query, options);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DiscoverPerspectivesAsync_RespectsMaxPerspectives()
    {
        // Arrange
        var query = "테스트";
        var options = new QueryExpansionOptions { MaxPerspectives = 2 };

        _mockService.SetStructuredResponse(new
        {
            perspectives = new[]
            {
                new { name = "P1", description = "D1" },
                new { name = "P2", description = "D2" },
                new { name = "P3", description = "D3" }
            }
        });

        // Act
        var result = await _expander.DiscoverPerspectivesAsync(query, options);

        // Assert
        result.Should().HaveCount(2);
    }
}

/// <summary>
/// 테스트용 Mock TextGenerationService
/// </summary>
internal class MockTextGenerationService : ITextGenerationService
{
    private static readonly System.Text.Json.JsonSerializerOptions s_caseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private object? _structuredResponse;
    private Exception? _exception;

    public void SetStructuredResponse<T>(T? response) where T : class
    {
        _structuredResponse = response;
        _exception = null;
    }

    public void SetException(Exception exception)
    {
        _exception = exception;
        _structuredResponse = null;
    }

    public Task<TextGenerationResult> GenerateAsync(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_exception != null)
        {
            throw _exception;
        }

        return Task.FromResult(new TextGenerationResult
        {
            Text = System.Text.Json.JsonSerializer.Serialize(_structuredResponse)
        });
    }

    public Task<T?> GenerateStructuredAsync<T>(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        if (_exception != null)
        {
            throw _exception;
        }

        if (_structuredResponse == null)
        {
            return Task.FromResult<T?>(null);
        }

        // JSON 직렬화 후 역직렬화로 타입 변환
        var json = System.Text.Json.JsonSerializer.Serialize(_structuredResponse);
        var result = System.Text.Json.JsonSerializer.Deserialize<T>(json, s_caseInsensitiveOptions);

        return Task.FromResult(result);
    }
}
