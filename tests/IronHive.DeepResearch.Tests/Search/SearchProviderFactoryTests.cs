using FluentAssertions;
using IronHive.DeepResearch.Abstractions;
using IronHive.DeepResearch.Models.Search;
using IronHive.DeepResearch.Options;
using IronHive.DeepResearch.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IronHive.Flux.Tests.DeepResearch.Search;

public class SearchProviderFactoryTests
{
    private readonly Mock<ISearchProvider> _mockTavilyProvider;
    private readonly Mock<ISearchProvider> _mockAcademicProvider;
    private readonly DeepResearchOptions _options;

    public SearchProviderFactoryTests()
    {
        _mockTavilyProvider = new Mock<ISearchProvider>();
        _mockTavilyProvider.Setup(p => p.ProviderId).Returns("tavily");
        _mockTavilyProvider.Setup(p => p.Capabilities)
            .Returns(SearchCapabilities.WebSearch | SearchCapabilities.NewsSearch | SearchCapabilities.ContentExtraction);

        _mockAcademicProvider = new Mock<ISearchProvider>();
        _mockAcademicProvider.Setup(p => p.ProviderId).Returns("semantic-scholar");
        _mockAcademicProvider.Setup(p => p.Capabilities)
            .Returns(SearchCapabilities.AcademicSearch);

        _options = new DeepResearchOptions
        {
            DefaultSearchProvider = "tavily"
        };
    }

    [Fact]
    public void Constructor_InitializesWithProviders()
    {
        // Arrange & Act
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Assert
        factory.AvailableProviders.Should().Contain("tavily");
        factory.AvailableProviders.Should().HaveCount(1);
    }

    [Fact]
    public void GetDefaultProvider_ReturnsConfiguredDefault()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var provider = factory.GetDefaultProvider();

        // Assert
        provider.ProviderId.Should().Be("tavily");
    }

    [Fact]
    public void GetProvider_ExistingProvider_ReturnsProvider()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var provider = factory.GetProvider("semantic-scholar");

        // Assert
        provider.ProviderId.Should().Be("semantic-scholar");
    }

    [Fact]
    public void GetProvider_NonExistentProvider_ThrowsException()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act
        var act = () => factory.GetProvider("non-existent");

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-existent*not found*");
    }

    [Fact]
    public void GetProvider_CaseInsensitive()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act
        var provider = factory.GetProvider("TAVILY");

        // Assert
        provider.Should().NotBeNull();
        provider.ProviderId.Should().Be("tavily");
    }

    [Fact]
    public void HasProvider_ExistingProvider_ReturnsTrue()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act & Assert
        factory.HasProvider("tavily").Should().BeTrue();
        factory.HasProvider("TAVILY").Should().BeTrue();
    }

    [Fact]
    public void HasProvider_NonExistentProvider_ReturnsFalse()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act & Assert
        factory.HasProvider("non-existent").Should().BeFalse();
    }

    [Fact]
    public void GetProvidersWithCapability_WebSearch_ReturnsTavily()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var providers = factory.GetProvidersWithCapability(SearchCapabilities.WebSearch);

        // Assert
        providers.Should().HaveCount(1);
        providers[0].ProviderId.Should().Be("tavily");
    }

    [Fact]
    public void GetProvidersWithCapability_AcademicSearch_ReturnsSemanticScholar()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var providers = factory.GetProvidersWithCapability(SearchCapabilities.AcademicSearch);

        // Assert
        providers.Should().HaveCount(1);
        providers[0].ProviderId.Should().Be("semantic-scholar");
    }

    [Fact]
    public void GetProvidersWithCapability_NoMatch_ReturnsEmpty()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act
        var providers = factory.GetProvidersWithCapability(SearchCapabilities.ImageSearch);

        // Assert
        providers.Should().BeEmpty();
    }

    [Fact]
    public void SelectProviderForSearchType_Web_ReturnsTavily()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var provider = factory.SelectProviderForSearchType(SearchType.Web);

        // Assert
        provider.ProviderId.Should().Be("tavily");
    }

    [Fact]
    public void SelectProviderForSearchType_Academic_ReturnsSemanticScholar()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object, _mockAcademicProvider.Object]);

        // Act
        var provider = factory.SelectProviderForSearchType(SearchType.Academic);

        // Assert
        provider.ProviderId.Should().Be("semantic-scholar");
    }

    [Fact]
    public void SelectProviderForSearchType_UnsupportedType_ReturnsDefault()
    {
        // Arrange
        var factory = CreateFactory([_mockTavilyProvider.Object]);

        // Act
        var provider = factory.SelectProviderForSearchType(SearchType.Academic);

        // Assert
        provider.ProviderId.Should().Be("tavily");
    }

    private SearchProviderFactory CreateFactory(IEnumerable<ISearchProvider> providers)
    {
        return new SearchProviderFactory(
            providers,
            _options,
            NullLogger<SearchProviderFactory>.Instance);
    }
}
