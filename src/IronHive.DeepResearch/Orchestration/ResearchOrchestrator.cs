using System.Runtime.CompilerServices;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Report;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration.Agents;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch.Orchestration;

/// <summary>
/// 리서치 오케스트레이터: 전체 파이프라인 조율
/// </summary>
public class ResearchOrchestrator
{
    private readonly QueryPlannerAgent _queryPlanner;
    private readonly SearchCoordinatorAgent _searchCoordinator;
    private readonly ContentEnrichmentAgent _contentEnrichment;
    private readonly AnalysisAgent _analysisAgent;
    private readonly ReportGeneratorAgent _reportGenerator;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<ResearchOrchestrator> _logger;

    public ResearchOrchestrator(
        QueryPlannerAgent queryPlanner,
        SearchCoordinatorAgent searchCoordinator,
        ContentEnrichmentAgent contentEnrichment,
        AnalysisAgent analysisAgent,
        ReportGeneratorAgent reportGenerator,
        DeepResearchOptions options,
        ILogger<ResearchOrchestrator> logger)
    {
        _queryPlanner = queryPlanner;
        _searchCoordinator = searchCoordinator;
        _contentEnrichment = contentEnrichment;
        _analysisAgent = analysisAgent;
        _reportGenerator = reportGenerator;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 리서치 실행 (동기)
    /// </summary>
    public virtual async Task<ResearchResult> ExecuteAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var state = CreateInitialState(request);

        _logger.LogInformation("리서치 시작: {Query}, 깊이: {Depth}", request.Query, request.Depth);

        // 시작 시 취소 상태 확인
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("리서치 시작 전 취소됨");
            state.CurrentPhase = ResearchPhase.Failed;
            return BuildPartialResult(state, "리서치가 시작 전에 취소되었습니다.");
        }

        try
        {
            // 반복 실행
            var maxIterations = GetMaxIterations(request);
            while (state.CurrentIteration < maxIterations && !cancellationToken.IsCancellationRequested)
            {
                state.CurrentIteration++;
                _logger.LogInformation("반복 {Iteration}/{Max} 시작", state.CurrentIteration, maxIterations);

                // 1. 계획 단계 (첫 번째 반복 또는 갭 기반)
                await ExecutePlanningPhaseAsync(state, cancellationToken);

                // 2. 검색 단계
                await ExecuteSearchPhaseAsync(state, cancellationToken);

                // 3. 콘텐츠 강화 단계
                await ExecuteContentEnrichmentPhaseAsync(state, cancellationToken);

                // 4. 분석 및 충분성 평가
                var analysisResult = await ExecuteAnalysisPhaseAsync(state, cancellationToken);

                // 5. 충분성 확인
                if (!analysisResult.NeedsMoreResearch)
                {
                    _logger.LogInformation("충분성 달성 (점수: {Score:P0})", analysisResult.SufficiencyScore.OverallScore);
                    break;
                }

                _logger.LogInformation("추가 리서치 필요 (점수: {Score:P0}, 갭: {GapCount}개)",
                    analysisResult.SufficiencyScore.OverallScore, analysisResult.Gaps.Count);
            }

            // 6. 보고서 생성
            var reportResult = await ExecuteReportGenerationPhaseAsync(state, cancellationToken);

            // 7. 최종 결과 생성
            return BuildResult(state, reportResult);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("리서치 취소됨");
            state.CurrentPhase = ResearchPhase.Failed;
            return BuildPartialResult(state, "리서치가 취소되었습니다.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "리서치 실행 중 오류 발생");
            state.CurrentPhase = ResearchPhase.Failed;
            state.Errors.Add(new ResearchError
            {
                Type = ResearchErrorType.Unknown,
                Message = ex.Message,
                OccurredAt = DateTimeOffset.UtcNow,
                Details = ex.ToString()
            });
            return BuildPartialResult(state, ex.Message);
        }
    }

    /// <summary>
    /// 리서치 실행 (스트리밍)
    /// </summary>
    public virtual async IAsyncEnumerable<ResearchProgress> ExecuteStreamAsync(
        ResearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = CreateInitialState(request);
        var maxIterations = GetMaxIterations(request);
        var progressList = new List<ResearchProgress>();
        Exception? caughtException = null;

        // 시작 이벤트
        yield return CreateProgress(state, ProgressType.Started, maxIterations);

        // try 블록 내에서 yield를 사용할 수 없으므로 결과를 수집
        try
        {
            while (state.CurrentIteration < maxIterations && !cancellationToken.IsCancellationRequested)
            {
                state.CurrentIteration++;

                // 1. 계획 단계
                state.CurrentPhase = ResearchPhase.Planning;
                await ExecutePlanningPhaseAsync(state, cancellationToken);
                progressList.Add(CreateProgress(state, ProgressType.PlanGenerated, maxIterations,
                    plan: new PlanProgress
                    {
                        GeneratedQueries = state.ExecutedQueries.Select(q => q.Query).ToList(),
                        ResearchAngles = state.ResearchAngles
                    }));

                // 2. 검색 단계
                state.CurrentPhase = ResearchPhase.Searching;
                progressList.Add(CreateProgress(state, ProgressType.SearchStarted, maxIterations));

                var searchResult = await ExecuteSearchPhaseInternalAsync(state, cancellationToken);

                foreach (var result in searchResult.SuccessfulResults)
                {
                    progressList.Add(CreateProgress(state, ProgressType.SearchCompleted, maxIterations,
                        search: new SearchProgress
                        {
                            Query = result.Query.Query,
                            Provider = result.Provider,
                            ResultCount = result.Sources.Count
                        }));
                }

                // 3. 콘텐츠 강화 단계
                state.CurrentPhase = ResearchPhase.ContentExtraction;
                progressList.Add(CreateProgress(state, ProgressType.ContentExtractionStarted, maxIterations));

                await ExecuteContentEnrichmentPhaseAsync(state, cancellationToken);

                // 4. 분석 단계
                state.CurrentPhase = ResearchPhase.Analysis;
                progressList.Add(CreateProgress(state, ProgressType.AnalysisStarted, maxIterations));

                var analysisResult = await ExecuteAnalysisPhaseAsync(state, cancellationToken);

                progressList.Add(CreateProgress(state, ProgressType.AnalysisCompleted, maxIterations,
                    analysis: new AnalysisProgress
                    {
                        FindingsCount = analysisResult.Findings.Count,
                        GapsIdentified = analysisResult.Gaps.Count,
                        Score = analysisResult.SufficiencyScore
                    }));

                // 반복 완료 체크포인트
                progressList.Add(CreateProgress(state, ProgressType.IterationCompleted, maxIterations));

                // 충분성 확인
                if (!analysisResult.NeedsMoreResearch)
                {
                    break;
                }
            }

            // 5. 보고서 생성
            state.CurrentPhase = ResearchPhase.ReportGeneration;
            progressList.Add(CreateProgress(state, ProgressType.ReportGenerationStarted, maxIterations));

            var reportResult = await ExecuteReportGenerationPhaseAsync(state, cancellationToken);

            // 섹션별 청크 저장
            foreach (var section in reportResult.Sections)
            {
                progressList.Add(CreateProgress(state, ProgressType.ReportSection, maxIterations,
                    reportChunk: $"## {section.Title}\n\n{section.Content}"));
            }

            // 완료
            state.CurrentPhase = ResearchPhase.Completed;
            var finalResult = BuildResult(state, reportResult);
            progressList.Add(CreateProgress(state, ProgressType.Completed, maxIterations, result: finalResult));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "스트리밍 리서치 중 오류");
            caughtException = ex;
        }

        // try 블록 외부에서 yield
        foreach (var progress in progressList)
        {
            yield return progress;
        }

        // 예외 발생 시 실패 이벤트 yield
        if (caughtException != null)
        {
            yield return CreateProgress(state, ProgressType.Failed, maxIterations,
                error: new ResearchError
                {
                    Type = ResearchErrorType.Unknown,
                    Message = caughtException.Message,
                    OccurredAt = DateTimeOffset.UtcNow
                });
        }
    }

    #region Phase Execution

    private async Task ExecutePlanningPhaseAsync(ResearchState state, CancellationToken cancellationToken)
    {
        state.CurrentPhase = ResearchPhase.Planning;

        if (state.CurrentIteration == 1)
        {
            // 첫 번째 반복: 초기 쿼리 계획
            var planResult = await _queryPlanner.PlanAsync(state, cancellationToken);

            // ExpandedQuery를 SearchQuery로 변환하여 저장
            foreach (var query in planResult.InitialQueries)
            {
                state.ExecutedQueries.Add(new SearchQuery
                {
                    Query = query.Query,
                    Type = query.SearchType switch
                    {
                        QuerySearchType.News => SearchType.News,
                        QuerySearchType.Academic => SearchType.Academic,
                        _ => SearchType.Web
                    }
                });
            }

            // 관점 저장
            state.ResearchAngles.AddRange(planResult.Perspectives.Select(p => p.Name));
        }
        else
        {
            // 후속 반복: 갭 기반 쿼리 생성
            var gapQueries = state.IdentifiedGaps
                .Where(g => g.Priority != GapPriority.Low)
                .Select(g => new SearchQuery
                {
                    Query = g.SuggestedQuery,
                    Type = SearchType.Web
                })
                .Take(3)
                .ToList();

            state.ExecutedQueries.AddRange(gapQueries);

            // 처리된 갭 제거
            state.IdentifiedGaps.Clear();
        }
    }

    private async Task ExecuteSearchPhaseAsync(ResearchState state, CancellationToken cancellationToken)
    {
        state.CurrentPhase = ResearchPhase.Searching;

        var retryCount = 0;
        var maxRetries = _options.MaxSearchRetriesPerIteration;

        while (retryCount <= maxRetries && !cancellationToken.IsCancellationRequested)
        {
            var searchResult = await ExecuteSearchPhaseInternalAsync(state, cancellationToken);

            // 에러 기록
            foreach (var failed in searchResult.FailedSearches)
            {
                state.Errors.Add(new ResearchError
                {
                    Type = ResearchErrorType.SearchProviderError,
                    Message = failed.ErrorMessage,
                    OccurredAt = DateTimeOffset.UtcNow
                });
            }

            // 소스를 얻었거나 최대 재시도 도달하면 종료
            if (searchResult.UniqueSourcesCollected > 0 || retryCount >= maxRetries)
            {
                if (searchResult.UniqueSourcesCollected == 0 && retryCount >= maxRetries)
                {
                    _logger.LogWarning("검색 재시도 {RetryCount}회 후에도 소스를 찾지 못함", retryCount);
                    state.AddThinkingStep(
                        ThinkingStepType.Searching,
                        "검색 결과 없음",
                        $"검색 재시도 {retryCount}회 후에도 소스를 찾지 못했습니다. 봇 보호 또는 네트워크 문제일 수 있습니다.",
                        data: new Dictionary<string, object> { ["retryCount"] = retryCount });
                }
                break;
            }

            // 재시도 대기 (봇 보호 우회)
            retryCount++;
            _logger.LogInformation("검색 결과 없음, {Delay}초 후 재시도 ({Retry}/{Max})",
                _options.RetryDelayOnNoResults.TotalSeconds, retryCount, maxRetries);

            state.AddThinkingStep(
                ThinkingStepType.Searching,
                $"검색 재시도 {retryCount}/{maxRetries}",
                $"검색 결과가 없어서 {_options.RetryDelayOnNoResults.TotalSeconds}초 대기 후 재시도합니다.");

            await Task.Delay(_options.RetryDelayOnNoResults, cancellationToken);
        }
    }

    private async Task<SearchExecutionResult> ExecuteSearchPhaseInternalAsync(
        ResearchState state, CancellationToken cancellationToken)
    {
        // 이번 반복에서 실행할 쿼리를 ExpandedQuery로 변환
        var queriesToExecute = state.ExecutedQueries
            .Skip(state.SearchResults.Count)
            .Take(state.Request.MaxSourcesPerIteration)
            .Select(q => new ExpandedQuery
            {
                Query = q.Query,
                Intent = "Search",
                Priority = 1,
                SearchType = q.Type switch
                {
                    SearchType.News => QuerySearchType.News,
                    SearchType.Academic => QuerySearchType.Academic,
                    _ => QuerySearchType.Web
                }
            })
            .ToList();

        if (queriesToExecute.Count == 0)
        {
            return new SearchExecutionResult
            {
                SuccessfulResults = [],
                FailedSearches = [],
                TotalQueriesExecuted = 0,
                UniqueSourcesCollected = 0,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        var searchResult = await _searchCoordinator.ExecuteSearchesAsync(
            queriesToExecute, cancellationToken: cancellationToken);

        state.SearchResults.AddRange(searchResult.SuccessfulResults);

        return searchResult;
    }

    private async Task ExecuteContentEnrichmentPhaseAsync(ResearchState state, CancellationToken cancellationToken)
    {
        state.CurrentPhase = ResearchPhase.ContentExtraction;

        // 최근 검색 결과에서 콘텐츠 강화
        var recentResults = state.SearchResults
            .Skip(Math.Max(0, state.SearchResults.Count - state.Request.MaxSourcesPerIteration))
            .ToList();

        if (recentResults.Count == 0) return;

        var enrichmentResult = await _contentEnrichment.EnrichSearchResultsAsync(
            recentResults, cancellationToken: cancellationToken);

        // 새 소스 추가 (중복 제거)
        var existingUrls = state.CollectedSources.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in enrichmentResult.Documents)
        {
            if (!existingUrls.Contains(doc.Url))
            {
                state.CollectedSources.Add(doc);
                existingUrls.Add(doc.Url);
            }
        }
    }

    private async Task<AnalysisResult> ExecuteAnalysisPhaseAsync(ResearchState state, CancellationToken cancellationToken)
    {
        state.CurrentPhase = ResearchPhase.Analysis;

        var analysisResult = await _analysisAgent.AnalyzeAsync(state, cancellationToken: cancellationToken);

        state.CurrentPhase = ResearchPhase.SufficiencyEvaluation;

        return analysisResult;
    }

    private async Task<ReportGenerationResult> ExecuteReportGenerationPhaseAsync(
        ResearchState state, CancellationToken cancellationToken)
    {
        state.CurrentPhase = ResearchPhase.ReportGeneration;

        var reportOptions = new ReportGenerationOptions
        {
            OutputFormat = state.Request.OutputFormat,
            Language = state.Request.Language
        };

        return await _reportGenerator.GenerateReportAsync(state, reportOptions, cancellationToken: cancellationToken);
    }

    #endregion

    #region Helper Methods

    private ResearchState CreateInitialState(ResearchRequest request)
    {
        return new ResearchState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Request = request,
            StartedAt = DateTimeOffset.UtcNow
        };
    }

    private int GetMaxIterations(ResearchRequest request)
    {
        return request.Depth switch
        {
            ResearchDepth.Quick => Math.Min(request.MaxIterations, 2),
            ResearchDepth.Standard => Math.Min(request.MaxIterations, 5),
            ResearchDepth.Comprehensive => Math.Min(request.MaxIterations, 10),
            _ => request.MaxIterations
        };
    }

    private ResearchResult BuildResult(ResearchState state, ReportGenerationResult reportResult)
    {
        state.CurrentPhase = ResearchPhase.Completed;

        // 인용된 소스와 인용되지 않은 소스 분리
        var citedSourceIds = reportResult.Citations.Select(c => c.SourceId).ToHashSet();
        var citedSources = state.CollectedSources.Where(s => citedSourceIds.Contains(s.Id)).ToList();
        var uncitedSources = state.CollectedSources.Where(s => !citedSourceIds.Contains(s.Id)).ToList();

        return new ResearchResult
        {
            SessionId = state.SessionId,
            Query = state.Request.Query,
            Report = reportResult.Report,
            Sections = reportResult.Sections,
            CitedSources = citedSources,
            UncitedSources = uncitedSources,
            Citations = reportResult.Citations,
            ThinkingProcess = state.ThinkingSteps,
            Metadata = new ResearchMetadata
            {
                IterationCount = state.CurrentIteration,
                TotalQueriesExecuted = state.ExecutedQueries.Count,
                TotalSourcesAnalyzed = state.CollectedSources.Count,
                Duration = DateTimeOffset.UtcNow - state.StartedAt,
                TokenUsage = state.AccumulatedTokenUsage,
                EstimatedCost = state.AccumulatedCost,
                FinalSufficiencyScore = state.LastSufficiencyScore ?? new SufficiencyScore()
            },
            Errors = state.Errors,
            IsPartial = false
        };
    }

    private ResearchResult BuildPartialResult(ResearchState state, string errorMessage)
    {
        // 가능한 경우 부분 보고서 생성
        var partialReport = state.GeneratedSections.Count > 0
            ? string.Join("\n\n", state.GeneratedSections.Select(s => $"## {s.Title}\n\n{s.Content}"))
            : $"리서치가 완료되지 않았습니다: {errorMessage}";

        return new ResearchResult
        {
            SessionId = state.SessionId,
            Query = state.Request.Query,
            Report = partialReport,
            Sections = state.GeneratedSections,
            CitedSources = [],
            UncitedSources = state.CollectedSources.ToList(),
            Citations = [],
            ThinkingProcess = state.ThinkingSteps,
            Metadata = new ResearchMetadata
            {
                IterationCount = state.CurrentIteration,
                TotalQueriesExecuted = state.ExecutedQueries.Count,
                TotalSourcesAnalyzed = state.CollectedSources.Count,
                Duration = DateTimeOffset.UtcNow - state.StartedAt,
                TokenUsage = state.AccumulatedTokenUsage,
                EstimatedCost = state.AccumulatedCost,
                FinalSufficiencyScore = state.LastSufficiencyScore ?? new SufficiencyScore()
            },
            Errors = state.Errors,
            IsPartial = true
        };
    }

    private static ResearchProgress CreateProgress(
        ResearchState state,
        ProgressType type,
        int maxIterations,
        PlanProgress? plan = null,
        SearchProgress? search = null,
        ContentProgress? content = null,
        AnalysisProgress? analysis = null,
        string? reportChunk = null,
        ResearchResult? result = null,
        ResearchError? error = null)
    {
        return new ResearchProgress
        {
            Type = type,
            CurrentIteration = state.CurrentIteration,
            MaxIterations = maxIterations,
            Timestamp = DateTimeOffset.UtcNow,
            Plan = plan,
            Search = search,
            Content = content,
            Analysis = analysis,
            ReportChunk = reportChunk,
            Result = result,
            Error = error
        };
    }

    #endregion
}
