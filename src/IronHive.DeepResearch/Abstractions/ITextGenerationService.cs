namespace IronHive.DeepResearch.Abstractions;

/// <summary>
/// 텍스트 생성 서비스 인터페이스 (LLM 추상화)
/// </summary>
public interface ITextGenerationService
{
    /// <summary>
    /// 프롬프트로부터 텍스트 생성
    /// </summary>
    Task<TextGenerationResult> GenerateAsync(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// JSON 형식으로 구조화된 출력 생성
    /// </summary>
    Task<T?> GenerateStructuredAsync<T>(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;
}

/// <summary>
/// 텍스트 생성 옵션
/// </summary>
public record TextGenerationOptions
{
    /// <summary>
    /// 최대 토큰 수
    /// </summary>
    public int MaxTokens { get; init; } = 2048;

    /// <summary>
    /// 온도 (0.0 ~ 1.0)
    /// </summary>
    public double Temperature { get; init; } = 0.7;

    /// <summary>
    /// 시스템 프롬프트
    /// </summary>
    public string? SystemPrompt { get; init; }
}

/// <summary>
/// 텍스트 생성 결과
/// </summary>
public record TextGenerationResult
{
    /// <summary>
    /// 생성된 텍스트
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// 사용된 토큰 수
    /// </summary>
    public TokenUsageInfo? TokenUsage { get; init; }

    /// <summary>
    /// 완료 이유
    /// </summary>
    public string? FinishReason { get; init; }
}

/// <summary>
/// 토큰 사용량 정보
/// </summary>
public record TokenUsageInfo
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}
