using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Providers;

public class FallbackChatClientProviderTests : IDisposable
{
    private readonly IChatClientProvider _provider1;
    private readonly IChatClientProvider _provider2;
    private readonly IChatClient _mockClient1;
    private readonly IChatClient _mockClient2;

    public FallbackChatClientProviderTests()
    {
        _provider1 = Substitute.For<IChatClientProvider>();
        _provider1.ProviderName.Returns("provider1");
        _provider1.IsAvailable.Returns(true);
        _provider1.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(true);

        _mockClient1 = Substitute.For<IChatClient>();
        _provider1.GetChatClientAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_mockClient1);

        _provider2 = Substitute.For<IChatClientProvider>();
        _provider2.ProviderName.Returns("provider2");
        _provider2.IsAvailable.Returns(true);
        _provider2.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(true);

        _mockClient2 = Substitute.For<IChatClient>();
        _provider2.GetChatClientAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_mockClient2);
    }

    private FallbackChatClientProvider? _sut;

    public void Dispose()
    {
        _sut?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Constructor

    [Fact]
    public void Constructor_NullProviders_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FallbackChatClientProvider(null!));
    }

    [Fact]
    public void Constructor_EmptyProviders_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FallbackChatClientProvider());
    }

    [Fact]
    public void Constructor_SingleProvider_DoesNotThrow()
    {
        _sut = new FallbackChatClientProvider(_provider1);
        Assert.NotNull(_sut);
    }

    #endregion

    #region Properties

    [Fact]
    public void ProviderName_NoActiveProvider_ReturnsFallback()
    {
        _sut = new FallbackChatClientProvider(_provider1);

        Assert.Equal("fallback", _sut.ProviderName);
    }

    [Fact]
    public async Task ProviderName_AfterInit_ReturnsActiveProviderName()
    {
        _sut = new FallbackChatClientProvider(_provider1);
        await _sut.GetChatClientAsync();

        Assert.Equal("provider1", _sut.ProviderName);
    }

    [Fact]
    public void IsAvailable_AnyProviderAvailable_ReturnsTrue()
    {
        _provider1.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        Assert.True(_sut.IsAvailable);
    }

    [Fact]
    public void IsAvailable_NoneAvailable_ReturnsFalse()
    {
        _provider1.IsAvailable.Returns(false);
        _provider2.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        Assert.False(_sut.IsAvailable);
    }

    [Fact]
    public void ActiveProvider_Initially_IsNull()
    {
        _sut = new FallbackChatClientProvider(_provider1);

        Assert.Null(_sut.ActiveProvider);
    }

    #endregion

    #region GetChatClientAsync

    [Fact]
    public async Task GetChatClientAsync_FirstAvailable_ReturnsClient()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var client = await _sut.GetChatClientAsync();

        Assert.Same(_mockClient1, client);
        Assert.Same(_provider1, _sut.ActiveProvider);
    }

    [Fact]
    public async Task GetChatClientAsync_FirstUnavailable_FallsToSecond()
    {
        _provider1.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var client = await _sut.GetChatClientAsync();

        Assert.Same(_mockClient2, client);
        Assert.Same(_provider2, _sut.ActiveProvider);
    }

    [Fact]
    public async Task GetChatClientAsync_FirstUnhealthy_FallsToSecond()
    {
        _provider1.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var client = await _sut.GetChatClientAsync();

        Assert.Same(_mockClient2, client);
    }

    [Fact]
    public async Task GetChatClientAsync_NoneAvailable_ThrowsInvalidOperation()
    {
        _provider1.IsAvailable.Returns(false);
        _provider2.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_AlreadyActive_ReusesProvider()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        await _sut.GetChatClientAsync();
        await _sut.GetChatClientAsync("model-2");

        // First call initializes, second call reuses
        await _provider1.Received(2).GetChatClientAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _provider1.Received(1).CheckHealthAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetChatClientAsync_PassesModelOverride()
    {
        _sut = new FallbackChatClientProvider(_provider1);

        await _sut.GetChatClientAsync("custom-model");

        await _provider1.Received(1).GetChatClientAsync("custom-model", Arg.Any<CancellationToken>());
    }

    #endregion

    #region CheckHealthAsync

    [Fact]
    public async Task CheckHealthAsync_FirstHealthy_ReturnsTrue()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var result = await _sut.CheckHealthAsync();

        Assert.True(result);
        Assert.Same(_provider1, _sut.ActiveProvider);
    }

    [Fact]
    public async Task CheckHealthAsync_FirstUnhealthy_TriesSecond()
    {
        _provider1.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var result = await _sut.CheckHealthAsync();

        Assert.True(result);
        Assert.Same(_provider2, _sut.ActiveProvider);
    }

    [Fact]
    public async Task CheckHealthAsync_NoneHealthy_ReturnsFalse()
    {
        _provider1.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(false);
        _provider2.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var result = await _sut.CheckHealthAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task CheckHealthAsync_SkipsUnavailable()
    {
        _provider1.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        await _sut.CheckHealthAsync();

        await _provider1.DidNotReceive().CheckHealthAsync(Arg.Any<CancellationToken>());
        await _provider2.Received(1).CheckHealthAsync(Arg.Any<CancellationToken>());
    }

    #endregion

    #region SwitchToNextProviderAsync

    [Fact]
    public async Task SwitchToNextProviderAsync_NoActive_StartsFromFirst()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        var result = await _sut.SwitchToNextProviderAsync();

        Assert.True(result);
        Assert.Same(_provider1, _sut.ActiveProvider);
    }

    [Fact]
    public async Task SwitchToNextProviderAsync_ActiveIsFirst_SwitchesToSecond()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);
        await _sut.GetChatClientAsync(); // activates provider1

        var result = await _sut.SwitchToNextProviderAsync();

        Assert.True(result);
        Assert.Same(_provider2, _sut.ActiveProvider);
    }

    [Fact]
    public async Task SwitchToNextProviderAsync_ActiveIsLast_ReturnsFalse()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);
        await _sut.GetChatClientAsync(); // activates provider1
        await _sut.SwitchToNextProviderAsync(); // activates provider2

        var result = await _sut.SwitchToNextProviderAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task SwitchToNextProviderAsync_SkipsUnavailable()
    {
        _provider2.IsAvailable.Returns(false);
        _sut = new FallbackChatClientProvider(_provider1, _provider2);
        await _sut.GetChatClientAsync(); // activates provider1

        var result = await _sut.SwitchToNextProviderAsync();

        Assert.False(result);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_DisposesAllProviders()
    {
        var disposableProvider = Substitute.For<IChatClientProvider, IDisposable>();
        _sut = new FallbackChatClientProvider(disposableProvider);

        _sut.Dispose();
        _sut = null; // prevent double dispose in test cleanup

        ((IDisposable)disposableProvider).Received(1).Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllProviders()
    {
        _sut = new FallbackChatClientProvider(_provider1, _provider2);

        await _sut.DisposeAsync();
        _sut = null; // prevent double dispose in test cleanup

        await _provider1.Received(1).DisposeAsync();
        await _provider2.Received(1).DisposeAsync();
    }

    #endregion
}
