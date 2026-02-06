using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class PromptCachingTests
{
    private readonly ContextTokenCounter _tokenCounter = new("gpt-4o");

    [Fact]
    public void CacheControl_WithCacheControl_AddsCacheHint()
    {
        var message = new ChatMessage(ChatRole.System, "You are helpful.");
        message.WithCacheControl();

        Assert.True(message.HasCacheControl());
        Assert.NotNull(message.GetCacheControl());
    }

    [Fact]
    public void CacheControl_WithoutCacheControl_ReturnsFalse()
    {
        var message = new ChatMessage(ChatRole.System, "You are helpful.");

        Assert.False(message.HasCacheControl());
        Assert.Null(message.GetCacheControl());
    }

    [Fact]
    public void ApplyCacheHints_DisabledOption_ReturnsOriginal()
    {
        var options = new PromptCachingOptions { Enabled = false };
        var manager = new PromptCacheManager(_tokenCounter, options);
        var history = CreateHistory();

        var result = manager.ApplyCacheHints(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void ApplyCacheHints_EmptyHistory_ReturnsOriginal()
    {
        var manager = new PromptCacheManager(_tokenCounter);
        var history = new List<ChatMessage>();

        var result = manager.ApplyCacheHints(history);

        Assert.Same(history, result);
    }

    [Fact]
    public void ApplyCacheHints_LargeSystemPrompt_AddsCacheHint()
    {
        var options = new PromptCachingOptions { MinSystemPromptTokens = 10 };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var largeSystemPrompt = new string('x', 500); // Definitely > 10 tokens
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, largeSystemPrompt),
            new(ChatRole.User, "Hello")
        };

        var result = manager.ApplyCacheHints(history);

        Assert.True(result[0].HasCacheControl());
        Assert.False(result[1].HasCacheControl());
    }

    [Fact]
    public void ApplyCacheHints_SmallSystemPrompt_NoCacheHint()
    {
        var options = new PromptCachingOptions { MinSystemPromptTokens = 1000 };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "Be helpful."), // Very short
            new(ChatRole.User, "Hello")
        };

        var result = manager.ApplyCacheHints(history);

        Assert.False(result[0].HasCacheControl());
    }

    [Fact]
    public void ApplyCacheHints_ConfiguredBreakpoints_AddsCacheHints()
    {
        var options = new PromptCachingOptions
        {
            MinSystemPromptTokens = 10000, // Disable auto-caching
            CacheBreakpoints = [0, 2]
        };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System"),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi")
        };

        var result = manager.ApplyCacheHints(history);

        Assert.True(result[0].HasCacheControl()); // Index 0
        Assert.False(result[1].HasCacheControl());
        Assert.True(result[2].HasCacheControl()); // Index 2
    }

    [Fact]
    public void CalculateOptimalBreakpoints_ReturnsSystemMessageIndex()
    {
        var options = new PromptCachingOptions { MinSystemPromptTokens = 10 };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var largeSystemPrompt = new string('x', 500);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, largeSystemPrompt),
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi")
        };

        var breakpoints = manager.CalculateOptimalBreakpoints(history);

        Assert.Contains(0, breakpoints); // System message
    }

    [Fact]
    public void EstimateSavings_CalculatesCorrectly()
    {
        var options = new PromptCachingOptions { MinSystemPromptTokens = 10 };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var largeSystemPrompt = new string('x', 500);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, largeSystemPrompt),
            new(ChatRole.User, "Hello")
        };

        var estimate = manager.EstimateSavings(history);

        Assert.True(estimate.TotalTokens > 0);
        Assert.True(estimate.CacheableTokens > 0);
        Assert.True(estimate.PotentialSavingsPercent > 0);
        Assert.True(estimate.PotentialSavingsPercent <= 1.0f);
    }

    [Fact]
    public void EstimateSavings_NoCacheableContent_ReturnsZeroSavings()
    {
        var options = new PromptCachingOptions { MinSystemPromptTokens = 10000 };
        var manager = new PromptCacheManager(_tokenCounter, options);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "Short."),
            new(ChatRole.User, "Hello")
        };

        var estimate = manager.EstimateSavings(history);

        Assert.Equal(0, estimate.CacheableTokens);
        Assert.Equal(0, estimate.PotentialSavingsPercent);
    }

    private static List<ChatMessage> CreateHistory()
    {
        return
        [
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hi there!")
        ];
    }
}
