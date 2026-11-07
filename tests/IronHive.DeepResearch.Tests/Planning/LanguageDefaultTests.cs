using AwesomeAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Analysis;
using IronHive.DeepResearch.Models.Planning;
using IronHive.DeepResearch.Models.Report;
using IronHive.DeepResearch.Models.Research;
using IronHive.DeepResearch.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IronHive.DeepResearch.Tests.Planning;

/// <summary>
/// A research request that names no language is not a Korean one: the models' <c>Language</c> is empty, which means
/// "answer in the language of the question", and the prompt says so instead of naming a language that is not there.
/// </summary>
public class LanguageDefaultTests
{
    [Fact]
    public void TheModels_DoNotStampKorean_WhenNobodySaidALanguage()
    {
        new ResearchRequest { Query = "q" }.Language.Should().BeEmpty();
        new QueryExpansionOptions().Language.Should().BeEmpty();
        new ReportGenerationOptions().Language.Should().BeEmpty();
        new AnalysisOptions().Language.Should().BeEmpty();
    }

    [Fact]
    public async Task TheDecompositionPrompt_WithoutALanguage_AsksForTheLanguageOfTheQuestion()
    {
        var service = new CapturingTextService();
        var expander = new LLMQueryExpander(service, NullLogger<LLMQueryExpander>.Instance);

        await expander.DecomposeQueryAsync("What changed in EU AI rules?", cancellationToken: TestContext.Current.CancellationToken);

        service.Prompts.Should().ContainSingle().Which.Should()
            .Contain("language of the user's question")
            .And.NotContain("Write all output in .");
    }

    [Fact]
    public async Task TheDecompositionPrompt_WithALanguage_StillNamesIt()
    {
        var service = new CapturingTextService();
        var expander = new LLMQueryExpander(service, NullLogger<LLMQueryExpander>.Instance);

        await expander.DecomposeQueryAsync("q", new QueryExpansionOptions { Language = "ja" }, TestContext.Current.CancellationToken);
        await expander.DecomposeQueryAsync("q", new QueryExpansionOptions { Language = "ko" }, TestContext.Current.CancellationToken);

        service.Prompts[0].Should().Contain("Write all output in ja.");
        service.Prompts[1].Should().Contain("모든 출력은 한국어로 작성하세요.");
    }

    private sealed class CapturingTextService : ITextGenerationService
    {
        public List<string> Prompts { get; } = [];

        public Task<TextGenerationResult> GenerateAsync(string prompt, TextGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<T?> GenerateStructuredAsync<T>(string prompt, TextGenerationOptions? options = null, CancellationToken cancellationToken = default)
            where T : class
        {
            Prompts.Add(prompt);
            return Task.FromResult<T?>(null);
        }
    }
}
