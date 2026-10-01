using IronHive.Agent.Invocation;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IronHive.Agent.Extensions;

/// <summary>
/// Registers the steps of the container's <see cref="ToolInvocationPipeline"/> — what
/// <c>UseToolInvocationPipeline()</c> and the Ironbees adapter run every tool call through. Steps run in registration
/// order, the first registered outermost; <c>AddIronHiveAgent</c> registers the default loop guards first.
/// </summary>
public static class ToolInvocationServiceCollectionExtensions
{
    /// <summary>Adds <typeparamref name="TMiddleware"/> (a singleton) to the steps around each tool call.</summary>
    /// <remarks>A type already registered this way is not added twice.</remarks>
    public static IServiceCollection AddToolInvocationMiddleware<TMiddleware>(this IServiceCollection services)
        where TMiddleware : class, IToolInvocationMiddleware
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolInvocationMiddleware, TMiddleware>());
        return services.AddToolInvocationPipeline();
    }

    /// <summary>Adds the middleware <paramref name="factory"/> creates (once, a singleton) to the steps around each tool call.</summary>
    public static IServiceCollection AddToolInvocationMiddleware(
        this IServiceCollection services,
        Func<IServiceProvider, IToolInvocationMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(factory);
        return services.AddToolInvocationPipeline();
    }

    /// <summary>Adds <typeparamref name="TMiddleware"/> (a singleton) to the steps over each tool result.</summary>
    /// <remarks>A type already registered this way is not added twice.</remarks>
    public static IServiceCollection AddToolResultMiddleware<TMiddleware>(this IServiceCollection services)
        where TMiddleware : class, IToolResultMiddleware
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolResultMiddleware, TMiddleware>());
        return services.AddToolInvocationPipeline();
    }

    /// <summary>Adds the middleware <paramref name="factory"/> creates (once, a singleton) to the steps over each tool result.</summary>
    public static IServiceCollection AddToolResultMiddleware(
        this IServiceCollection services,
        Func<IServiceProvider, IToolResultMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(factory);
        return services.AddToolInvocationPipeline();
    }

    /// <summary>
    /// Turns on the permission gate: adds <see cref="ApprovalGateMiddleware"/> over the container's
    /// <see cref="IToolCallPolicy"/> and, for <c>Ask</c> verdicts, its <see cref="IHumanApprovalService"/> (without one an
    /// <c>Ask</c> verdict is refused). When the container has an <see cref="IModeManager"/> (<c>AddIronHiveAgent</c>
    /// registers one), Planning mode is enforced over its <see cref="IModeToolFilter"/>. Registers the default policy,
    /// filter and evaluator when none is registered. Without this call the pipeline has no gate.
    /// </summary>
    public static IServiceCollection AddIronHiveAgentApprovalGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddPermissionServices();
        return services.AddToolInvocationMiddleware<ApprovalGateMiddleware>();
    }

    /// <summary>
    /// Registers the container's <see cref="ToolInvocationPipeline"/> (once): the registered
    /// <see cref="IToolInvocationMiddleware"/>s and <see cref="IToolResultMiddleware"/>s in registration order, plus a
    /// <see cref="ToolResultGuardMiddleware"/> over the registered <see cref="IToolResultGuard"/> when there is one and no
    /// guard step was registered for it.
    /// </summary>
    public static IServiceCollection AddToolInvocationPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp =>
        {
            var results = sp.GetServices<IToolResultMiddleware>().ToList();
            if (sp.GetService<IToolResultGuard>() is { } guard
                && !results.OfType<ToolResultGuardMiddleware>().Any(m => ReferenceEquals(m.Guard, guard)))
            {
                results.Add(new ToolResultGuardMiddleware(guard, sp.GetService<ILogger<ToolResultGuardMiddleware>>()));
            }

            return new ToolInvocationPipeline(sp.GetServices<IToolInvocationMiddleware>(), results);
        });
        return services;
    }

    /// <summary>The permission evaluator, and the mode filter and tool-call policy over it, unless the consumer registered its own.</summary>
    internal static IServiceCollection TryAddPermissionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IPermissionEvaluator>(sp =>
            new PermissionEvaluator(sp.GetService<PermissionConfig>()));
        // The filter judges on the same evaluator the rest of the container uses — a consumer that
        // registered its own IPermissionEvaluator must not find the filter quietly judging on a
        // different one. TryAdd keeps a consumer's own registration.
        services.TryAddSingleton<IModeToolFilter>(sp =>
            new ModeToolFilter(sp.GetRequiredService<IPermissionEvaluator>()));
        services.TryAddSingleton<IToolCallPolicy>(sp =>
            new ToolCallPolicy(sp.GetRequiredService<IPermissionEvaluator>()));
        return services;
    }
}
