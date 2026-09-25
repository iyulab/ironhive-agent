using AwesomeAssertions;
using FluxGuard.Remote.MCP;
using IronHive.Agent.FluxGuard;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// The FluxGuard backing of the core MCP guard seam (<see cref="IMcpToolCallGuard"/>, IronHive.Agent 0.19.0): FluxGuard's
/// validation results map onto the core verdicts, and <c>AddIronHiveAgentFluxGuard</c> puts both guards in the container
/// that <c>AddIronHiveAgent</c>'s <see cref="McpPluginManager"/> and the Ironbees adapter resolve.
/// </summary>
public class FluxGuardMcpToolCallGuardTests
{
    private static readonly McpToolCallInspection Call = new("files", "read", new Dictionary<string, object?> { ["path"] = "a.txt", ["encoding"] = null });

    [Fact]
    public async Task Request_that_should_block_is_blocked_with_the_guardrail_reason()
    {
        var guardrail = Substitute.For<IMCPGuardrail>();
        guardrail.ValidateToolCallAsync(Arg.Any<MCPToolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = false, ShouldBlock = true, Reason = "unknown server" });

        var verdict = await new FluxGuardMcpToolCallGuard(guardrail).CheckCallAsync(Call, TestContext.Current.CancellationToken);

        verdict.Blocked.Should().BeTrue();
        verdict.Reason.Should().Be("unknown server");
    }

    [Fact]
    public async Task Valid_request_is_allowed_and_reaches_the_guardrail_with_server_tool_and_arguments()
    {
        var guardrail = Substitute.For<IMCPGuardrail>();
        MCPToolRequest? seen = null;
        guardrail.ValidateToolCallAsync(Arg.Do<MCPToolRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = true });

        var verdict = await new FluxGuardMcpToolCallGuard(guardrail).CheckCallAsync(Call, TestContext.Current.CancellationToken);

        verdict.Blocked.Should().BeFalse();
        seen!.ServerName.Should().Be("files");
        seen.ToolName.Should().Be("read");
        seen.Arguments!["path"].Should().Be("a.txt");
        seen.Arguments["encoding"].Should().Be(string.Empty, "FluxGuard's argument map takes no nulls");
    }

    [Fact]
    public async Task Invalid_request_without_a_reason_is_blocked_as_a_policy_violation()
    {
        var guardrail = Substitute.For<IMCPGuardrail>();
        guardrail.ValidateToolCallAsync(Arg.Any<MCPToolRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = false });

        var verdict = await new FluxGuardMcpToolCallGuard(guardrail).CheckCallAsync(Call, TestContext.Current.CancellationToken);

        verdict.Blocked.Should().BeTrue();
        verdict.Reason.Should().Be("policy violation");
    }

    [Fact]
    public async Task Result_maps_to_withhold_replace_or_allow()
    {
        var guardrail = Substitute.For<IMCPGuardrail>();
        guardrail.ValidateToolResultAsync(Arg.Any<MCPToolRequest>(), "bad", Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = false, ShouldBlock = true, Reason = "injection" });
        guardrail.ValidateToolResultAsync(Arg.Any<MCPToolRequest>(), "dirty", Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = true, SanitizedResult = "clean" });
        guardrail.ValidateToolResultAsync(Arg.Any<MCPToolRequest>(), "fine", Arg.Any<CancellationToken>())
            .Returns(new MCPValidationResult { IsValid = true });
        var sut = new FluxGuardMcpToolCallGuard(guardrail);
        var ct = TestContext.Current.CancellationToken;

        var withheld = await sut.CheckResultAsync(Call, "bad", ct);
        var replaced = await sut.CheckResultAsync(Call, "dirty", ct);
        var allowed = await sut.CheckResultAsync(Call, "fine", ct);

        withheld.Withheld.Should().BeTrue();
        withheld.Reason.Should().Be("injection");
        replaced.Withheld.Should().BeFalse();
        replaced.Replacement.Should().Be("clean");
        allowed.Withheld.Should().BeFalse();
        allowed.Replacement.Should().BeNull();
    }

    [Fact]
    public void AddIronHiveAgentFluxGuard_registers_both_guards_over_the_registered_guardrail()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMCPGuardrail>());

        services.AddIronHiveAgentFluxGuard();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMcpToolCallGuard>().Should().BeOfType<FluxGuardMcpToolCallGuard>();
        provider.GetRequiredService<IToolResultGuard>().Should().BeOfType<McpGuardrailToolResultGuard>();
    }

    [Fact]
    public void AddIronHiveAgentFluxGuard_keeps_a_guard_the_host_registered_first()
    {
        var own = Substitute.For<IMcpToolCallGuard>();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMCPGuardrail>());
        services.AddSingleton(own);

        services.AddIronHiveAgentFluxGuard();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMcpToolCallGuard>().Should().BeSameAs(own);
    }
}
