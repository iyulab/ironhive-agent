using IndexThinking.Client;

namespace IronHive.Agent.Loop;

/// <summary>
/// Factory for creating IAgentLoop instances with runtime configuration.
/// </summary>
public interface IAgentLoopFactory
{
    /// <summary>
    /// Creates an IAgentLoop with the default configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured IAgentLoop instance.</returns>
    Task<IAgentLoop> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an IAgentLoop with custom options.
    /// </summary>
    /// <param name="options">Options for configuring the agent loop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured IAgentLoop instance.</returns>
    Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for creating an IAgentLoop instance.
/// </summary>
public record AgentLoopFactoryOptions
{
    /// <summary>
    /// Provider name (e.g., "gpustack", "lmsupply"). If null, uses the default provider.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Model to use. If null, uses the provider's default model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// System prompt to initialize the agent.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Temperature for response generation.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Maximum tokens for response generation.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Options for thinking/reasoning extraction. If null, uses defaults.
    /// </summary>
    public ThinkingChatClientOptions? ThinkingOptions { get; init; }

    /// <summary>
    /// Working directory for built-in tools. If null, uses current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }
}
