using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Orchestration.State;

namespace IronHive.DeepResearch.Models.Report;

/// <summary>
/// 보고서 생성 결과
/// </summary>
public record ReportGenerationResult
{
    /// <summary>
    /// 생성된 보고서 (전체 텍스트)
    /// </summary>
    public required string Report { get; init; }

    /// <summary>
    /// 보고서 섹션들
    /// </summary>
    public required IReadOnlyList<ReportSection> Sections { get; init; }

    /// <summary>
    /// 인용 목록
    /// </summary>
    public required IReadOnlyList<Citation> Citations { get; init; }

    /// <summary>
    /// 생성된 아웃라인
    /// </summary>
    public required ReportOutline Outline { get; init; }

    /// <summary>
    /// 생성 시작 시간
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// 생성 완료 시간
    /// </summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// 소요 시간
    /// </summary>
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// 보고서 생성 옵션
/// </summary>
public record ReportGenerationOptions
{
    /// <summary>
    /// 출력 형식
    /// </summary>
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Markdown;

    /// <summary>
    /// 출력 언어
    /// </summary>
    public string Language { get; init; } = "ko";

    /// <summary>
    /// 최대 섹션 수
    /// </summary>
    public int MaxSections { get; init; } = 8;

    /// <summary>
    /// 섹션당 최대 토큰 수
    /// </summary>
    public int MaxTokensPerSection { get; init; } = 2000;

    /// <summary>
    /// 인용 스타일
    /// </summary>
    public CitationStyle CitationStyle { get; init; } = CitationStyle.Numbered;

    /// <summary>
    /// 요약 섹션 포함
    /// </summary>
    public bool IncludeSummary { get; init; } = true;

    /// <summary>
    /// 결론 섹션 포함
    /// </summary>
    public bool IncludeConclusion { get; init; } = true;

    /// <summary>
    /// 참고문헌 섹션 포함
    /// </summary>
    public bool IncludeReferences { get; init; } = true;

    /// <summary>
    /// 한계점 언급 여부
    /// </summary>
    public bool MentionLimitations { get; init; } = true;
}

/// <summary>
/// 인용 스타일
/// </summary>
public enum CitationStyle
{
    /// <summary>
    /// 번호 인용 [1], [2]
    /// </summary>
    Numbered,

    /// <summary>
    /// 저자-연도 (Author, Year)
    /// </summary>
    AuthorYear,

    /// <summary>
    /// 각주 스타일
    /// </summary>
    Footnote,

    /// <summary>
    /// 인라인 URL
    /// </summary>
    InlineUrl
}

/// <summary>
/// 보고서 생성 진행 상황
/// </summary>
public record ReportGenerationProgress
{
    /// <summary>
    /// 현재 단계
    /// </summary>
    public required ReportGenerationPhase Phase { get; init; }

    /// <summary>
    /// 완료된 섹션 수
    /// </summary>
    public int CompletedSections { get; init; }

    /// <summary>
    /// 전체 섹션 수
    /// </summary>
    public int TotalSections { get; init; }

    /// <summary>
    /// 현재 처리 중인 섹션
    /// </summary>
    public string? CurrentSection { get; init; }

    /// <summary>
    /// 진행률 (0-1)
    /// </summary>
    public double Progress => TotalSections > 0
        ? (double)CompletedSections / TotalSections
        : 0;
}

/// <summary>
/// 보고서 생성 단계
/// </summary>
public enum ReportGenerationPhase
{
    GeneratingOutline,
    GeneratingSections,
    ProcessingCitations,
    AssemblingReport,
    Completed
}

#region LLM Request/Response DTOs

/// <summary>
/// 아웃라인 생성 응답 (LLM용)
/// </summary>
internal record OutlineGenerationResponse
{
    public string Title { get; init; } = "";
    public List<OutlineSectionDto>? Sections { get; init; }
}

/// <summary>
/// 아웃라인 섹션 DTO (LLM 응답용)
/// </summary>
internal record OutlineSectionDto
{
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public List<string>? KeyPoints { get; init; }
}

/// <summary>
/// 섹션 콘텐츠 생성 응답 (LLM용)
/// </summary>
internal record SectionContentResponse
{
    public string Content { get; init; } = "";
    public List<string>? UsedFindings { get; init; }
    public List<CitationReference>? Citations { get; init; }
}

/// <summary>
/// 인용 참조 (LLM 응답용)
/// </summary>
internal record CitationReference
{
    public string SourceId { get; init; } = "";
    public string Quote { get; init; } = "";
}

#endregion
