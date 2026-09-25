using FluxGuard.Remote.MCP;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IronHive.Agent.FluxGuard;

/// <summary>Registers the FluxGuard-backed tool guards.</summary>
public static class FluxGuardServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMcpToolCallGuard"/> (<see cref="FluxGuardMcpToolCallGuard"/>) and <see cref="IToolResultGuard"/>
    /// (<see cref="McpGuardrailToolResultGuard"/>) over the <see cref="IMCPGuardrail"/> in the container — register that
    /// first (FluxGuard's <c>AddFluxGuardMcpGuardrail()</c>, or your own). <c>AddIronHiveAgent</c>'s
    /// <c>McpPluginManager</c> then guards every MCP tool call, and the Ironbees adapter every in-process tool result.
    /// Existing registrations of either guard are kept.
    /// </summary>
    public static IServiceCollection AddIronHiveAgentFluxGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMcpToolCallGuard>(sp => new FluxGuardMcpToolCallGuard(sp.GetRequiredService<IMCPGuardrail>()));
        services.TryAddSingleton<IToolResultGuard>(sp => new McpGuardrailToolResultGuard(sp.GetRequiredService<IMCPGuardrail>()));
        return services;
    }
}
