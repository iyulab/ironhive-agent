using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Context;

/// <summary>
/// Token counter for chat messages using character-based estimation and TokenMeter model catalog.
/// </summary>
public class ContextTokenCounter : IContextTokenCounter
{
    private readonly int _maxContextTokens;

    private const int MessageOverhead = 4;

    public ContextTokenCounter(string modelName = "gpt-4o", int? maxContextTokens = null)
    {
        ModelName = modelName;
        _maxContextTokens = maxContextTokens
            ?? ModelCatalog.FindModel(modelName)?.ContextWindow
            ?? 8192;
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public int MaxContextTokens => _maxContextTokens;

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return EstimateTokens(text);
    }

    /// <inheritdoc />
    public int CountTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var tokens = MessageOverhead;

        if (!string.IsNullOrEmpty(message.Text))
        {
            tokens += EstimateTokens(message.Text);
        }

        foreach (var content in message.Contents)
        {
            tokens += CountContentTokens(content);
        }

        return tokens;
    }

    /// <inheritdoc />
    public int CountTokens(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        foreach (var message in messages)
        {
            total += CountTokens(message);
        }

        total += 3; // conversation priming overhead
        return total;
    }

    private static int CountContentTokens(AIContent content)
    {
        return content switch
        {
            TextContent text => EstimateTokens(text.Text ?? string.Empty),
            FunctionCallContent func => CountFunctionCallTokens(func),
            FunctionResultContent result => EstimateTokens(result.Result?.ToString() ?? string.Empty),
            _ when content.GetType().Name.Contains("Image", StringComparison.OrdinalIgnoreCase) => 85,
            _ => 0
        };
    }

    private static int CountFunctionCallTokens(FunctionCallContent func)
    {
        var tokens = EstimateTokens(func.Name);
        if (func.Arguments is not null)
        {
            tokens += EstimateTokens(System.Text.Json.JsonSerializer.Serialize(func.Arguments));
        }
        return tokens + 10;
    }

    private static int EstimateTokens(string text) => (text.Length + 3) / 4;

    public static ContextTokenCounter ForGpt4o() => new("gpt-4o", 128000);
    public static ContextTokenCounter ForClaude35Sonnet() => new("claude-3.5-sonnet", 200000);
    public static ContextTokenCounter Default() => new("gpt-4", 8192);
}
