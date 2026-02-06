using Ironbees.Core;
using Ironbees.Core.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IronHive.Agent.Ironbees;

/// <summary>
/// Extension methods for configuring Ironbees services in DI.
/// </summary>
public static class IronbeesServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ironbees multi-agent orchestration services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure Ironbees options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronbees(
        this IServiceCollection services,
        Action<IronbeesOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new IronbeesOptions();
        configure(options);

        // Register agent loader
        services.AddSingleton<IAgentLoader>(sp =>
            new FileSystemAgentLoader());

        // Register agent registry
        services.AddSingleton<IAgentRegistry, AgentRegistry>();

        // Register agent selector based on options
        services.AddSingleton<IAgentSelector>(sp =>
        {
            return options.SelectorType switch
            {
                AgentSelectorType.Keyword => new KeywordAgentSelector(),
                AgentSelectorType.Embedding => CreateEmbeddingSelector(options),
                AgentSelectorType.Hybrid => CreateHybridSelector(options),
                _ => new KeywordAgentSelector()
            };
        });

        // Register framework adapter
        services.AddSingleton<ILLMFrameworkAdapter>(sp =>
        {
            if (options.ChatClientFactory != null)
            {
                return new ChatClientFrameworkAdapter(options.ChatClientFactory);
            }

            var chatClient = sp.GetService<IChatClient>();
            if (chatClient != null)
            {
                return new ChatClientFrameworkAdapter(chatClient);
            }

            throw new InvalidOperationException(
                "No IChatClient available. Either configure ChatClientFactory in IronbeesOptions or register IChatClient in DI.");
        });

        // Register orchestrator
        services.AddSingleton<IAgentOrchestrator>(sp =>
        {
            var loader = sp.GetRequiredService<IAgentLoader>();
            var registry = sp.GetRequiredService<IAgentRegistry>();
            var adapter = sp.GetRequiredService<ILLMFrameworkAdapter>();
            var selector = sp.GetRequiredService<IAgentSelector>();

            return new AgentOrchestrator(
                loader,
                registry,
                adapter,
                selector,
                options.AgentsDirectory);
        });

        // Register OrchestratedAgentLoop
        services.AddTransient<OrchestratedAgentLoop>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IAgentOrchestrator>();
            return new OrchestratedAgentLoop(orchestrator, options.DefaultAgentName);
        });

        return services;
    }

    private static EmbeddingAgentSelector CreateEmbeddingSelector(IronbeesOptions options)
    {
        if (options.EmbeddingProvider == null)
        {
            throw new InvalidOperationException(
                "EmbeddingProvider must be configured when using Embedding or Hybrid selector. " +
                "Use OnnxEmbeddingProvider.CreateAsync() to create one.");
        }

        return new EmbeddingAgentSelector(options.EmbeddingProvider);
    }

    private static HybridAgentSelector CreateHybridSelector(IronbeesOptions options)
    {
        var keywordSelector = new KeywordAgentSelector();
        var embeddingSelector = CreateEmbeddingSelector(options);

        return new HybridAgentSelector(
            keywordSelector,
            embeddingSelector,
            options.HybridKeywordWeight,
            1.0 - options.HybridKeywordWeight);
    }
}

/// <summary>
/// Configuration options for Ironbees integration.
/// </summary>
public class IronbeesOptions
{
    /// <summary>
    /// Directory containing agent configurations (agents/{name}/agent.yaml).
    /// Defaults to "./agents".
    /// </summary>
    public string AgentsDirectory { get; set; } = "./agents";

    /// <summary>
    /// Default agent name to use instead of auto-selection.
    /// If null, auto-selection will be used.
    /// </summary>
    public string? DefaultAgentName { get; set; }

    /// <summary>
    /// Type of agent selector to use.
    /// </summary>
    public AgentSelectorType SelectorType { get; set; } = AgentSelectorType.Keyword;

    /// <summary>
    /// Custom embedding provider for embedding-based selection.
    /// If null, default ONNX provider is used.
    /// </summary>
    public IEmbeddingProvider? EmbeddingProvider { get; set; }

    /// <summary>
    /// Weight for keyword matching in hybrid selector (0.0 to 1.0).
    /// Default is 0.3 (30% keyword, 70% embedding).
    /// </summary>
    public double HybridKeywordWeight { get; set; } = 0.3;

    /// <summary>
    /// Factory function to create IChatClient from ModelConfig.
    /// If null, IChatClient will be resolved from DI.
    /// </summary>
    public Func<ModelConfig, IChatClient>? ChatClientFactory { get; set; }
}

/// <summary>
/// Type of agent selector to use.
/// </summary>
public enum AgentSelectorType
{
    /// <summary>
    /// Keyword-based matching using tags and capabilities.
    /// Fast but less accurate for complex queries.
    /// </summary>
    Keyword,

    /// <summary>
    /// Semantic embedding-based matching.
    /// More accurate but requires embedding model.
    /// </summary>
    Embedding,

    /// <summary>
    /// Hybrid approach combining keyword and embedding.
    /// Best balance of speed and accuracy.
    /// </summary>
    Hybrid
}
