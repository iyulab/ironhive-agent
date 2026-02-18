using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IronHive.Agent.Tests.Providers;

public class ChatClientFactoryTests
{
    private readonly IChatClientProvider _defaultProvider;
    private readonly IChatClientProvider _secondProvider;
    private readonly IChatClient _mockClient;
    private readonly Dictionary<string, IChatClientProvider> _providers;

    public ChatClientFactoryTests()
    {
        _defaultProvider = Substitute.For<IChatClientProvider>();
        _defaultProvider.ProviderName.Returns("default");
        _defaultProvider.IsAvailable.Returns(true);

        _mockClient = Substitute.For<IChatClient>();
        _defaultProvider.GetChatClientAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_mockClient);

        _secondProvider = Substitute.For<IChatClientProvider>();
        _secondProvider.ProviderName.Returns("second");
        _secondProvider.IsAvailable.Returns(true);
        _secondProvider.GetChatClientAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_mockClient);

        _providers = new Dictionary<string, IChatClientProvider>
        {
            ["default"] = _defaultProvider,
            ["second"] = _secondProvider
        };
    }

    #region Constructor

    [Fact]
    public void Constructor_NullProviders_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ChatClientFactory(null!, _defaultProvider));
    }

    [Fact]
    public void Constructor_NullDefaultProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ChatClientFactory(_providers, null!));
    }

    #endregion

    #region Properties

    [Fact]
    public void AvailableProviders_ReturnsOnlyAvailableProviders()
    {
        _secondProvider.IsAvailable.Returns(false);
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        var available = factory.AvailableProviders;

        Assert.Single(available);
        Assert.Contains("default", available);
    }

    [Fact]
    public void AvailableProviders_AllAvailable_ReturnsAll()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        var available = factory.AvailableProviders;

        Assert.Equal(2, available.Count);
    }

    [Fact]
    public void DefaultProviderName_ReturnsProviderName()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        Assert.Equal("default", factory.DefaultProviderName);
    }

    #endregion

    #region CreateAsync(modelOverride)

    [Fact]
    public async Task CreateAsync_NullModel_UsesDefaultProvider()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        var client = await factory.CreateAsync(null);

        Assert.Same(_mockClient, client);
        await _defaultProvider.Received(1).GetChatClientAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_SimpleModel_UsesDefaultProvider()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("gpt-4o");

        await _defaultProvider.Received(1).GetChatClientAsync("gpt-4o", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ProviderSlashModel_RoutesToProvider()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("second/my-model");

        await _secondProvider.Received(1).GetChatClientAsync("my-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ProviderSlashModel_CaseInsensitive()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("SECOND/my-model");

        await _secondProvider.Received(1).GetChatClientAsync("my-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnknownProviderSlashModel_FallsBackToDefault()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("unknown/model-x");

        // Falls through to default provider with full string
        await _defaultProvider.Received(1).GetChatClientAsync("unknown/model-x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_SlashOnly_UsesDefaultProvider()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        // "/" → parts[0]="" or parts[1]="" → empty check fails → default
        await factory.CreateAsync("/");

        await _defaultProvider.Received(1).GetChatClientAsync("/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithDecorator_AppliesDecorator()
    {
        var decoratedClient = Substitute.For<IChatClient>();
        var factory = new ChatClientFactory(
            _providers, _defaultProvider,
            client => decoratedClient);

        var result = await factory.CreateAsync("test");

        Assert.Same(decoratedClient, result);
    }

    [Fact]
    public async Task CreateAsync_NoDecorator_ReturnsOriginalClient()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider, null);

        var result = await factory.CreateAsync("test");

        Assert.Same(_mockClient, result);
    }

    #endregion

    #region CreateAsync(providerName, modelOverride)

    [Fact]
    public async Task CreateAsync_ExplicitProvider_UsesSpecifiedProvider()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("second", "my-model");

        await _secondProvider.Received(1).GetChatClientAsync("my-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ExplicitProvider_CaseInsensitive()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await factory.CreateAsync("SECOND", "model");

        await _secondProvider.Received(1).GetChatClientAsync("model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnknownProvider_ThrowsArgumentException()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => factory.CreateAsync("nonexistent", "model"));

        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("Available", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_UnavailableProvider_ThrowsInvalidOperation()
    {
        _secondProvider.IsAvailable.Returns(false);
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync("second", "model"));
    }

    [Fact]
    public async Task CreateAsync_ExplicitProvider_WithDecorator_AppliesDecorator()
    {
        var decoratedClient = Substitute.For<IChatClient>();
        var factory = new ChatClientFactory(
            _providers, _defaultProvider,
            client => decoratedClient);

        var result = await factory.CreateAsync("second", "model");

        Assert.Same(decoratedClient, result);
    }

    #endregion

    #region GetAllAvailableModelsAsync

    [Fact]
    public async Task GetAllAvailableModelsAsync_AggregatesFromAllProviders()
    {
        var models1 = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-1", Provider = "default" }
        };
        var models2 = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-2", Provider = "second" }
        };
        _defaultProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models1);
        _secondProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models2);

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAllAvailableModelsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAllAvailableModelsAsync_SkipsUnavailableProviders()
    {
        _secondProvider.IsAvailable.Returns(false);
        var models = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-1", Provider = "default" }
        };
        _defaultProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models);

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAllAvailableModelsAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetAllAvailableModelsAsync_ProviderThrows_ReturnsOtherResults()
    {
        _defaultProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("provider error"));
        var models = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-2", Provider = "second" }
        };
        _secondProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models);

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAllAvailableModelsAsync();

        Assert.Single(result);
    }

    #endregion

    #region GetAvailableModelsAsync(providerName)

    [Fact]
    public async Task GetAvailableModelsAsync_ValidProvider_ReturnsModels()
    {
        var models = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-1", Provider = "default" }
        };
        _defaultProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models);

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAvailableModelsAsync("default");

        Assert.Single(result);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_UnknownProvider_ReturnsEmpty()
    {
        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAvailableModelsAsync("nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_UnavailableProvider_ReturnsEmpty()
    {
        _secondProvider.IsAvailable.Returns(false);
        var factory = new ChatClientFactory(_providers, _defaultProvider);

        var result = await factory.GetAvailableModelsAsync("second");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ProviderThrows_ReturnsEmpty()
    {
        _defaultProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("error"));

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAvailableModelsAsync("default");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CaseInsensitive()
    {
        var models = new List<AvailableModelInfo>
        {
            new() { ModelId = "model-1", Provider = "second" }
        };
        _secondProvider.GetAvailableModelsAsync(Arg.Any<CancellationToken>())
            .Returns(models);

        var factory = new ChatClientFactory(_providers, _defaultProvider);
        var result = await factory.GetAvailableModelsAsync("SECOND");

        Assert.Single(result);
    }

    #endregion
}
