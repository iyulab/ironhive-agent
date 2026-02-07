using System.Collections.Concurrent;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Orchestration;
using IronHive.DeepResearch.Orchestration.State;
using Microsoft.Extensions.Logging;

namespace IronHive.DeepResearch;

/// <summary>
/// 딥리서치 메인 파사드 구현
/// </summary>
public class DeepResearcher : IDeepResearcher
{
    private readonly ResearchOrchestrator _orchestrator;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<DeepResearcher> _logger;

    // 세션 저장소 (메모리 기반, 프로덕션에서는 분산 캐시 사용)
    private readonly ConcurrentDictionary<string, ResearchSession> _sessions = new();

    public DeepResearcher(
        ResearchOrchestrator orchestrator,
        DeepResearchOptions options,
        ILogger<DeepResearcher> logger)
    {
        _orchestrator = orchestrator;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ResearchResult> ResearchAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("리서치 시작: {Query}", TruncateQuery(request.Query));

        var result = await _orchestrator.ExecuteAsync(request, cancellationToken);

        _logger.LogInformation("리서치 완료: {SessionId}, 소스 {SourceCount}개, 반복 {Iterations}회",
            result.SessionId, result.AllSources.Count, result.Metadata.IterationCount);

        return result;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ResearchProgress> ResearchStreamAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("스트리밍 리서치 시작: {Query}", TruncateQuery(request.Query));
        return _orchestrator.ExecuteStreamAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IResearchSession> StartInteractiveAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("대화형 리서치 세션 시작: {Query}", TruncateQuery(request.Query));

        var state = new ResearchState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Request = request,
            StartedAt = DateTimeOffset.UtcNow
        };

        var session = new ResearchSession(state, _orchestrator, _logger);
        _sessions[state.SessionId] = session;

        // 초기 계획 생성
        await session.InitializeAsync(cancellationToken);

        return session;
    }

    /// <inheritdoc />
    public async Task<ResearchResult> ResumeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"세션을 찾을 수 없습니다: {sessionId}");
        }

        _logger.LogInformation("리서치 재개: {SessionId}", sessionId);

        try
        {
            return await session.FinalizeAsync();
        }
        finally
        {
            // 완료된 세션을 제거하여 메모리 누수 방지
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private static string TruncateQuery(string query)
    {
        return query.Length > 50 ? query[..50] + "..." : query;
    }
}

/// <summary>
/// 대화형 리서치 세션 구현
/// </summary>
public class ResearchSession : IResearchSession
{
    private readonly ResearchState _state;
    private readonly ResearchOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private bool _isDisposed;
    private bool _isComplete;

    public ResearchSession(
        ResearchState state,
        ResearchOrchestrator orchestrator,
        ILogger logger)
    {
        _state = state;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SessionId => _state.SessionId;

    /// <inheritdoc />
    public ResearchState CurrentState => _state;

    /// <inheritdoc />
    public bool IsComplete => _isComplete;

    /// <summary>
    /// 세션 초기화 (내부 사용)
    /// </summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // 첫 번째 반복의 계획 단계만 실행
        _state.CurrentIteration = 1;
        _state.CurrentPhase = ResearchPhase.Planning;

        // 계획 수립은 오케스트레이터에서 처리
        _logger.LogDebug("세션 초기화 완료: {SessionId}", SessionId);
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResearchCheckpoint> GetCheckpointAsync()
    {
        var checkpoint = new ResearchCheckpoint
        {
            SessionId = _state.SessionId,
            CheckpointNumber = _state.CurrentIteration,
            CreatedAt = DateTimeOffset.UtcNow,
            State = _state,
            KeyFindings = _state.Findings
                .OrderByDescending(f => f.VerificationScore)
                .Take(5)
                .ToList(),
            SuggestedQueries = _state.IdentifiedGaps
                .OrderByDescending(g => g.Priority)
                .Select(g => g.SuggestedQuery)
                .Take(3)
                .ToList(),
            Summary = GenerateSummary()
        };

        return Task.FromResult(checkpoint);
    }

    /// <inheritdoc />
    public async Task ContinueAsync()
    {
        if (_isComplete)
        {
            throw new InvalidOperationException("세션이 이미 완료되었습니다.");
        }

        _logger.LogInformation("세션 계속 진행: {SessionId}, 반복 {Iteration}",
            SessionId, _state.CurrentIteration + 1);

        // 다음 반복 실행 (전체 파이프라인 1회)
        // 실제 구현에서는 오케스트레이터의 단일 반복 메서드 호출
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddQueryAsync(string customQuery)
    {
        if (_isComplete)
        {
            throw new InvalidOperationException("세션이 이미 완료되었습니다.");
        }

        _state.ExecutedQueries.Add(new SearchQuery
        {
            Query = customQuery,
            Type = SearchType.Web
        });

        _logger.LogDebug("사용자 쿼리 추가: {Query}", customQuery);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ResearchResult> FinalizeAsync()
    {
        if (_isComplete)
        {
            throw new InvalidOperationException("세션이 이미 완료되었습니다.");
        }

        _logger.LogInformation("세션 종료 및 보고서 생성: {SessionId}", SessionId);

        // 남은 작업 완료 및 보고서 생성
        var result = await _orchestrator.ExecuteAsync(_state.Request);

        _isComplete = true;
        return result;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;

        _isDisposed = true;
        _logger.LogDebug("세션 해제: {SessionId}", SessionId);

        return ValueTask.CompletedTask;
    }

    private string GenerateSummary()
    {
        var summary = new System.Text.StringBuilder();

        summary.AppendLine($"## 리서치 진행 상황");
        summary.AppendLine($"- 반복: {_state.CurrentIteration}회");
        summary.AppendLine($"- 수집된 소스: {_state.CollectedSources.Count}개");
        summary.AppendLine($"- 발견 사항: {_state.Findings.Count}개");

        if (_state.LastSufficiencyScore != null)
        {
            summary.AppendLine($"- 충분성 점수: {_state.LastSufficiencyScore.OverallScore:P0}");
        }

        if (_state.IdentifiedGaps.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("### 정보 갭");
            foreach (var gap in _state.IdentifiedGaps.Take(3))
            {
                summary.AppendLine($"- [{gap.Priority}] {gap.Description}");
            }
        }

        return summary.ToString();
    }
}
