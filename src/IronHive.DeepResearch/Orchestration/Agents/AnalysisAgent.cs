using System.Text.Json;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Content;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration.Agents;

/// <summary>
/// 분석 에이전트: Finding 추출 및 정보 갭 식별
/// </summary>
public partial class AnalysisAgent
{
    private readonly ITextGenerationService _textService;
    private readonly DeepResearchOptions _researchOptions;
    private readonly ILogger<AnalysisAgent> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AnalysisAgent(
        ITextGenerationService textService,
        DeepResearchOptions researchOptions,
        ILogger<AnalysisAgent> logger)
    {
        _textService = textService;
        _researchOptions = researchOptions;
        _logger = logger;
    }

    /// <summary>
    /// 수집된 소스 분석 실행
    /// </summary>
    public virtual async Task<AnalysisResult> AnalyzeAsync(
        ResearchState state,
        AnalysisOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CreateDefaultOptions(state);
        var startedAt = DateTimeOffset.UtcNow;

        LogAnalysisStarting(_logger, state.CollectedSources.Count, state.CurrentIteration);

        // 1. 소스에서 Finding 추출
        var findings = await ExtractFindingsAsync(state, options, cancellationToken);
        LogFindingsExtracted(_logger, findings.Count);

        // 2. 정보 갭 식별
        var gaps = await IdentifyGapsAsync(state, findings, options, cancellationToken);
        LogGapsIdentified(_logger, gaps.Count);

        // 3. 충분성 평가
        var sufficiencyScore = await EvaluateSufficiencyAsync(
            state, findings, gaps, options, cancellationToken);
        LogSufficiencyScore(_logger, sufficiencyScore.OverallScore);

        var completedAt = DateTimeOffset.UtcNow;

        // 상태 업데이트
        UpdateState(state, findings, gaps, sufficiencyScore);

        LogAnalysisCompleted(_logger, findings.Count, gaps.Count, sufficiencyScore.OverallScore,
            (completedAt - startedAt).TotalMilliseconds);

        return new AnalysisResult
        {
            Findings = findings,
            Gaps = gaps,
            SufficiencyScore = sufficiencyScore,
            SourcesAnalyzed = Math.Min(state.CollectedSources.Count, options.MaxSourcesToAnalyze),
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// 소스에서 Finding 추출
    /// </summary>
    private async Task<IReadOnlyList<Finding>> ExtractFindingsAsync(
        ResearchState state,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var sourcesToAnalyze = state.CollectedSources
            .OrderByDescending(s => s.RelevanceScore)
            .ThenByDescending(s => s.TrustScore)
            .Take(options.MaxSourcesToAnalyze)
            .ToList();

        var subQuestions = state.ResearchAngles.Count > 0
            ? state.ResearchAngles
            : null;

        foreach (var source in sourcesToAnalyze)
        {
            try
            {
                var sourceFindings = await ExtractFindingsFromSourceAsync(
                    source, state.Request.Query, subQuestions, options, cancellationToken);

                findings.AddRange(sourceFindings);
            }
            catch (Exception ex)
            {
                LogSourceAnalysisFailed(_logger, ex, source.Id);
            }
        }

        // 중복 제거 및 우선순위 정렬
        return DeduplicateFindings(findings);
    }

    private async Task<IReadOnlyList<Finding>> ExtractFindingsFromSourceAsync(
        SourceDocument source,
        string originalQuery,
        IReadOnlyList<string>? subQuestions,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildFindingExtractionPrompt(source, originalQuery, subQuestions, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.3,
            MaxTokens = 2000,
            SystemPrompt = GetFindingExtractionSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<FindingExtractionResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Findings is null || response.Findings.Count == 0)
            {
                return [];
            }

            return response.Findings
                .Take(options.MaxFindingsPerSource)
                .Select((f, i) => new Finding
                {
                    Id = $"find_{source.Id}_{i + 1}",
                    Claim = f.Claim,
                    SourceId = source.Id,
                    EvidenceQuote = f.Evidence,
                    VerificationScore = f.Confidence,
                    IsVerified = f.Confidence >= 0.7m,
                    IterationDiscovered = 0, // 상태에서 설정
                    DiscoveredAt = DateTimeOffset.UtcNow
                })
                .ToList();
        }
        catch (Exception ex)
        {
            LogFindingExtractionFailed(_logger, ex, source.Id);
            return [];
        }
    }

    /// <summary>
    /// 정보 갭 식별
    /// </summary>
    private async Task<IReadOnlyList<InformationGap>> IdentifyGapsAsync(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildGapAnalysisPrompt(state, findings, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.4,
            MaxTokens = 1500,
            SystemPrompt = GetGapAnalysisSystemPrompt(options.Language)
        };

        try
        {
            var response = await _textService.GenerateStructuredAsync<GapAnalysisResponse>(
                prompt, genOptions, cancellationToken);

            if (response?.Gaps is null || response.Gaps.Count == 0)
            {
                return [];
            }

            return response.Gaps
                .Take(options.MaxGaps)
                .Select(g => new InformationGap
                {
                    Description = g.Description,
                    SuggestedQuery = g.SuggestedQuery,
                    Priority = ParseGapPriority(g.Priority),
                    IdentifiedAt = DateTimeOffset.UtcNow
                })
                .ToList();
        }
        catch (Exception ex)
        {
            LogGapAnalysisFailed(_logger, ex);
            return [];
        }
    }

    /// <summary>
    /// 충분성 평가 (LLM-as-Judge)
    /// </summary>
    private async Task<SufficiencyScore> EvaluateSufficiencyAsync(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<InformationGap> gaps,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        // 기본 메트릭 계산
        var sourceDiversity = CalculateSourceDiversity(state.CollectedSources);
        var freshness = CalculateFreshnessScore(state.CollectedSources);
        var newFindingsCount = findings.Count;

        // LLM 기반 품질/커버리지 평가
        var llmEvaluation = await EvaluateWithLLMAsync(
            state, findings, gaps, options, cancellationToken);

        var coverageScore = llmEvaluation?.CoverageScore ?? 0.5m;
        var qualityScore = llmEvaluation?.QualityScore ?? 0.5m;

        // 종합 점수 계산 (가중 평균)
        var overallScore = CalculateOverallScore(
            coverageScore, qualityScore, sourceDiversity, freshness, gaps.Count);

        return new SufficiencyScore
        {
            OverallScore = overallScore,
            CoverageScore = coverageScore,
            QualityScore = qualityScore,
            SourceDiversityScore = sourceDiversity,
            FreshnessScore = freshness,
            NewFindingsCount = newFindingsCount,
            EvaluatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<SufficiencyEvaluationResponse?> EvaluateWithLLMAsync(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<InformationGap> gaps,
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildSufficiencyEvaluationPrompt(state, findings, gaps, options);
        var genOptions = new TextGenerationOptions
        {
            Temperature = 0.2,
            MaxTokens = 1000,
            SystemPrompt = GetSufficiencyEvaluationSystemPrompt(options.Language)
        };

        try
        {
            return await _textService.GenerateStructuredAsync<SufficiencyEvaluationResponse>(
                prompt, genOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            LogLlmSufficiencyEvaluationFailed(_logger, ex);
            return null;
        }
    }

    #region Prompt Building

    private static string BuildFindingExtractionPrompt(
        SourceDocument source,
        string originalQuery,
        IReadOnlyList<string>? subQuestions,
        AnalysisOptions options)
    {
        var subQuestionsText = subQuestions != null && subQuestions.Count > 0
            ? $"\n관련 하위 질문:\n{string.Join("\n", subQuestions.Select((q, i) => $"- {q}"))}"
            : "";

        var contentPreview = source.Content.Length > 3000
            ? source.Content[..3000] + "..."
            : source.Content;

        return $$"""
            원본 연구 질문: {{originalQuery}}
            {{subQuestionsText}}

            소스 정보:
            - URL: {{source.Url}}
            - 제목: {{source.Title}}

            소스 콘텐츠:
            {{contentPreview}}

            위 소스에서 연구 질문과 관련된 주요 발견 사항(Finding)을 추출하세요.

            규칙:
            1. 각 Finding은 구체적인 사실 또는 주장이어야 합니다
            2. 가능하면 원문에서 증거 인용을 포함하세요
            3. 신뢰도(confidence)를 0-1 사이로 평가하세요
            4. 최대 {{options.MaxFindingsPerSource}}개의 Finding을 추출하세요

            JSON 형식으로 응답:
            {
              "findings": [
                {
                  "claim": "발견된 사실 또는 주장",
                  "evidence": "원문 인용 (선택사항)",
                  "confidence": 0.8,
                  "relatedSubQuestion": "관련 하위 질문 (선택사항)"
                }
              ]
            }
            """;
    }

    private static string BuildGapAnalysisPrompt(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        AnalysisOptions options)
    {
        var findingSummary = string.Join("\n",
            findings.Take(10).Select(f => $"- {f.Claim}"));

        var anglesSummary = state.ResearchAngles.Count > 0
            ? $"\n탐구 관점:\n{string.Join("\n", state.ResearchAngles.Select(a => $"- {a}"))}"
            : "";

        return $$"""
            연구 질문: {{state.Request.Query}}
            {{anglesSummary}}

            현재까지 발견된 정보:
            {{findingSummary}}

            수집된 소스 수: {{state.CollectedSources.Count}}

            위 정보를 바탕으로 아직 답변되지 않은 정보 갭을 식별하세요.

            규칙:
            1. 원본 질문에 완전히 답하기 위해 부족한 정보를 식별
            2. 각 갭에 대해 검색할 수 있는 쿼리를 제안
            3. 우선순위를 high/medium/low로 설정
            4. 최대 {{options.MaxGaps}}개의 갭을 식별

            JSON 형식으로 응답:
            {
              "gaps": [
                {
                  "description": "부족한 정보 설명",
                  "suggestedQuery": "검색 쿼리",
                  "priority": "high|medium|low",
                  "reason": "이 정보가 필요한 이유"
                }
              ],
              "coverageEstimate": 0.7,
              "summary": "현재 정보 상태 요약"
            }
            """;
    }

    private static string BuildSufficiencyEvaluationPrompt(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<InformationGap> gaps,
        AnalysisOptions options)
    {
        var findingSummary = string.Join("\n",
            findings.Take(15).Select(f => $"- {f.Claim} (신뢰도: {f.VerificationScore:P0})"));

        var gapSummary = string.Join("\n",
            gaps.Select(g => $"- [{g.Priority}] {g.Description}"));

        return $$"""
            연구 질문: {{state.Request.Query}}

            === 수집된 정보 ===
            소스 수: {{state.CollectedSources.Count}}
            발견 사항 수: {{findings.Count}}

            주요 발견 사항:
            {{findingSummary}}

            === 식별된 정보 갭 ===
            {{gapSummary}}

            위 정보를 바탕으로 연구 질문에 대한 충분성을 평가하세요.

            평가 기준:
            1. 커버리지: 질문의 핵심 측면이 얼마나 다뤄졌는가 (0-1)
            2. 품질: 정보의 신뢰성과 깊이 (0-1)
            3. 소스 다양성: 다양한 관점이 포함되었는가 (0-1)

            JSON 형식으로 응답:
            {
              "overallScore": 0.75,
              "coverageScore": 0.8,
              "qualityScore": 0.7,
              "sourceDiversityScore": 0.75,
              "reasoning": "평가 근거",
              "strengthAreas": ["잘 다뤄진 영역"],
              "weakAreas": ["보완이 필요한 영역"]
            }
            """;
    }

    private static string GetFindingExtractionSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 텍스트에서 핵심 정보를 추출하는 전문가입니다. 항상 JSON 형식으로 응답하세요."
            : "You are an expert at extracting key information from text. Always respond in JSON format.";
    }

    private static string GetGapAnalysisSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 연구 분석 전문가로, 정보의 완전성을 평가하고 부족한 부분을 식별합니다. 항상 JSON 형식으로 응답하세요."
            : "You are a research analyst who evaluates information completeness and identifies gaps. Always respond in JSON format.";
    }

    private static string GetSufficiencyEvaluationSystemPrompt(string language)
    {
        return language == "ko"
            ? "당신은 연구 품질 평가 전문가입니다. 객관적이고 일관된 기준으로 정보의 충분성을 평가하세요. 항상 JSON 형식으로 응답하세요."
            : "You are a research quality evaluator. Assess information sufficiency with objective and consistent criteria. Always respond in JSON format.";
    }

    #endregion

    #region Helper Methods

    private static List<Finding> DeduplicateFindings(List<Finding> findings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Finding>();

        foreach (var finding in findings.OrderByDescending(f => f.VerificationScore))
        {
            // 간단한 중복 체크: 주장의 처음 50자
            var key = finding.Claim.Length > 50
                ? finding.Claim[..50].ToLowerInvariant()
                : finding.Claim.ToLowerInvariant();

            if (seen.Add(key))
            {
                result.Add(finding);
            }
        }

        return result;
    }

    private static decimal CalculateSourceDiversity(List<SourceDocument> sources)
    {
        if (sources.Count == 0)
        {
            return 0;
        }

        // 도메인 다양성
        var domains = sources
            .Select(s => GetDomain(s.Url))
            .Distinct()
            .Count();

        var domainDiversity = Math.Min(1.0m, domains / 5.0m); // 5개 이상 도메인이면 만점

        // 프로바이더 다양성
        var providers = sources.Select(s => s.Provider).Distinct().Count();
        var providerDiversity = Math.Min(1.0m, providers / 3.0m);

        return (domainDiversity + providerDiversity) / 2;
    }

    private static decimal CalculateFreshnessScore(List<SourceDocument> sources)
    {
        if (sources.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var scores = sources
            .Where(s => s.PublishedDate.HasValue)
            .Select(s =>
            {
                var age = now - s.PublishedDate!.Value;
                return age.TotalDays switch
                {
                    <= 7 => 1.0m,      // 1주 이내
                    <= 30 => 0.9m,     // 1달 이내
                    <= 90 => 0.7m,     // 3달 이내
                    <= 365 => 0.5m,    // 1년 이내
                    _ => 0.3m          // 1년 이상
                };
            })
            .ToList();

        if (scores.Count == 0)
        {
            return 0.5m; // 날짜 정보 없으면 중간 점수
        }

        return scores.Average();
    }

    private static decimal CalculateOverallScore(
        decimal coverage, decimal quality, decimal diversity, decimal freshness, int gapCount)
    {
        // 가중 평균 (커버리지와 품질에 더 높은 가중치)
        var baseScore = coverage * 0.35m + quality * 0.30m + diversity * 0.20m + freshness * 0.15m;

        // 갭이 많으면 감점
        var gapPenalty = Math.Min(0.2m, gapCount * 0.04m);

        return Math.Clamp(baseScore - gapPenalty, 0, 1);
    }

    private static string GetDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }

    private static GapPriority ParseGapPriority(string priority)
    {
        return priority?.ToLowerInvariant() switch
        {
            "high" => GapPriority.High,
            "low" => GapPriority.Low,
            _ => GapPriority.Medium
        };
    }

    private static void UpdateState(
        ResearchState state,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<InformationGap> gaps,
        SufficiencyScore score)
    {
        // Findings 추가 (반복 번호 설정)
        foreach (var finding in findings)
        {
            state.Findings.Add(finding with { IterationDiscovered = state.CurrentIteration });
        }

        // 갭 추가
        foreach (var gap in gaps)
        {
            state.IdentifiedGaps.Add(gap);
        }

        // 충분성 점수 업데이트
        state.LastSufficiencyScore = score;
    }

    private AnalysisOptions CreateDefaultOptions(ResearchState state)
    {
        return new AnalysisOptions
        {
            MaxFindingsPerSource = 5,
            MaxGaps = 5,
            SufficiencyThreshold = _researchOptions.SufficiencyThreshold,
            Language = state.Request.Language,
            MaxSourcesToAnalyze = 20
        };
    }

    #endregion

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "분석 시작: 소스 {SourceCount}개, 반복 {Iteration}")]
    private static partial void LogAnalysisStarting(ILogger logger, int sourceCount, int iteration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Finding {Count}개 추출됨")]
    private static partial void LogFindingsExtracted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "정보 갭 {Count}개 식별됨")]
    private static partial void LogGapsIdentified(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "충분성 점수: {Score}")]
    private static partial void LogSufficiencyScore(ILogger logger, decimal score);

    [LoggerMessage(Level = LogLevel.Information, Message = "분석 완료: Finding {FindingCount}개, 갭 {GapCount}개, 충분성 {Score:P0}, 소요 시간 {Duration}ms")]
    private static partial void LogAnalysisCompleted(ILogger logger, int findingCount, int gapCount, decimal score, double duration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "소스 분석 실패: {SourceId}")]
    private static partial void LogSourceAnalysisFailed(ILogger logger, Exception? exception, string sourceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finding 추출 실패: {SourceId}")]
    private static partial void LogFindingExtractionFailed(ILogger logger, Exception? exception, string sourceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "갭 분석 실패")]
    private static partial void LogGapAnalysisFailed(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM 충분성 평가 실패")]
    private static partial void LogLlmSufficiencyEvaluationFailed(ILogger logger, Exception? exception);

    #endregion
}
