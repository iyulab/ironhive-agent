using FluentAssertions;
using IronHive.Agent.Context;

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
}
