using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Invocation;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tracking;
using IronHive.Agent.Webhook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Extensions;

/// <summary>
/// Extension methods for configuring IronHive Agent services in DI.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Adds IronHive Agent services to the service collection.
    /// This provides the core agent infrastructure without CLI-specific dependencies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronHiveAgent(
        this IServiceCollection services,
        Action<AgentServicesOptions>? configure = null)
    {
        var options = new AgentServicesOptions();
        configure?.Invoke(options);

        // Register usage tracking
        services.AddSingleton<IUsageTracker, UsageTracker>();

        if (options.UsageLimits is not null)
        {
            services.AddSingleton(options.UsageLimits);
            services.AddSingleton<UsageLimiter>();
            // Also exposed as IUsageLimiter so a consumer's AgentLoop factory can resolve it
            // without depending on the concrete type -- AgentLoop's own constructor takes the
            // interface and enforces the configured limit around each model call.
            services.AddSingleton<IUsageLimiter>(sp => sp.GetRequiredService<UsageLimiter>());
        }

        // Register mode management
        services.AddSingleton<IModeManager, ModeManager>();
        // Permission evaluation (on the consumer's PermissionConfig when one is registered) and the mode filter over
        // it; a consumer's own registration of either is kept.
        services.TryAddPermissionServices();
        services.AddSingleton<IAvailableToolsContext, AvailableToolsContext>();

        // Register context management. The container's ContextManager applies the registered CompactionConfig
        // (AddIronHiveAgentContext) in full — trigger, compactor, tool-result compaction, observation masking, goal
        // reminder, CompactOnOverflow, TargetRatio — the same way ContextManager.ForModel does. It is built on the
        // container's token counter: with no model to name, the window is CompactionConfig.MaxContextTokens, or a guess
        // that the first overflow corrects (CompactOnOverflow).
        services.AddSingleton<IContextTokenCounter>(sp =>
            new ContextTokenCounter(maxContextTokens: (sp.GetService<CompactionConfig>() ?? new CompactionConfig()).MaxContextTokens));
        services.AddSingleton(sp => ContextManager.FromConfig(
            sp.GetRequiredService<IContextTokenCounter>(),
            sp.GetService<CompactionConfig>() ?? new CompactionConfig(),
            summarizer: null,
            instructionContributors: sp.GetServices<ISystemInstructionContributor>()));

        // Register error recovery
        if (options.ErrorRecovery is not null)
        {
            services.AddSingleton(options.ErrorRecovery);
        }
        services.AddSingleton<IErrorRecoveryService>(sp =>
        {
            var config = sp.GetService<ErrorRecoveryConfig>();
            return new ErrorRecoveryService(config);
        });

        // Register webhook service
        if (options.Webhook is not null)
        {
            services.AddSingleton(options.Webhook);
        }
        services.AddSingleton<IWebhookService>(sp =>
        {
            var config = sp.GetService<WebhookConfig>();
            var httpClient = sp.GetService<HttpClient>();
            var logger = sp.GetService<ILogger<WebhookService>>();
            return new WebhookService(config, httpClient, logger);
        });

        // Register MCP plugin manager
        services.AddSingleton<IMcpPluginManager, McpPluginManager>();

        // The tool invocation pipeline (UseToolInvocationPipeline, the Ironbees adapter) with its default loop guards,
        // registered first so they are the outermost steps. The permission gate is not among them: it is opt-in
        // (AddIronHiveAgentApprovalGate).
        if (options.ToolInvocation is not null)
        {
            services.AddSingleton(options.ToolInvocation);
        }
        services.AddToolInvocationMiddleware<ArgumentParseFailureMiddleware>();
        services.AddToolInvocationMiddleware<RepeatedCallGuardMiddleware>();
        services.AddToolInvocationMiddleware<RepeatedErrorGuardMiddleware>();

        // IPlanExecutor is not registered by default — consumers should register it
        // at the application layer where IChatClient is available.
        // Example: services.AddTransient<IPlanExecutor>(sp =>
        //     new DefaultPlanExecutor(sp.GetRequiredService<IChatClient>(), tools));

        return services;
    }

    /// <summary>
    /// Adds IronHive Agent context management services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action for compaction.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronHiveAgentContext(
        this IServiceCollection services,
        Action<CompactionConfig>? configure = null)
    {
        var config = new CompactionConfig();
        configure?.Invoke(config);

        services.AddSingleton(config);

        return services;
    }

    /// <summary>
    /// Adds Agent Skills (<c>SKILL.md</c> bundles) to the container: a <see cref="Skills.SkillsLoader"/> built from
    /// <paramref name="config"/>, and its <see cref="Context.ISystemInstructionContributor"/> so a loop built with the
    /// container's contributors carries the skills' metadata. The <c>load_skill</c> tool is
    /// <see cref="Skills.SkillsLoader.LoadTool"/> — a host adds it to the loop's tools where it assembles them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Roots, enable/exclude, per-session filter and metadata budget.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAgentSkills(this IServiceCollection services, Skills.SkillsConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton(config);
        services.AddSingleton(sp => Skills.SkillsLoader.Create(sp.GetRequiredService<Skills.SkillsConfig>()));
        services.AddSingleton<Context.ISystemInstructionContributor>(sp => sp.GetRequiredService<Skills.SkillsLoader>().Contributor);
        return services;
    }

    /// <summary>
    /// Adds IronHive Agent permission services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action for permissions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddIronHiveAgentPermissions(
        this IServiceCollection services,
        Action<PermissionConfig>? configure = null)
    {
        // Starts from the default rules (PermissionConfig.CreateDefault), as the container does when no configuration
        // is registered: configuring one rule must not silently drop the rest.
        var config = PermissionConfig.CreateDefault();
        configure?.Invoke(config);

        services.AddSingleton(config);

        return services;
    }
}

/// <summary>
/// Options for configuring IronHive Agent services.
/// </summary>
public class AgentServicesOptions
{
    /// <summary>
    /// Usage limits configuration. If null, no limits are enforced.
    /// </summary>
    public UsageLimitsConfig? UsageLimits { get; set; }

    /// <summary>
    /// Error recovery configuration. If null, defaults are used.
    /// </summary>
    public ErrorRecoveryConfig? ErrorRecovery { get; set; }

    /// <summary>
    /// Webhook configuration. If null, webhooks are disabled.
    /// </summary>
    public WebhookConfig? Webhook { get; set; }

    /// <summary>
    /// Thresholds of the loop guards in the container's tool invocation pipeline. If null, defaults are used.
    /// </summary>
    public ToolInvocationOptions? ToolInvocation { get; set; }
}
