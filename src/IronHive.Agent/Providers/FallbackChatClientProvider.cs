using Microsoft.Extensions.AI;

namespace IronHive.Agent.Providers;

/// <summary>
/// Composite chat client provider with automatic fallback.
/// Tries providers in order until one succeeds.
/// </summary>
public sealed class FallbackChatClientProvider : IChatClientProvider, IDisposable
{
    private readonly IChatClientProvider[] _providers;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile IChatClientProvider? _activeProvider;

    public FallbackChatClientProvider(params IChatClientProvider[] providers)
    {
        if (providers == null || providers.Length == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }

        _providers = providers;
    }

    /// <inheritdoc />
    public string ProviderName => _activeProvider?.ProviderName ?? "fallback";

    /// <inheritdoc />
    public bool IsAvailable => _providers.Any(p => p.IsAvailable);

    /// <inheritdoc />
    public async Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        var active = _activeProvider;
        if (active != null)
        {
            return await active.GetChatClientAsync(modelOverride, cancellationToken);
        }

        // Auto-initialize: try to find and initialize a provider
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after lock
            if (_activeProvider != null)
            {
                return await _activeProvider.GetChatClientAsync(modelOverride, cancellationToken);
            }

            foreach (var provider in _providers)
            {
                if (provider.IsAvailable)
                {
                    // Try to initialize the provider
                    if (await provider.CheckHealthAsync(cancellationToken))
                    {
                        _activeProvider = provider;
                        return await provider.GetChatClientAsync(modelOverride, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            _initLock.Release();
        }

        throw new InvalidOperationException("No available chat client provider.");
    }

    /// <inheritdoc />
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable)
            {
                continue;
            }

            if (await provider.CheckHealthAsync(cancellationToken))
            {
                _activeProvider = provider;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the currently active provider.
    /// </summary>
    public IChatClientProvider? ActiveProvider => _activeProvider;

    /// <summary>
    /// Forces a switch to the next available provider.
    /// </summary>
    public async Task<bool> SwitchToNextProviderAsync(CancellationToken cancellationToken = default)
    {
        var startIndex = _activeProvider != null
            ? Array.IndexOf(_providers, _activeProvider) + 1
            : 0;

        for (var i = startIndex; i < _providers.Length; i++)
        {
            var provider = _providers[i];
            if (provider.IsAvailable && await provider.CheckHealthAsync(cancellationToken))
            {
                _activeProvider = provider;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        _initLock.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _initLock.Dispose();
    }
}
