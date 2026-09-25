using FluxGuard.Remote.MCP;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;

namespace IronHive.Agent.FluxGuard;

/// <summary>
/// An <see cref="IMcpToolCallGuard"/> backed by a FluxGuard <see cref="IMCPGuardrail"/> (e.g. <c>MCPToolValidator</c>):
/// the request is checked with <see cref="IMCPGuardrail.ValidateToolCallAsync"/>, the result with
/// <see cref="IMCPGuardrail.ValidateToolResultAsync"/>. A validation that should block, or is not valid, blocks the call
/// or withholds the result; a sanitized result replaces the original.
/// </summary>
public sealed class FluxGuardMcpToolCallGuard(IMCPGuardrail guardrail) : IMcpToolCallGuard
{
    private readonly IMCPGuardrail _guardrail = guardrail ?? throw new ArgumentNullException(nameof(guardrail));

    /// <inheritdoc />
    public async ValueTask<McpToolCallVerdict> CheckCallAsync(McpToolCallInspection call, CancellationToken cancellationToken)
    {
        var validation = await _guardrail.ValidateToolCallAsync(ToRequest(call), cancellationToken);
        return validation.ShouldBlock || !validation.IsValid
            ? McpToolCallVerdict.Block(validation.Reason ?? "policy violation")
            : McpToolCallVerdict.Allow();
    }

    /// <inheritdoc />
    public async ValueTask<ToolResultVerdict> CheckResultAsync(McpToolCallInspection call, string result, CancellationToken cancellationToken)
    {
        var validation = await _guardrail.ValidateToolResultAsync(ToRequest(call), result, cancellationToken);
        if (validation.ShouldBlock || !validation.IsValid)
        {
            return ToolResultVerdict.Withhold(validation.Reason ?? "policy violation");
        }
        return validation.SanitizedResult is { } sanitized ? ToolResultVerdict.Replace(sanitized) : ToolResultVerdict.Allow();
    }

    internal static MCPToolRequest ToRequest(McpToolCallInspection call) => new()
    {
        ServerName = call.ServerName,
        ToolName = call.ToolName,
        Arguments = call.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value ?? (object)string.Empty)
    };
}
