using Microsoft.Extensions.AI;

namespace IronHive.Agent.Providers;

/// <summary>
/// Provider interface for obtaining IChatClient instances.
/// </summary>
public interface IChatClientProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider name (e.g., "gpustack", "lmsupply").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Gets whether this provider is available and configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the IChatClient instance for this provider.
    /// </summary>
    /// <param name="modelOverride">Model to use instead of the default. If null, uses the default model.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The IChatClient instance.</returns>
    Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the provider's backend service is reachable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the service is reachable.</returns>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the list of available models from this provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available models. Returns empty list if provider is not available.</returns>
    Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Default implementation returns empty list
        return Task.FromResult<IReadOnlyList<AvailableModelInfo>>(Array.Empty<AvailableModelInfo>());
    }
}
