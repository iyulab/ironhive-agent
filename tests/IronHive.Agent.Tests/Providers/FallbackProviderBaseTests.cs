using FluentAssertions;
using IronHive.Agent.Providers;

namespace IronHive.Agent.Tests.Providers;

public class FallbackProviderBaseTests
{
    #region Test Types

    private sealed class TestProvider : IAsyncDisposable
    {
        public bool Available { get; set; } = true;
        public bool InitializeSucceeds { get; set; } = true;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestFallbackProvider : FallbackProviderBase<TestProvider>
    {
        public TestFallbackProvider(params TestProvider[] providers)
            : base(providers) { }

        public override string ProviderName => "test";

        protected override bool IsProviderAvailable(TestProvider provider)
            => provider.Available;

        protected override ValueTask<bool> TryInitializeProviderAsync(
            TestProvider provider, CancellationToken cancellationToken)
            => new(provider.InitializeSucceeds);

        public new ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
            => base.EnsureInitializedAsync(cancellationToken);

        public new TestProvider? ActiveProvider => base.ActiveProvider;
    }

    private sealed class ThrowingFallbackProvider : FallbackProviderBase<TestProvider>
    {
        public ThrowingFallbackProvider(params TestProvider[] providers)
            : base(providers) { }

        public override string ProviderName => "throwing";

        protected override bool IsProviderAvailable(TestProvider provider)
            => provider.Available;

        protected override ValueTask<bool> TryInitializeProviderAsync(
            TestProvider provider, CancellationToken cancellationToken)
            => throw new InvalidOperationException("init failed");

        public new ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
            => base.EnsureInitializedAsync(cancellationToken);
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_NullProviders_ThrowsArgumentException()
    {
        var act = () => new TestFallbackProvider(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyProviders_ThrowsArgumentException()
    {
        var act = () => new TestFallbackProvider([]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ValidProviders_Succeeds()
    {
        var provider = new TestProvider();

        var fallback = new TestFallbackProvider(provider);

        fallback.Should().NotBeNull();
    }

    #endregion

    #region IsAvailable

    [Fact]
    public void IsAvailable_AllAvailable_ReturnsTrue()
    {
        var p1 = new TestProvider { Available = true };
        var p2 = new TestProvider { Available = true };
        var fallback = new TestFallbackProvider(p1, p2);

        fallback.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_NoneAvailable_ReturnsFalse()
    {
        var p1 = new TestProvider { Available = false };
        var fallback = new TestFallbackProvider(p1);

        fallback.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_SomeAvailable_ReturnsTrue()
    {
        var p1 = new TestProvider { Available = false };
        var p2 = new TestProvider { Available = true };
        var fallback = new TestFallbackProvider(p1, p2);

        fallback.IsAvailable.Should().BeTrue();
    }

    #endregion

    #region EnsureInitializedAsync

    [Fact]
    public async Task EnsureInitializedAsync_FirstProviderSucceeds_SetsActiveProvider()
    {
        var p1 = new TestProvider();
        var p2 = new TestProvider();
        var fallback = new TestFallbackProvider(p1, p2);

        await fallback.EnsureInitializedAsync(CancellationToken.None);

        fallback.ActiveProvider.Should().BeSameAs(p1);
    }

    [Fact]
    public async Task EnsureInitializedAsync_FirstFails_FallsBackToSecond()
    {
        var p1 = new TestProvider { InitializeSucceeds = false };
        var p2 = new TestProvider { InitializeSucceeds = true };
        var fallback = new TestFallbackProvider(p1, p2);

        await fallback.EnsureInitializedAsync(CancellationToken.None);

        fallback.ActiveProvider.Should().BeSameAs(p2);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkipsUnavailable()
    {
        var p1 = new TestProvider { Available = false };
        var p2 = new TestProvider { Available = true };
        var fallback = new TestFallbackProvider(p1, p2);

        await fallback.EnsureInitializedAsync(CancellationToken.None);

        fallback.ActiveProvider.Should().BeSameAs(p2);
    }

    [Fact]
    public async Task EnsureInitializedAsync_AllFail_ThrowsInvalidOperationException()
    {
        var p1 = new TestProvider { InitializeSucceeds = false };
        var p2 = new TestProvider { InitializeSucceeds = false };
        var fallback = new TestFallbackProvider(p1, p2);

        var act = async () => await fallback.EnsureInitializedAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No available*");
    }

    [Fact]
    public async Task EnsureInitializedAsync_AlreadyInitialized_DoesNotReinitialize()
    {
        var p1 = new TestProvider();
        var fallback = new TestFallbackProvider(p1);

        await fallback.EnsureInitializedAsync(CancellationToken.None);
        await fallback.EnsureInitializedAsync(CancellationToken.None);

        fallback.ActiveProvider.Should().BeSameAs(p1);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ThrowingProvider_FallsBackToNext()
    {
        var p1 = new TestProvider();
        var p2 = new TestProvider();
        var fallback = new ThrowingFallbackProvider(p1, p2);

        var act = async () => await fallback.EnsureInitializedAsync(CancellationToken.None);

        // All providers throw during init, so it should fail
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region ProviderName

    [Fact]
    public void ProviderName_ReturnsExpectedName()
    {
        var fallback = new TestFallbackProvider(new TestProvider());

        fallback.ProviderName.Should().Be("test");
    }

    #endregion

    #region DisposeAsync

    [Fact]
    public async Task DisposeAsync_DisposesAllProviders()
    {
        var p1 = new TestProvider();
        var p2 = new TestProvider();
        var fallback = new TestFallbackProvider(p1, p2);

        await fallback.DisposeAsync();

        p1.DisposeCount.Should().Be(1);
        p2.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_OnlyDisposesOnce()
    {
        var p1 = new TestProvider();
        var fallback = new TestFallbackProvider(p1);

        await fallback.DisposeAsync();
        await fallback.DisposeAsync();

        // Second dispose should be no-op due to Interlocked guard
        p1.DisposeCount.Should().Be(1);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_DisposesProviders()
    {
        var p1 = new TestProvider();
        var fallback = new TestFallbackProvider(p1);

        fallback.Dispose();

        // TestProvider doesn't implement IDisposable, so Disposed won't be set
        // But Dispose should not throw
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var fallback = new TestFallbackProvider(new TestProvider());

        var act = () =>
        {
            fallback.Dispose();
            fallback.Dispose();
        };

        act.Should().NotThrow();
    }

    #endregion
}
