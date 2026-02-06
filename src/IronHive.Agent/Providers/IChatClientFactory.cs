using Microsoft.Extensions.AI;

namespace IronHive.Agent.Providers;

/// <summary>
/// Factory for creating IChatClient instances with runtime configuration.
/// </summary>
public interface IChatClientFactory
{
    /// <summary>
    /// Creates an IChatClient with optional model override.
    /// </summary>
    /// <param name="modelOverride">Model to use instead of the default. If null, uses the default model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured IChatClient instance.</returns>
    Task<IChatClient> CreateAsync(string? modelOverride = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an IChatClient with a specific provider and optional model override.
    /// </summary>
    /// <param name="providerName">Provider name (e.g., "gpustack", "lmsupply").</param>
    /// <param name="modelOverride">Model to use instead of the default. If null, uses the default model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured IChatClient instance.</returns>
    Task<IChatClient> CreateAsync(string providerName, string? modelOverride, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of available provider names.
    /// </summary>
    IReadOnlyList<string> AvailableProviders { get; }

    /// <summary>
    /// Gets the default provider name.
    /// </summary>
    string DefaultProviderName { get; }

    /// <summary>
    /// Gets available models from all configured providers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available models from all providers.</returns>
    Task<IReadOnlyList<AvailableModelInfo>> GetAllAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets available models from a specific provider.
    /// </summary>
    /// <param name="providerName">Provider name (e.g., "openai", "anthropic").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available models from the specified provider.</returns>
    Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(string providerName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for creating a chat client.
/// </summary>
public record ChatClientOptions
{
    /// <summary>
    /// Provider name (e.g., "gpustack", "lmsupply"). If null, uses the default provider.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Model to use. If null, uses the provider's default model.
    /// </summary>
    public string? Model { get; init; }
}
