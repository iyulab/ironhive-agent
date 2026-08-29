using AwesomeAssertions;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class ContextManagerForModelTests
{
    // ── Small-context compaction clamp tests ──────────────────────────────────

    [Fact]
    public void ForModel_SmallContextWithDefaultProtect_TriggersCompaction()
    {
        // Bug: default ProtectRecentTokens=40_000 > MaxContextTokens=500
        // → prunableTokens always negative → ShouldCompact always false.
        // Fix: clamp protect to maxCtx/2 so trigger fires normally.
        var config = new CompactionConfig { MaxContextTokens = 500 };
        var manager = ContextManager.ForModel("unknown-model", config);

        var history = CreateSmallContextHistory(20);

        var usage = manager.GetUsage(history);
        usage.NeedsCompaction.Should().BeTrue(
            "token-based compaction trigger must fire when history approaches 500-token limit");
    }

    [Fact]
    public async Task ForModel_SmallContextWithDefaultProtect_CompactionActuallyPrunes()
    {
        var config = new CompactionConfig { MaxContextTokens = 500 };
        var manager = ContextManager.ForModel("unknown-model", config);

        var history = CreateSmallContextHistory(20);

        var result = await manager.CompactIfNeededAsync(history, TestContext.Current.CancellationToken);
        result.MessagesCompacted.Should().BeGreaterThan(0,
            "compaction must remove at least one message");
        result.CompactedTokens.Should().BeLessThan(result.OriginalTokens,
            "token count must decrease after compaction");
    }

    [Fact]
    public void ForModel_ExplicitProtectBelowHalfOfMaxContext_IsNotOverridden()
    {
        // ProtectRecentTokens=100 < MaxContextTokens/2=250 → must be preserved, not clamped.
        var config = new CompactionConfig
        {
            MaxContextTokens = 500,
            ProtectRecentTokens = 100,
            MinimumPruneTokens = 50
        };
        var manager = ContextManager.ForModel("unknown-model", config);

        var history = CreateSmallContextHistory(20);

        var usage = manager.GetUsage(history);
        usage.NeedsCompaction.Should().BeTrue(
            "compaction must trigger with explicitly small ProtectRecentTokens=100");
    }

    private static List<ChatMessage> CreateSmallContextHistory(int turns)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant.")
        };
        for (var i = 0; i < turns; i++)
        {
            history.Add(new(ChatRole.User, $"Turn {i}: Please help me with task number {i}."));
            history.Add(new(ChatRole.Assistant, $"Sure, here is my response for task {i}."));
        }
        return history;
    }

    // ─────────────────────────────────────────────────────────────────────────

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
