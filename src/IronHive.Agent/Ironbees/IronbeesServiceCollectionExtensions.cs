using Ironbees.Core;
using Ironbees.Core.Conversation;
using Ironbees.Core.Embeddings;
using IronHive.Agent.Mcp;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tools;
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

        // Register framework adapter with tool execution loop support
        services.AddSingleton<ILLMFrameworkAdapter>(sp =>
        {
            var clientFactory = options.ChatClientFactory;
            if (clientFactory is null)
            {
                var chatClient = sp.GetService<IChatClient>();
                if (chatClient is null)
                {
                    throw new InvalidOperationException(
                        "No IChatClient available. Either configure ChatClientFactory in IronbeesOptions or register IChatClient in DI.");
                }
                clientFactory = _ => chatClient;
            }

            var permissionEvaluator = sp.GetService<IPermissionEvaluator>();

            // Dynamic tool factory: resolves tools at invocation time (supports MCP hot reload)
            Func<IList<AITool>>? toolsFactory = null;
            if (options.EnableToolExecution)
            {
                var workingDirectory = options.WorkingDirectory;
                toolsFactory = () =>
                {
                    var builtIn = BuiltInTools.GetAll(workingDirectory);
                    var mcpManager = sp.GetService<IMcpPluginManager>();
                    if (mcpManager is not null)
                    {
                        var mcpTools = mcpManager.GetToolsAsync().GetAwaiter().GetResult();
                        return builtIn.Concat(mcpTools).ToList();
                    }
                    return builtIn;
                };
            }

            return new ChatClientFrameworkAdapter(
                clientFactory,
                toolsFactory,
                permissionEvaluator,
                options.MaxToolTurns);
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

        // Register ConversationStore if configured
        if (options.ConversationsDirectory is not null)
        {
            services.AddSingleton<IConversationStore>(sp =>
                new FileSystemConversationStore(options.ConversationsDirectory));
        }

        // Register OrchestratedAgentLoop
        services.AddTransient<OrchestratedAgentLoop>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IAgentOrchestrator>();
            var conversationStore = sp.GetService<IConversationStore>();
            return new OrchestratedAgentLoop(orchestrator, options.DefaultAgentName, conversationStore);
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

    /// <summary>
    /// Enable tool execution loop in the framework adapter.
    /// When true, agents can execute tools autonomously.
    /// </summary>
    public bool EnableToolExecution { get; set; }

    /// <summary>
    /// Working directory for built-in tools.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Maximum tool execution turns to prevent infinite loops.
    /// </summary>
    public int MaxToolTurns { get; set; } = 20;

    /// <summary>
    /// Directory for conversation persistence.
    /// If null, conversation store is not registered.
    /// </summary>
    public string? ConversationsDirectory { get; set; }
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
