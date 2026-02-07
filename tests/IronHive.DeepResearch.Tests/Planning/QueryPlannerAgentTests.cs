using FluentAssertions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Planning;

public class QueryPlannerAgentTests
{
    private readonly QueryPlannerAgent _agent;
    private readonly MockQueryExpander _mockExpander;

    public QueryPlannerAgentTests()
    {
        _mockExpander = new MockQueryExpander();
        _agent = new QueryPlannerAgent(
            _mockExpander,
            NullLogger<QueryPlannerAgent>.Instance);
    }

    [Fact]
    public async Task PlanAsync_CreatesCompletePlan()
    {
        // Arrange
        var state = CreateTestState("AI의 미래 발전 방향");

        _mockExpander.SubQuestions =
        [
            new SubQuestion { Id = "sq_1", Question = "AI 기술 현황?", Purpose = "현황 파악", Priority = 1 },
            new SubQuestion { Id = "sq_2", Question = "AI 윤리적 문제?", Purpose = "윤리 탐구", Priority = 2 }
        ];

        _mockExpander.Perspectives =
        [
            new ResearchPerspective { Id = "p_1", Name = "기술적 관점", Description = "기술 분석" },
            new ResearchPerspective { Id = "p_2", Name = "사회적 관점", Description = "사회 영향" }
        ];

        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "AI technology 2026", Intent = "기술 검색", Priority = 1 },
            new ExpandedQuery { Query = "AI ethics concerns", Intent = "윤리 검색", Priority = 2 }
        ];

        // Act
        var result = await _agent.PlanAsync(state);

        // Assert
        result.Should().NotBeNull();
        result.SubQuestions.Should().HaveCount(2);
        result.Perspectives.Should().HaveCount(2);
        result.InitialQueries.Should().HaveCount(2);
        result.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PlanAsync_UsesCorrectOptionsForDepth()
    {
        // Arrange
        var state = CreateTestState("테스트 쿼리", ResearchDepth.Comprehensive);

        _mockExpander.SubQuestions = [new SubQuestion { Id = "sq_1", Question = "Q", Purpose = "P", Priority = 1 }];
        _mockExpander.Perspectives = [new ResearchPerspective { Id = "p_1", Name = "N", Description = "D" }];
        _mockExpander.ExpandedQueries = [new ExpandedQuery { Query = "Q", Intent = "I", Priority = 1 }];

        // Act
        await _agent.PlanAsync(state);

        // Assert
        _mockExpander.LastOptions.Should().NotBeNull();
        _mockExpander.LastOptions!.MaxSubQuestions.Should().BeGreaterThan(8); // Comprehensive uses more
    }

    [Fact]
    public async Task PlanAsync_DeduplicatesQueries()
    {
        // Arrange
        var state = CreateTestState("테스트");

        _mockExpander.SubQuestions = [new SubQuestion { Id = "sq_1", Question = "Q", Purpose = "P", Priority = 1 }];
        _mockExpander.Perspectives = [new ResearchPerspective { Id = "p_1", Name = "N", Description = "D" }];
        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "same query", Intent = "I1", Priority = 1 },
            new ExpandedQuery { Query = "SAME QUERY", Intent = "I2", Priority = 2 }, // 중복
            new ExpandedQuery { Query = "different query", Intent = "I3", Priority = 3 }
        ];

        // Act
        var result = await _agent.PlanAsync(state);

        // Assert
        result.InitialQueries.Should().HaveCount(2);
        result.InitialQueries.Select(q => q.Query.ToLower())
            .Distinct()
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task PlanAsync_SortsQueriesByPriority()
    {
        // Arrange
        var state = CreateTestState("테스트");

        _mockExpander.SubQuestions = [];
        _mockExpander.Perspectives = [];
        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "low", Intent = "I", Priority = 3 },
            new ExpandedQuery { Query = "high", Intent = "I", Priority = 1 },
            new ExpandedQuery { Query = "medium", Intent = "I", Priority = 2 }
        ];

        // Act
        var result = await _agent.PlanAsync(state);

        // Assert
        result.InitialQueries[0].Priority.Should().Be(1);
        result.InitialQueries[1].Priority.Should().Be(2);
        result.InitialQueries[2].Priority.Should().Be(3);
    }

    [Fact]
    public async Task GenerateFollowUpQueriesAsync_NoGaps_ReturnsEmpty()
    {
        // Arrange
        var state = CreateTestState("테스트");
        state.IdentifiedGaps.Clear();

        // Act
        var result = await _agent.GenerateFollowUpQueriesAsync(state);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateFollowUpQueriesAsync_WithGaps_GeneratesQueries()
    {
        // Arrange
        var state = CreateTestState("테스트");
        state.IdentifiedGaps.Add(new InformationGap
        {
            Description = "비용 정보 부족",
            Priority = GapPriority.High,
            SuggestedQuery = "AI 구현 비용",
            IdentifiedAt = DateTimeOffset.UtcNow
        });

        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "AI implementation cost 2026", Intent = "비용 검색", Priority = 1 }
        ];

        // Act
        var result = await _agent.GenerateFollowUpQueriesAsync(state);

        // Assert
        result.Should().HaveCount(1);
        result[0].Query.Should().Be("AI implementation cost 2026");
    }

    [Fact]
    public async Task GenerateFollowUpQueriesAsync_ExcludesExecutedQueries()
    {
        // Arrange
        var state = CreateTestState("테스트");
        state.ExecutedQueries.Add(new SearchQuery { Query = "AI implementation cost 2026" });
        state.IdentifiedGaps.Add(new InformationGap
        {
            Description = "비용 정보 부족",
            Priority = GapPriority.High,
            SuggestedQuery = "비용 쿼리",
            IdentifiedAt = DateTimeOffset.UtcNow
        });

        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "AI implementation cost 2026", Intent = "비용 검색", Priority = 1 }, // 중복
            new ExpandedQuery { Query = "new unique query", Intent = "신규", Priority = 2 }
        ];

        // Act
        var result = await _agent.GenerateFollowUpQueriesAsync(state);

        // Assert
        result.Should().HaveCount(1);
        result[0].Query.Should().Be("new unique query");
    }

    [Fact]
    public async Task GenerateFollowUpQueriesAsync_UsesExistingAngles()
    {
        // Arrange
        var state = CreateTestState("테스트");
        state.ResearchAngles.Add("기술적 관점");
        state.ResearchAngles.Add("경제적 관점");
        state.IdentifiedGaps.Add(new InformationGap
        {
            Description = "갭",
            Priority = GapPriority.Medium,
            SuggestedQuery = "쿼리",
            IdentifiedAt = DateTimeOffset.UtcNow
        });

        _mockExpander.ExpandedQueries =
        [
            new ExpandedQuery { Query = "test", Intent = "I", Priority = 1 }
        ];

        // Act
        await _agent.GenerateFollowUpQueriesAsync(state);

        // Assert
        // 기존 관점을 perspectives로 전달했는지 확인
        _mockExpander.LastPerspectives.Should().NotBeNull();
        _mockExpander.LastPerspectives!.Count.Should().Be(2);
    }

    [Fact]
    public async Task PlanAsync_IncludesAcademicSearchWhenRequested()
    {
        // Arrange
        var state = CreateTestState("학술 연구 주제");
        state.Request.GetType().GetProperty("IncludeAcademic")!
            .SetValue(state.Request, true);

        _mockExpander.SubQuestions = [];
        _mockExpander.Perspectives = [];
        _mockExpander.ExpandedQueries = [];

        // Act
        await _agent.PlanAsync(state);

        // Assert
        _mockExpander.LastOptions!.IncludeAcademic.Should().BeTrue();
    }

    private static ResearchState CreateTestState(
        string query,
        ResearchDepth depth = ResearchDepth.Standard)
    {
        return new ResearchState
        {
            SessionId = Guid.NewGuid().ToString(),
            Request = new ResearchRequest
            {
                Query = query,
                Depth = depth,
                Language = "ko"
            },
            StartedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// 테스트용 Mock QueryExpander
/// </summary>
internal class MockQueryExpander : IQueryExpander
{
    public IReadOnlyList<SubQuestion> SubQuestions { get; set; } = [];
    public IReadOnlyList<ResearchPerspective> Perspectives { get; set; } = [];
    public IReadOnlyList<ExpandedQuery> ExpandedQueries { get; set; } = [];

    public QueryExpansionOptions? LastOptions { get; private set; }
    public IReadOnlyList<ResearchPerspective>? LastPerspectives { get; private set; }

    public Task<IReadOnlyList<SubQuestion>> DecomposeQueryAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return Task.FromResult(SubQuestions);
    }

    public Task<IReadOnlyList<ResearchPerspective>> DiscoverPerspectivesAsync(
        string query,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return Task.FromResult(Perspectives);
    }

    public Task<IReadOnlyList<ExpandedQuery>> ExpandQueriesAsync(
        string originalQuery,
        IReadOnlyList<SubQuestion> subQuestions,
        IReadOnlyList<ResearchPerspective> perspectives,
        QueryExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        LastPerspectives = perspectives;
        return Task.FromResult(ExpandedQueries);
    }
}
