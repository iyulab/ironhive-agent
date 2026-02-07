using System.Text.Json;
using IronHive.DeepResearch.Abstractions;
using Microsoft.Extensions.AI;

namespace IronHive.DeepResearch.Adapters;

/// <summary>
/// Microsoft.Extensions.AI의 IChatClient를 DeepResearch ITextGenerationService로 어댑트.
/// IronHive.Agent가 IChatClient 기반이므로 자연스러운 통합 경로를 제공합니다.
/// </summary>
public class ChatClientTextGenerationAdapter : ITextGenerationService
{
    private readonly IChatClient _chatClient;
    private readonly IResearchUsageCallback? _usageCallback;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatClientTextGenerationAdapter(
        IChatClient chatClient,
        IResearchUsageCallback? usageCallback = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _usageCallback = usageCallback;
    }

    /// <inheritdoc />
    public async Task<TextGenerationResult> GenerateAsync(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(options?.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, options.SystemPrompt));
        }

        messages.Add(new ChatMessage(ChatRole.User, prompt));

        var chatOptions = new ChatOptions
        {
            Temperature = (float?)(options?.Temperature),
            MaxOutputTokens = options?.MaxTokens
        };

        var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);

        // 토큰 사용량 콜백
        var usage = response.Usage;
        if (usage != null)
        {
            _usageCallback?.OnTokensUsed(
                (int)(usage.InputTokenCount ?? 0),
                (int)(usage.OutputTokenCount ?? 0));
        }

        var text = response.Text ?? string.Empty;

        return new TextGenerationResult
        {
            Text = text,
            TokenUsage = usage != null
                ? new TokenUsageInfo
                {
                    PromptTokens = (int)(usage.InputTokenCount ?? 0),
                    CompletionTokens = (int)(usage.OutputTokenCount ?? 0)
                }
                : null,
            FinishReason = response.FinishReason?.ToString()
        };
    }

    /// <inheritdoc />
    public async Task<T?> GenerateStructuredAsync<T>(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var jsonPrompt = $"{prompt}\n\nRespond ONLY with valid JSON. No markdown code blocks or other text.";

        var result = await GenerateAsync(jsonPrompt, options, cancellationToken);

        try
        {
            var json = IronHiveTextGenerationAdapter.ExtractJsonFromText(result.Text);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
