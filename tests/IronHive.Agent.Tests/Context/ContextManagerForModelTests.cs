using FluentAssertions;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class ContextManagerForModelTests
{
    [Fact]
    public void ForModel_WithMaxContextTokens_OverridesDefaultLookup()
    {
        // "unknown-model" would normally fall back to 8192
        var config = new CompactionConfig { MaxContextTokens = 2048 };
        var manager = ContextManager.ForModel("unknown-model", config);

        manager.MaxContextTokens.Should().Be(2048);
    }

    [Fact]
    public void ForModel_WithoutMaxContextTokens_UsesDefaultLookup()
    {
        var config = new CompactionConfig(); // MaxContextTokens = null
        var manager = ContextManager.ForModel("gpt-4o", config);

        manager.MaxContextTokens.Should().Be(128000);
    }

    [Fact]
    public void ForModel_SimpleOverload_UsesDefaultLookup()
    {
        var manager = ContextManager.ForModel("gpt-4o");
        manager.MaxContextTokens.Should().Be(128000);
    }

    [Fact]
    public void ForModel_WithMaxContextTokens_OverridesKnownModel()
    {
        // Even for known models, explicit override takes precedence
        var config = new CompactionConfig { MaxContextTokens = 50000 };
        var manager = ContextManager.ForModel("gpt-4o", config);

        manager.MaxContextTokens.Should().Be(50000);
    }

    [Fact]
    public void ValidateContextFit_WhenExceeds80Percent_ReturnsWarning()
    {
        // Use a very small context so system prompt easily exceeds 80%
        var config = new CompactionConfig { MaxContextTokens = 100 };
        var manager = ContextManager.ForModel("gpt-4o", config);

        // Create a system prompt that exceeds 80% of 100 tokens
        var longSystemPrompt = string.Join(' ', Enumerable.Repeat("word", 200));
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, longSystemPrompt)
        };

        var warning = manager.ValidateContextFit(history);
        warning.Should().NotBeNull();
        warning!.SystemPromptTokens.Should().BeGreaterThan(0);
        warning.MaxContextTokens.Should().Be(100);
        warning.IsOverBudget.Should().BeTrue();
    }

    [Fact]
    public void ValidateContextFit_WhenWithinBudget_ReturnsNull()
    {
        var config = new CompactionConfig { MaxContextTokens = 128000 };
        var manager = ContextManager.ForModel("gpt-4o", config);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };

        var warning = manager.ValidateContextFit(history);
        warning.Should().BeNull();
    }
}
