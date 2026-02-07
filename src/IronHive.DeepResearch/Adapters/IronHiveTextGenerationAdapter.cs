using System.Text.Json;
using System.Text.RegularExpressions;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Abstractions.Messages.Roles;
using IronHive.DeepResearch.Abstractions;

namespace IronHive.DeepResearch.Adapters;

/// <summary>
/// IronHive IMessageGenerator를 DeepResearch ITextGenerationService로 어댑트
/// </summary>
public partial class IronHiveTextGenerationAdapter : ITextGenerationService
{
    private readonly IMessageGenerator _generator;
    private readonly string _modelId;
    private readonly IResearchUsageCallback? _usageCallback;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IronHiveTextGenerationAdapter(
        IMessageGenerator generator,
        string modelId = "gpt-4o-mini",
        IResearchUsageCallback? usageCallback = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _modelId = modelId;
        _usageCallback = usageCallback;
    }

    /// <inheritdoc />
    public async Task<TextGenerationResult> GenerateAsync(
        string prompt,
        TextGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(prompt, options);
        var response = await _generator.GenerateMessageAsync(request, cancellationToken);

        // 토큰 사용량 콜백
        if (response.TokenUsage != null)
        {
            _usageCallback?.OnTokensUsed(
                response.TokenUsage.InputTokens,
                response.TokenUsage.OutputTokens);
        }

        var text = ExtractTextFromResponse(response);

        return new TextGenerationResult
        {
            Text = text,
            TokenUsage = response.TokenUsage != null
                ? new TokenUsageInfo
                {
                    PromptTokens = response.TokenUsage.InputTokens,
                    CompletionTokens = response.TokenUsage.OutputTokens
                }
                : null,
            FinishReason = response.DoneReason?.ToString()
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
            var json = ExtractJsonFromText(result.Text);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private MessageGenerationRequest CreateRequest(string prompt, TextGenerationOptions? options)
    {
        var messages = new List<Message>
        {
            new UserMessage
            {
                Content = [new TextMessageContent { Value = prompt }]
            }
        };

        return new MessageGenerationRequest
        {
            Model = _modelId,
            System = options?.SystemPrompt,
            Messages = messages,
            Temperature = (float?)(options?.Temperature) ?? 0.7f,
            MaxTokens = options?.MaxTokens ?? 2048
        };
    }

    private static string ExtractTextFromResponse(MessageResponse response)
    {
        var textContents = response.Message.Content?
            .OfType<TextMessageContent>()
            .Select(c => c.Value);

        return textContents != null ? string.Join("", textContents) : string.Empty;
    }

    internal static string ExtractJsonFromText(string text)
    {
        var jsonMatch = JsonCodeBlockRegex().Match(text);
        if (jsonMatch.Success)
        {
            return jsonMatch.Groups[1].Value.Trim();
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        var objectMatch = JsonObjectRegex().Match(text);
        if (objectMatch.Success)
        {
            return objectMatch.Value;
        }

        var arrayMatch = JsonArrayRegex().Match(text);
        if (arrayMatch.Success)
        {
            return arrayMatch.Value;
        }

        return text;
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```")]
    private static partial Regex JsonCodeBlockRegex();

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectRegex();

    [GeneratedRegex(@"\[[\s\S]*\]")]
    private static partial Regex JsonArrayRegex();
}
