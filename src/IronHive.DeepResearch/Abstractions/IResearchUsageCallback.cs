namespace IronHive.DeepResearch.Abstractions;

/// <summary>
/// 토큰 사용량 콜백 인터페이스.
/// DeepResearch가 IronHive.Agent의 IUsageTracker를 직접 참조하지 않되,
/// 소비 앱에서 연결할 수 있도록 느슨한 결합을 제공합니다.
/// </summary>
public interface IResearchUsageCallback
{
    /// <summary>
    /// LLM 호출 시 소비된 토큰 수를 보고
    /// </summary>
    void OnTokensUsed(int inputTokens, int outputTokens);
}
