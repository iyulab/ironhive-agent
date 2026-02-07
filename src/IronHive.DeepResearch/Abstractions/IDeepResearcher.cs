namespace IronHive.DeepResearch.Abstractions;

/// <summary>
/// 딥리서치 실행을 위한 메인 인터페이스
/// </summary>
public interface IDeepResearcher
{
    /// <summary>
    /// 동기적 리서치 실행 (완료까지 대기)
    /// </summary>
    Task<ResearchResult> ResearchAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 스트리밍 리서치 실행 (진행 상황 실시간 전달)
    /// </summary>
    IAsyncEnumerable<ResearchProgress> ResearchStreamAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 대화형 리서치 세션 시작 (Human-in-the-Loop)
    /// </summary>
    Task<IResearchSession> StartInteractiveAsync(
        ResearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 체크포인트에서 리서치 재개
    /// </summary>
    Task<ResearchResult> ResumeAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 대화형 리서치 세션
/// </summary>
public interface IResearchSession : IAsyncDisposable
{
    /// <summary>
    /// 세션 ID
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// 현재 리서치 상태
    /// </summary>
    ResearchState CurrentState { get; }

    /// <summary>
    /// 완료 여부
    /// </summary>
    bool IsComplete { get; }

    /// <summary>
    /// 현재 체크포인트 조회
    /// </summary>
    Task<ResearchCheckpoint> GetCheckpointAsync();

    /// <summary>
    /// 리서치 계속 진행
    /// </summary>
    Task ContinueAsync();

    /// <summary>
    /// 사용자 정의 쿼리 추가
    /// </summary>
    Task AddQueryAsync(string customQuery);

    /// <summary>
    /// 리서치 종료 및 결과 반환
    /// </summary>
    Task<ResearchResult> FinalizeAsync();
}
