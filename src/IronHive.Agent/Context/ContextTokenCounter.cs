using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Context;

/// <summary>
/// Token counter for chat messages using TokenMeter.
/// </summary>
public class ContextTokenCounter : IContextTokenCounter
{
    private readonly ITokenCounter _tokenCounter;
    private readonly int _maxContextTokens;

    // Overhead tokens per message (role, formatting, etc.)
    private const int MessageOverhead = 4;

    /// <summary>
    /// Known model context window sizes.
    /// </summary>
    private static readonly Dictionary<string, int> ModelContextSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI
        ["gpt-4"] = 8192,
        ["gpt-4-turbo"] = 128000,
        ["gpt-4o"] = 128000,
        ["gpt-4o-mini"] = 128000,
        ["gpt-3.5-turbo"] = 16385,

        // Anthropic
        ["claude-3-opus"] = 200000,
        ["claude-3-sonnet"] = 200000,
        ["claude-3-haiku"] = 200000,
        ["claude-3.5-sonnet"] = 200000,
        ["claude-3.5-haiku"] = 200000,

        // Default for unknown models
        ["default"] = 8192
    };

    public ContextTokenCounter(string modelName = "gpt-4o", int? maxContextTokens = null)
    {
        ModelName = modelName;
        _tokenCounter = new TokenCounter(modelName);
        _maxContextTokens = maxContextTokens ?? GetDefaultContextSize(modelName);
    }

    public ContextTokenCounter(ITokenCounter tokenCounter, string modelName, int maxContextTokens)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        ModelName = modelName;
        _maxContextTokens = maxContextTokens;
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

        return _tokenCounter.CountTokens(text);
    }

    /// <inheritdoc />
    public int CountTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var tokens = MessageOverhead; // Base overhead for message structure

        // Count text content
        if (!string.IsNullOrEmpty(message.Text))
        {
            tokens += _tokenCounter.CountTokens(message.Text);
        }

        // Count additional content items (images, tool calls, etc.)
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

        // Add conversation overhead (priming tokens)
        total += 3;

        return total;
    }

    private int CountContentTokens(AIContent content)
    {
        return content switch
        {
            TextContent text => _tokenCounter.CountTokens(text.Text ?? string.Empty),
            FunctionCallContent func => CountFunctionCallTokens(func),
            FunctionResultContent result => _tokenCounter.CountTokens(result.Result?.ToString() ?? string.Empty),
            // Images and other binary content: approximate token estimate
            _ when content.GetType().Name.Contains("Image", StringComparison.OrdinalIgnoreCase) => 85,
            _ => 0
        };
    }

    private int CountFunctionCallTokens(FunctionCallContent func)
    {
        var tokens = _tokenCounter.CountTokens(func.Name);

        if (func.Arguments is not null)
        {
            // Estimate JSON argument tokens
            var argsJson = System.Text.Json.JsonSerializer.Serialize(func.Arguments);
            tokens += _tokenCounter.CountTokens(argsJson);
        }

        return tokens + 10; // Overhead for function call structure
    }

    private static int GetDefaultContextSize(string modelName)
    {
        // Try exact match first
        if (ModelContextSizes.TryGetValue(modelName, out var size))
        {
            return size;
        }

        // Try prefix matching for versioned models
        foreach (var (prefix, contextSize) in ModelContextSizes)
        {
            if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return contextSize;
            }
        }

        return ModelContextSizes["default"];
    }

    /// <summary>
    /// Creates a token counter for GPT-4o models.
    /// </summary>
    public static ContextTokenCounter ForGpt4o() => new("gpt-4o", 128000);

    /// <summary>
    /// Creates a token counter for Claude 3.5 Sonnet.
    /// </summary>
    public static ContextTokenCounter ForClaude35Sonnet() => new("claude-3.5-sonnet", 200000);

    /// <summary>
    /// Creates a default token counter with conservative settings.
    /// </summary>
    public static ContextTokenCounter Default() => new("gpt-4", 8192);
}
