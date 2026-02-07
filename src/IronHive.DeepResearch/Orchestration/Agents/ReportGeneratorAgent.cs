using System.Text;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Report;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// 보고서 생성 에이전트: 아웃라인 생성, 섹션 작성, 인용 처리
/// </summary>
public class ReportGeneratorAgent
{
    private readonly ITextGenerationService _textService;
    private readonly DeepResearchOptions _researchOptions;
    private readonly ILogger<ReportGeneratorAgent> _logger;

    public ReportGeneratorAgent(
        ITextGenerationService textService,
        DeepResearchOptions researchOptions,
        ILogger<ReportGeneratorAgent> logger)
    {
        _textService = textService;
        _researchOptions = researchOptions;
        _logger = logger;
    }

    /// <summary>
    /// 보고서 생성 실행
    /// </summary>
    public virtual async Task<ReportGenerationResult> GenerateReportAsync(
        ResearchState state,
        ReportGenerationOptions? options = null,
        IProgress<ReportGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CreateDefaultOptions(state);
        var startedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("보고서 생성 시작: Finding {FindingCount}개, 소스 {SourceCount}개",
            state.Findings.Count, state.CollectedSources.Count);

        // 1. 아웃라인 생성
        ReportProgress(progress, ReportGenerationPhase.GeneratingOutline, 0, 0);
        var outline = await GenerateOutlineAsync(state, options, cancellationToken);
        state.Outline = outline;
        _logger.LogDebug("아웃라인 생성 완료: {SectionCount}개 섹션", outline.Sections.Count);

        // 2. 섹션별 콘텐츠 생성
        var sections = new List<ReportSection>();
        var citationContext = new CitationContext();

        for (int i = 0; i < outline.Sections.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outlineSection = outline.Sections[i];
            ReportProgress(progress, ReportGenerationPhase.GeneratingSections,
                i, outline.Sections.Count, outlineSection.Title);

            var section = await GenerateSectionAsync(
                state, outlineSection, i, options, citationContext, cancellationToken);

            sections.Add(section);
            state.GeneratedSections.Add(section);

            _logger.LogDebug("섹션 생성 완료: {Title}", section.Title);
        }

        // 3. 인용 처리
        ReportProgress(progress, ReportGenerationPhase.ProcessingCitations,
            outline.Sections.Count, outline.Sections.Count);
        var citations = citationContext.Citations.Values.OrderBy(c => c.Number).ToList();

        // 4. 보고서 조립 (본문만)
        ReportProgress(progress, ReportGenerationPhase.AssemblingReport,
            outline.Sections.Count, outline.Sections.Count);
        var report = AssembleReport(outline, sections, options);

        var completedAt = DateTimeOffset.UtcNow;

        ReportProgress(progress, ReportGenerationPhase.Completed,
            outline.Sections.Count, outline.Sections.Count);

        _logger.LogInformation("보고서 생성 완료: {SectionCount}개 섹션, {CitationCount}개 인용, 소요 시간 {Duration}ms",
            sections.Count, citations.Count, (completedAt - startedAt).TotalMilliseconds);

        return new ReportGenerationResult
        {
            Report = report,
            Sections = sections,
            Citations = citations,
            Outline = outline,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// 아웃라인 생성
    /// </summary>
    private async Task<ReportOutline> GenerateOutlineAsync(
        ResearchState state,
        ReportGenerationOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildOutlinePrompt(state, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.4,
            MaxTokens = 1500,
            SystemPrompt = GetOutlineSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<OutlineGenerationResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Sections is null || response.Sections.Count == 0)
            {
                return CreateDefaultOutline(state, options);
            }

            var sections = response.Sections
                .Take(options.MaxSections)
                .Select((s, i) => new OutlineSection
                {
                    Title = s.Title,
                    Purpose = s.Purpose,
                    Order = i + 1,
                    KeyPoints = s.KeyPoints ?? []
                })
                .ToList();

            return new ReportOutline
            {
                Title = response.Title,
                Sections = sections
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "아웃라인 생성 실패, 기본 아웃라인 사용");
            return CreateDefaultOutline(state, options);
        }
    }

    /// <summary>
    /// 섹션 콘텐츠 생성
    /// </summary>
    private async Task<ReportSection> GenerateSectionAsync(
        ResearchState state,
        OutlineSection outlineSection,
        int sectionIndex,
        ReportGenerationOptions options,
        CitationContext citationContext,
        CancellationToken cancellationToken)
    {
        var prompt = BuildSectionPrompt(state, outlineSection, citationContext.Citations, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.5,
            MaxTokens = options.MaxTokensPerSection,
            SystemPrompt = GetSectionSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<SectionContentResponse>(
                prompt, genOptions, cancellationToken);

            var content = response?.Content ?? "";
            var usedFindings = response?.UsedFindings ?? [];

            // 인용 처리
            if (response?.Citations != null)
            {
                foreach (var citationRef in response.Citations)
                {
                    if (!citationContext.Citations.ContainsKey(citationRef.SourceId))
                    {
                        var source = state.CollectedSources
                            .FirstOrDefault(s => s.Id == citationRef.SourceId);

                        if (source != null)
                        {
                            var citation = CreateCitation(source, citationContext.NextNumber());
                            citationContext.Citations[citationRef.SourceId] = citation;
                        }
                    }
                }
            }

            // 인용 번호 삽입
            content = ProcessCitationMarkers(content, citationContext.Citations, options.CitationStyle);

            return new ReportSection
            {
                Title = outlineSection.Title,
                Content = content,
                Order = sectionIndex + 1,
                RelatedFindings = usedFindings
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "섹션 생성 실패: {Title}", outlineSection.Title);

            return new ReportSection
            {
                Title = outlineSection.Title,
                Content = $"[섹션 생성 실패: {outlineSection.Title}]",
                Order = sectionIndex + 1,
                RelatedFindings = []
            };
        }
    }

    /// <summary>
    /// 인용 컨텍스트 (번호 관리)
    /// </summary>
    private class CitationContext
    {
        private int _nextNumber = 1;

        public Dictionary<string, Citation> Citations { get; } = new();

        public int NextNumber() => _nextNumber++;
    }

    /// <summary>
    /// 보고서 조립 (본문만, 출처는 ResearchResult에서 별도 제공)
    /// </summary>
    private string AssembleReport(
        ReportOutline outline,
        List<ReportSection> sections,
        ReportGenerationOptions options)
    {
        var sb = new StringBuilder();

        // 제목
        sb.AppendLine($"# {outline.Title}");
        sb.AppendLine();

        // 섹션들
        foreach (var section in sections.OrderBy(s => s.Order))
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #region Prompt Building

    private static string BuildOutlinePrompt(ResearchState state, ReportGenerationOptions options)
    {
        var findingSummary = string.Join("\n",
            state.Findings.Take(15).Select(f => $"- {f.Claim}"));

        var anglesSummary = state.ResearchAngles.Count > 0
            ? $"\n탐구 관점:\n{string.Join("\n", state.ResearchAngles.Select(a => $"- {a}"))}"
            : "";

        return $$"""
            연구 질문: {{state.Request.Query}}
            {{anglesSummary}}

            주요 발견 사항:
            {{findingSummary}}

            수집된 소스 수: {{state.CollectedSources.Count}}

            위 정보를 바탕으로 연구 보고서의 아웃라인을 생성하세요.

            요구사항:
            1. 제목은 연구 질문을 반영해야 합니다
            2. {{options.MaxSections}}개 이내의 섹션으로 구성
            3. 각 섹션에 명확한 목적과 핵심 포인트 포함
            {{(options.IncludeSummary ? "4. 요약/개요 섹션 포함" : "")}}
            {{(options.IncludeConclusion ? "5. 결론 섹션 포함" : "")}}

            JSON 형식으로 응답:
            {
              "title": "보고서 제목",
              "sections": [
                {
                  "title": "섹션 제목",
                  "purpose": "이 섹션의 목적",
                  "keyPoints": ["핵심 포인트 1", "핵심 포인트 2"]
                }
              ]
            }
            """;
    }

    private static string BuildSectionPrompt(
        ResearchState state,
        OutlineSection section,
        Dictionary<string, Citation> existingCitations,
        ReportGenerationOptions options)
    {
        // 섹션과 관련된 Finding 선택
        var relevantFindings = state.Findings
            .Where(f => IsRelevantToSection(f, section))
            .Take(10)
            .ToList();

        var findingsText = relevantFindings.Count > 0
            ? string.Join("\n", relevantFindings.Select(f =>
                $"- [{f.Id}] {f.Claim} (소스: {f.SourceId}, 신뢰도: {f.VerificationScore:P0})"))
            : "관련 발견 사항 없음";

        var keyPointsText = section.KeyPoints.Count > 0
            ? string.Join("\n", section.KeyPoints.Select(p => $"- {p}"))
            : "";

        // 관련 소스 정보
        var sourceIds = relevantFindings.Select(f => f.SourceId).Distinct().ToList();
        var sources = state.CollectedSources
            .Where(s => sourceIds.Contains(s.Id))
            .Take(5)
            .ToList();

        var sourcesText = sources.Count > 0
            ? string.Join("\n", sources.Select(s =>
                $"- [{s.Id}] {s.Title} ({s.Url})"))
            : "";

        return $$"""
            연구 질문: {{state.Request.Query}}

            === 현재 섹션 ===
            제목: {{section.Title}}
            목적: {{section.Purpose}}
            {{(keyPointsText.Length > 0 ? $"핵심 포인트:\n{keyPointsText}" : "")}}

            === 관련 발견 사항 ===
            {{findingsText}}

            === 참조 가능한 소스 ===
            {{sourcesText}}

            위 정보를 바탕으로 이 섹션의 콘텐츠를 작성하세요.

            규칙:
            1. 마크다운 형식으로 작성 (제목은 생략, 본문만)
            2. 발견 사항을 인용할 때 소스 ID 표시 (예: [src_1])
            3. 객관적이고 중립적인 톤 유지
            4. 적절한 단락 구분

            JSON 형식으로 응답:
            {
              "content": "섹션 본문 내용 (마크다운)",
              "usedFindings": ["find_1", "find_2"],
              "citations": [
                {"sourceId": "src_1", "quote": "인용 내용"}
              ]
            }
            """;
    }

    private static string GetOutlineSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 연구 보고서 구조를 설계하는 전문가입니다. 논리적이고 체계적인 보고서 아웃라인을 생성하세요. 항상 JSON 형식으로 응답하세요."
            : "You are an expert at structuring research reports. Create logical and systematic report outlines. Always respond in JSON format.";
    }

    private static string GetSectionSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 연구 보고서 작성 전문가입니다. 발견 사항을 바탕으로 명확하고 객관적인 보고서 섹션을 작성하세요. 항상 JSON 형식으로 응답하세요."
            : "You are an expert research report writer. Create clear and objective report sections based on findings. Always respond in JSON format.";
    }

    #endregion

    #region Helper Methods

    private static bool IsRelevantToSection(Finding finding, OutlineSection section)
    {
        var sectionWords = GetKeywords(section.Title + " " + section.Purpose);
        var findingWords = GetKeywords(finding.Claim);

        // 키워드 겹침으로 관련성 판단
        return sectionWords.Intersect(findingWords, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static HashSet<string> GetKeywords(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been",
            "and", "or", "but", "in", "on", "at", "to", "for", "of",
            "은", "는", "이", "가", "을", "를", "에", "의", "와", "과"
        };

        return text
            .Split(new[] { ' ', ',', '.', ':', ';', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Citation CreateCitation(SourceDocument source, int number)
    {
        return new Citation
        {
            Number = number,
            SourceId = source.Id,
            Url = source.Url,
            Title = source.Title,
            Author = source.Author,
            PublishedDate = source.PublishedDate,
            AccessedDate = DateTimeOffset.UtcNow
        };
    }

    private static string ProcessCitationMarkers(
        string content,
        Dictionary<string, Citation> citations,
        CitationStyle style)
    {
        // [src_xxx] 형식을 인용 스타일에 맞게 변환
        foreach (var (sourceId, citation) in citations)
        {
            var marker = style switch
            {
                CitationStyle.Numbered => $"[{citation.Number}]",
                CitationStyle.AuthorYear => $"({citation.Author ?? "Unknown"}, {citation.PublishedDate?.Year ?? DateTime.Now.Year})",
                CitationStyle.InlineUrl => $"([{citation.Title}]({citation.Url}))",
                _ => $"[{citation.Number}]"
            };

            content = content.Replace($"[{sourceId}]", marker);
        }

        return content;
    }

    private ReportOutline CreateDefaultOutline(ResearchState state, ReportGenerationOptions options)
    {
        var sections = new List<OutlineSection>();
        var order = 1;

        if (options.IncludeSummary)
        {
            sections.Add(new OutlineSection
            {
                Title = "요약",
                Purpose = "연구 결과의 핵심 내용을 요약합니다.",
                Order = order++,
                KeyPoints = []
            });
        }

        sections.Add(new OutlineSection
        {
            Title = "개요",
            Purpose = "연구 배경과 목적을 설명합니다.",
            Order = order++,
            KeyPoints = []
        });

        sections.Add(new OutlineSection
        {
            Title = "주요 발견",
            Purpose = "연구를 통해 발견한 주요 내용을 설명합니다.",
            Order = order++,
            KeyPoints = state.Findings.Take(5).Select(f => f.Claim).ToList()
        });

        sections.Add(new OutlineSection
        {
            Title = "분석",
            Purpose = "발견 사항에 대한 분석과 해석을 제공합니다.",
            Order = order++,
            KeyPoints = []
        });

        if (options.IncludeConclusion)
        {
            sections.Add(new OutlineSection
            {
                Title = "결론",
                Purpose = "연구 결과를 종합하고 결론을 도출합니다.",
                Order = order++,
                KeyPoints = []
            });
        }

        if (options.MentionLimitations)
        {
            sections.Add(new OutlineSection
            {
                Title = "한계점",
                Purpose = "연구의 한계점과 향후 연구 방향을 제시합니다.",
                Order = order,
                KeyPoints = []
            });
        }

        return new ReportOutline
        {
            Title = $"연구 보고서: {state.Request.Query}",
            Sections = sections
        };
    }

    private ReportGenerationOptions CreateDefaultOptions(ResearchState state)
    {
        return new ReportGenerationOptions
        {
            OutputFormat = state.Request.OutputFormat,
            Language = state.Request.Language,
            MaxSections = 8,
            MaxTokensPerSection = 2000,
            CitationStyle = CitationStyle.Numbered,
            IncludeSummary = true,
            IncludeConclusion = true,
            IncludeReferences = true,
            MentionLimitations = true
        };
    }

    private static void ReportProgress(
        IProgress<ReportGenerationProgress>? progress,
        ReportGenerationPhase phase,
        int completed,
        int total,
        string? currentSection = null)
    {
        progress?.Report(new ReportGenerationProgress
        {
            Phase = phase,
            CompletedSections = completed,
            TotalSections = total,
            CurrentSection = currentSection
        });
    }

    #endregion
}
