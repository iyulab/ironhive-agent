using FluxGuard.Remote.MCP;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;

namespace IronHive.Agent.FluxGuard;

/// <summary>
/// An <see cref="IToolResultGuard"/> backed by a FluxGuard <see cref="IMCPGuardrail"/> — the same guardrail the MCP path
/// uses (<see cref="FluxGuardMcpToolCallGuard"/>), so one configured guard covers in-process and MCP tool results alike.
/// In-process tools are reported to it under <paramref name="serverName"/>.
/// </summary>
public sealed class McpGuardrailToolResultGuard(IMCPGuardrail guardrail, string serverName = "in-process") : IToolResultGuard
{
    private readonly FluxGuardMcpToolCallGuard _guard = new(guardrail);

    /// <inheritdoc />
    public ValueTask<ToolResultVerdict> InspectAsync(ToolResultInspection inspection, CancellationToken cancellationToken)
        => _guard.CheckResultAsync(
            new McpToolCallInspection(serverName, inspection.ToolName, inspection.Arguments),
            inspection.Result,
            cancellationToken);
}
