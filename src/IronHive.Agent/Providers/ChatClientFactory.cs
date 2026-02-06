using Microsoft.Extensions.AI;

namespace IronHive.Agent.Providers;

/// <summary>
/// Default implementation of IChatClientFactory.
/// Creates IChatClient instances with optional model/provider override.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly IReadOnlyDictionary<string, IChatClientProvider> _providers;
    private readonly IChatClientProvider _defaultProvider;
    private readonly Func<IChatClient, IChatClient>? _clientDecorator;

    /// <summary>
    /// Creates a new ChatClientFactory.
    /// </summary>
    /// <param name="providers">Dictionary of provider name to provider instance.</param>
    /// <param name="defaultProvider">The default provider to use when no provider is specified.</param>
    /// <param name="clientDecorator">Optional decorator to apply to created clients (e.g., FunctionInvokingChatClient).</param>
    public ChatClientFactory(
        IReadOnlyDictionary<string, IChatClientProvider> providers,
        IChatClientProvider defaultProvider,
        Func<IChatClient, IChatClient>? clientDecorator = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _defaultProvider = defaultProvider ?? throw new ArgumentNullException(nameof(defaultProvider));
        _clientDecorator = clientDecorator;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AvailableProviders =>
        _providers.Where(p => p.Value.IsAvailable).Select(p => p.Key).ToList();

    /// <inheritdoc />
    public string DefaultProviderName => _defaultProvider.ProviderName;

    /// <inheritdoc />
    public async Task<IChatClient> CreateAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        // Parse "Provider/Model" format (e.g., "GpuStack/gpt-oss-20b" → provider: "gpustack", model: "gpt-oss-20b")
        if (!string.IsNullOrEmpty(modelOverride) && modelOverride.Contains('/'))
        {
            var parts = modelOverride.Split('/', 2);
            if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
            {
                var providerName = parts[0].ToLowerInvariant();
                var modelName = parts[1];

                // Check if parsed provider exists
                if (_providers.ContainsKey(providerName))
                {
                    return await CreateAsync(providerName, modelName, cancellationToken);
                }
                // If provider not found, fall through to use default provider with full model string
            }
        }

        var client = await _defaultProvider.GetChatClientAsync(modelOverride, cancellationToken);
        return _clientDecorator?.Invoke(client) ?? client;
    }

    /// <inheritdoc />
    public async Task<IChatClient> CreateAsync(string providerName, string? modelOverride, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(providerName.ToLowerInvariant(), out var provider))
        {
            throw new ArgumentException($"Provider '{providerName}' not found. Available: {string.Join(", ", AvailableProviders)}", nameof(providerName));
        }

        if (!provider.IsAvailable)
        {
            throw new InvalidOperationException($"Provider '{providerName}' is not available or configured.");
        }

        var client = await provider.GetChatClientAsync(modelOverride, cancellationToken);
        return _clientDecorator?.Invoke(client) ?? client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableModelInfo>> GetAllAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _providers
            .Where(p => p.Value.IsAvailable)
            .Select(async p =>
            {
                try
                {
                    return await p.Value.GetAvailableModelsAsync(cancellationToken);
                }
                catch
                {
                    return Array.Empty<AvailableModelInfo>();
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(string providerName, CancellationToken cancellationToken = default)
    {
        var normalizedName = providerName.ToLowerInvariant();

        if (!_providers.TryGetValue(normalizedName, out var provider))
        {
            return Array.Empty<AvailableModelInfo>();
        }

        if (!provider.IsAvailable)
        {
            return Array.Empty<AvailableModelInfo>();
        }

        try
        {
            return await provider.GetAvailableModelsAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<AvailableModelInfo>();
        }
    }
}
