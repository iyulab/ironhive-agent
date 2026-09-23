using AwesomeAssertions;
using IronHive.Agent.Providers;
using NSubstitute;

namespace IronHive.Agent.Tests.Providers;

/// <summary>
/// The fallback chain lists the models of its available providers. Before, it fell through to the interface
/// default and reported no models although the providers it holds list theirs.
/// </summary>
public class FallbackChatClientProviderModelsTests
{
    private static IChatClientProvider Provider(string name, bool available, params string[] models)
    {
        var p = Substitute.For<IChatClientProvider>();
        p.ProviderName.Returns(name);
        p.IsAvailable.Returns(available);
        p.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models.Select(m => new AvailableModelInfo { ModelId = m, Provider = name }).ToList());
        return p;
    }

    [Fact]
    public async Task Lists_the_models_of_every_available_provider_in_chain_order()
    {
        using var chain = new FallbackChatClientProvider(
            Provider("cloud", available: true, "gpt-5", "gpt-5-mini"),
            Provider("offline", available: false, "never-listed"),
            Provider("local", available: true, "qwen3"));

        var models = await ((IChatClientProvider)chain).GetAvailableModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => (m.Provider, m.ModelId)).Should().Equal(
            ("cloud", "gpt-5"), ("cloud", "gpt-5-mini"), ("local", "qwen3"));
    }
}
