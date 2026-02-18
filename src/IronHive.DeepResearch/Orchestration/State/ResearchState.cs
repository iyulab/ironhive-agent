using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;

namespace IronHive.DeepResearch.Orchestration.State;

/// <summary>
/// 리서치 상태 (체크포인트 가능)
/// </summary>
public class ResearchState
{
    public required string SessionId { get; init; }
    public required ResearchRequest Request { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    // 현재 진행 상태
    public ResearchPhase CurrentPhase { get; set; } = ResearchPhase.Planning;
    public int CurrentIteration { get; set; }

    // 수집된 데이터
    public List<SearchQuery> ExecutedQueries { get; } = [];
    public List<SearchResult> SearchResults { get; } = [];
    public List<SourceDocument> CollectedSources { get; } = [];
    public List<Finding> Findings { get; } = [];
    public List<InformationGap> IdentifiedGaps { get; } = [];

    // 분석 결과
    public SufficiencyScore? LastSufficiencyScore { get; set; }
    public List<string> ResearchAngles { get; } = [];

    // 보고서 생성 상태
    public ReportOutline? Outline { get; set; }
    public List<ReportSection> GeneratedSections { get; } = [];

    // 비용 추적
    public TokenUsage AccumulatedTokenUsage { get; set; } = new();
    public decimal AccumulatedCost { get; set; } = 0;

    // 에러 추적
    public List<ResearchError> Errors { get; } = [];

    // 사고 과정 추적
    public List<ThinkingStep> ThinkingSteps { get; } = [];

    /// <summary>
    /// 사고 과정 단계 추가
    /// </summary>
    public void AddThinkingStep(ThinkingStepType type, string title, string description,
        TimeSpan? duration = null, Dictionary<string, object>? data = null)
    {
        ThinkingSteps.Add(new ThinkingStep
        {
            Type = type,
            Title = title,
            Description = description,
            Timestamp = DateTimeOffset.UtcNow,
            Duration = duration,
            Data = data
        });
    }
}

/// <summary>
/// 리서치 단계
/// </summary>
public enum ResearchPhase
{
    Planning,
    Searching,
    ContentExtraction,
    Analysis,
    SufficiencyEvaluation,
    ReportGeneration,
    Completed,
    Failed
}

/// <summary>
/// 보고서 아웃라인
/// </summary>
public record ReportOutline
{
    public required string Title { get; init; }
    public required IReadOnlyList<OutlineSection> Sections { get; init; }
}

/// <summary>
/// 아웃라인 섹션
/// </summary>
public record OutlineSection
{
    public required string Title { get; init; }
    public required string Purpose { get; init; }
    public required int Order { get; init; }
    public IReadOnlyList<string> KeyPoints { get; init; } = [];
}

/// <summary>
/// 체크포인트 (직렬화 가능)
/// </summary>
public record ResearchCheckpoint
{
    public required string SessionId { get; init; }
    public required int CheckpointNumber { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ResearchState State { get; init; }

    // Human-in-the-Loop 지원
    public IReadOnlyList<Finding>? KeyFindings { get; init; }
    public IReadOnlyList<string>? SuggestedQueries { get; init; }
    public string? Summary { get; init; }
}
