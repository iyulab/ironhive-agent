namespace IronHive.Agent.Providers;

/// <summary>
/// Base class for composite providers with automatic fallback.
/// Tries providers in order until one succeeds.
/// </summary>
/// <typeparam name="TProvider">The provider interface type.</typeparam>
public abstract class FallbackProviderBase<TProvider> : IAsyncDisposable, IDisposable
    where TProvider : class, IAsyncDisposable
{
    private readonly TProvider[] _providers;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile TProvider? _activeProvider;
    private int _disposed;

    /// <summary>
    /// Gets the array of providers.
    /// </summary>
    protected TProvider[] Providers => _providers;

    protected FallbackProviderBase(params TProvider[] providers)
    {
        if (providers == null || providers.Length == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        _providers = providers;
    }

    /// <summary>
    /// Gets the provider name.
    /// </summary>
    public abstract string ProviderName { get; }

    /// <summary>
    /// Gets whether any provider is available.
    /// </summary>
    public bool IsAvailable => _providers.Any(IsProviderAvailable);

    /// <summary>
    /// Gets the currently active provider.
    /// </summary>
    protected TProvider? ActiveProvider => _activeProvider;

    /// <summary>
    /// Checks if a specific provider is available.
    /// </summary>
    protected abstract bool IsProviderAvailable(TProvider provider);

    /// <summary>
    /// Tries to initialize and verify a provider works.
    /// </summary>
    protected abstract ValueTask<bool> TryInitializeProviderAsync(TProvider provider, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a provider is initialized and ready.
    /// </summary>
    protected async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_activeProvider != null)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after lock
            if (_activeProvider != null)
            {
                return;
            }

            foreach (var provider in _providers)
            {
                if (!IsProviderAvailable(provider))
                {
                    continue;
                }

                try
                {
                    if (await TryInitializeProviderAsync(provider, cancellationToken))
                    {
                        _activeProvider = provider;
                        return;
                    }
                }
                catch
                {
                    // Provider failed, try next
                }
            }

            throw new InvalidOperationException($"No available {typeof(TProvider).Name} provider.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        foreach (var provider in _providers)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
