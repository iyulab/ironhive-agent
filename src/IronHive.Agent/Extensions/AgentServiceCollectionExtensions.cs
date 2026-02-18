using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tracking;
using IronHive.Agent.Webhook;
using Microsoft.Extensions.DependencyInjection;
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
        }

        // Register mode management
        services.AddSingleton<IModeManager, ModeManager>();
        services.AddSingleton<IModeToolFilter>(sp =>
        {
            var permissionConfig = sp.GetService<PermissionConfig>();
            if (permissionConfig is not null)
            {
                return new ModeToolFilter(permissionConfig);
            }
            return new ModeToolFilter();
        });

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

        // Register permission evaluation
        services.AddSingleton<IPermissionEvaluator, PermissionEvaluator>();

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

        // Note: ISubAgentService requires IChatClient which is CLI-specific
        // It should be registered at the CLI layer where IChatClient is available

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
