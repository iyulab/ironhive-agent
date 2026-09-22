using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
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
        // The filter judges on the same evaluator the rest of the container uses — a consumer that
        // registered its own IPermissionEvaluator must not find the filter quietly judging on a
        // different one. TryAdd keeps a consumer's own registration.
        services.TryAddSingleton<IModeToolFilter>(sp =>
            new ModeToolFilter(sp.GetRequiredService<IPermissionEvaluator>()));
        services.AddSingleton<IAvailableToolsContext, AvailableToolsContext>();

        // Register context management
        services.AddSingleton<IContextTokenCounter, ContextTokenCounter>();
        services.AddSingleton<ICompactionTrigger>(sp =>
        {
            var compactionConfig = sp.GetService<CompactionConfig>() ?? new CompactionConfig();
            return new TokenBasedCompactionTrigger(
                compactionConfig.ProtectRecentTokens,
                compactionConfig.MinimumPruneTokens);
        });
        services.AddSingleton<IHistoryCompactor>(sp =>
        {
            var compactionConfig = sp.GetService<CompactionConfig>() ?? new CompactionConfig();
            var tokenCounter = sp.GetRequiredService<IContextTokenCounter>();
            return new TokenBasedHistoryCompactor(tokenCounter, compactionConfig);
        });
        services.AddSingleton<ContextManager>();

        // Register permission evaluation (on the consumer's PermissionConfig when one is registered)
        services.TryAddSingleton<IPermissionEvaluator>(sp =>
            new PermissionEvaluator(sp.GetService<PermissionConfig>()));

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
        var config = new PermissionConfig();
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
}
