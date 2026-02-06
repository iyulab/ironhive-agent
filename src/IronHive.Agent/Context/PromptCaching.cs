using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Options for prompt caching behavior.
/// </summary>
public class PromptCachingOptions
{
    /// <summary>
    /// Whether prompt caching is enabled.
    /// Default is true.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum number of tokens in system prompt to enable caching.
    /// Caching has overhead, so small prompts don't benefit.
    /// Default is 1024 (Anthropic's recommendation).
    /// </summary>
    public int MinSystemPromptTokens { get; init; } = 1024;

    /// <summary>
    /// Cache breakpoints as indices in the message history.
    /// These mark points where the cache can be reused.
    /// </summary>
    public IList<int> CacheBreakpoints { get; init; } = [];
}

/// <summary>
/// Cache control hint for messages.
/// Used by providers that support prompt caching (e.g., Anthropic, OpenAI).
/// </summary>
public static class CacheControl
{
    /// <summary>
    /// Key for cache control in message additional properties.
    /// </summary>
    public const string CacheControlKey = "cache_control";

    /// <summary>
    /// Ephemeral cache type (Anthropic).
    /// </summary>
    public const string Ephemeral = "ephemeral";

    /// <summary>
    /// Marks a message for caching.
    /// </summary>
    public static ChatMessage WithCacheControl(this ChatMessage message, string cacheType = Ephemeral)
    {
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[CacheControlKey] = new { type = cacheType };
        return message;
    }

    /// <summary>
    /// Checks if a message has cache control hints.
    /// </summary>
    public static bool HasCacheControl(this ChatMessage message)
    {
        return message.AdditionalProperties?.ContainsKey(CacheControlKey) == true;
    }

    /// <summary>
    /// Gets the cache control value from a message.
    /// </summary>
    public static object? GetCacheControl(this ChatMessage message)
    {
        return message.AdditionalProperties?.GetValueOrDefault(CacheControlKey);
    }
}

/// <summary>
/// Manages prompt caching for efficient API usage.
/// Supports Anthropic's prompt caching and similar mechanisms.
/// </summary>
public class PromptCacheManager
{
    private readonly IContextTokenCounter _tokenCounter;
    private readonly PromptCachingOptions _options;

    public PromptCacheManager(IContextTokenCounter tokenCounter, PromptCachingOptions? options = null)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _options = options ?? new PromptCachingOptions();
    }

    /// <summary>
    /// Applies cache control hints to messages based on configuration.
    /// </summary>
    public IReadOnlyList<ChatMessage> ApplyCacheHints(IReadOnlyList<ChatMessage> history)
    {
        if (!_options.Enabled || history.Count == 0)
        {
            return history;
        }

        var result = new List<ChatMessage>(history.Count);

        for (var i = 0; i < history.Count; i++)
        {
            var message = CloneMessage(history[i]);

            // Apply cache hint to system messages that meet the threshold
            if (message.Role == ChatRole.System)
            {
                var tokens = _tokenCounter.CountTokens(message);
                if (tokens >= _options.MinSystemPromptTokens)
                {
                    message.WithCacheControl();
                }
            }

            // Apply cache hints at configured breakpoints
            if (_options.CacheBreakpoints.Contains(i))
            {
                message.WithCacheControl();
            }

            result.Add(message);
        }

        return result;
    }

    /// <summary>
    /// Calculates optimal cache breakpoints for a history.
    /// Places breakpoints at natural boundaries (after tool definitions, after long context).
    /// </summary>
    public IReadOnlyList<int> CalculateOptimalBreakpoints(IReadOnlyList<ChatMessage> history)
    {
        var breakpoints = new List<int>();

        // Always cache after system messages
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.System)
            {
                var tokens = _tokenCounter.CountTokens(history[i]);
                if (tokens >= _options.MinSystemPromptTokens)
                {
                    breakpoints.Add(i);
                }
            }
        }

        // Add breakpoint at natural conversation boundaries
        // (e.g., after every 10 turns if conversation is long)
        var turnCount = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.User)
            {
                turnCount++;
                if (turnCount % 10 == 0 && i > 0)
                {
                    // Add breakpoint before this user message (after previous assistant response)
                    breakpoints.Add(i - 1);
                }
            }
        }

        return breakpoints.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Estimates cache savings for a given history.
    /// </summary>
    public CacheSavingsEstimate EstimateSavings(IReadOnlyList<ChatMessage> history)
    {
        var cacheableTokens = 0;
        var totalTokens = _tokenCounter.CountTokens(history);

        foreach (var message in history)
        {
            if (message.Role == ChatRole.System)
            {
                var tokens = _tokenCounter.CountTokens(message);
                if (tokens >= _options.MinSystemPromptTokens)
                {
                    cacheableTokens += tokens;
                }
            }
        }

        // Anthropic charges 25% for cache writes, 10% for cache reads
        // Net savings on subsequent requests: 90% of cached tokens
        var potentialSavingsPercent = totalTokens > 0
            ? (float)cacheableTokens / totalTokens * 0.90f
            : 0;

        return new CacheSavingsEstimate
        {
            TotalTokens = totalTokens,
            CacheableTokens = cacheableTokens,
            PotentialSavingsPercent = potentialSavingsPercent
        };
    }

    private static ChatMessage CloneMessage(ChatMessage message)
    {
        var clone = new ChatMessage(message.Role, message.Text);

        foreach (var content in message.Contents)
        {
            clone.Contents.Add(content);
        }

        if (message.AdditionalProperties is not null)
        {
            clone.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var kvp in message.AdditionalProperties)
            {
                clone.AdditionalProperties[kvp.Key] = kvp.Value;
            }
        }

        return clone;
    }
}

/// <summary>
/// Estimate of potential cache savings.
/// </summary>
public record CacheSavingsEstimate
{
    /// <summary>
    /// Total tokens in the history.
    /// </summary>
    public int TotalTokens { get; init; }

    /// <summary>
    /// Tokens that can be cached.
    /// </summary>
    public int CacheableTokens { get; init; }

    /// <summary>
    /// Potential savings as a percentage (0.0 to 1.0).
    /// </summary>
    public float PotentialSavingsPercent { get; init; }
}
