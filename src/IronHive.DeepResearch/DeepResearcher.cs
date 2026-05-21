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
public partial class DeepResearcher : IDeepResearcher
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
        LogResearchStarting(_logger, request.Query);

        var result = await _orchestrator.ExecuteAsync(request, cancellationToken);

        LogResearchCompleted(_logger, result.SessionId, result.AllSources.Count, result.Metadata.IterationCount);

        return result;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ResearchProgress> ResearchStreamAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        LogStreamingResearchStarting(_logger, request.Query);
        return _orchestrator.ExecuteStreamAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IResearchSession> StartInteractiveAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default)
    {
        LogInteractiveSessionStarting(_logger, request.Query);

        var state = new ResearchState
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Request = request,
            StartedAt = DateTimeOffset.UtcNow
        };

        var session = new ResearchSession(state, _orchestrator, _logger,
            onDispose: () => _sessions.TryRemove(state.SessionId, out _));
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
            throw new InvalidOperationException($"Session not found: {sessionId}");
        }

        LogResearchResuming(_logger, sessionId);

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

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information, Message = "Research starting: {Query}")]
    private static partial void LogResearchStarting(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research completed: {SessionId}, sources: {SourceCount}, iterations: {Iterations}")]
    private static partial void LogResearchCompleted(ILogger logger, string sessionId, int sourceCount, int iterations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming research starting: {Query}")]
    private static partial void LogStreamingResearchStarting(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Information, Message = "Interactive research session starting: {Query}")]
    private static partial void LogInteractiveSessionStarting(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Information, Message = "Research resuming: {SessionId}")]
    private static partial void LogResearchResuming(ILogger logger, string sessionId);

    #endregion
}

/// <summary>
/// 대화형 리서치 세션 구현
/// </summary>
public partial class ResearchSession : IResearchSession
{
    private readonly ResearchState _state;
    private readonly ResearchOrchestrator _orchestrator;
    private readonly ILogger _logger;
    private readonly Action? _onDispose;
    private bool _isDisposed;
    private bool _isComplete;

    public ResearchSession(
        ResearchState state,
        ResearchOrchestrator orchestrator,
        ILogger logger,
        Action? onDispose = null)
    {
        _state = state;
        _orchestrator = orchestrator;
        _logger = logger;
        _onDispose = onDispose;
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
        LogSessionInitialized(_logger, SessionId);
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
    public Task ContinueAsync()
        => throw new NotImplementedException(
            "Interactive continuation is not yet implemented. " +
            "Use FinalizeAsync() to complete the session or start a new research session.");

    /// <inheritdoc />
    public Task AddQueryAsync(string customQuery)
    {
        if (_isComplete)
        {
            throw new InvalidOperationException("Session is already complete.");
        }

        _state.ExecutedQueries.Add(new SearchQuery
        {
            Query = customQuery,
            Type = SearchType.Web
        });

        LogUserQueryAdded(_logger, customQuery);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ResearchResult> FinalizeAsync()
    {
        if (_isComplete)
        {
            throw new InvalidOperationException("Session is already complete.");
        }

        LogSessionFinalizing(_logger, SessionId);

        // Pass accumulated state so the orchestrator continues from collected sources,
        // findings, queries, and gaps rather than starting from scratch.
        var result = await _orchestrator.ExecuteAsync(_state);

        _isComplete = true;
        return result;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        GC.SuppressFinalize(this);
        _isDisposed = true;
        _onDispose?.Invoke();
        LogSessionDisposed(_logger, SessionId);

        return ValueTask.CompletedTask;
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session initialized: {SessionId}")]
    private static partial void LogSessionInitialized(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "User query added: {Query}")]
    private static partial void LogUserQueryAdded(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session finalizing and generating report: {SessionId}")]
    private static partial void LogSessionFinalizing(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session disposed: {SessionId}")]
    private static partial void LogSessionDisposed(ILogger logger, string sessionId);

    #endregion

    private string GenerateSummary()
    {
        var summary = new System.Text.StringBuilder();

        summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"## 리서치 진행 상황");
        summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- 반복: {_state.CurrentIteration}회");
        summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- 수집된 소스: {_state.CollectedSources.Count}개");
        summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- 발견 사항: {_state.Findings.Count}개");

        if (_state.LastSufficiencyScore != null)
        {
            summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- 충분성 점수: {_state.LastSufficiencyScore.OverallScore:P0}");
        }

        if (_state.IdentifiedGaps.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("### 정보 갭");
            foreach (var gap in _state.IdentifiedGaps.Take(3))
            {
                summary.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"- [{gap.Priority}] {gap.Description}");
            }
        }

        return summary.ToString();
    }
}
