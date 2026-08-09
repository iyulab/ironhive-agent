using IronHive.Agent.Ironbees;
using Ironbees.Core;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

public class OrchestratedAgentLoopTests
{
    [Fact]
    public async Task RunAsync_WithNullOverrideOptions_DelegatesNormally()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.ProcessAsync("Hello", Arg.Any<CancellationToken>()).Returns("Hi there!");
        var loop = new OrchestratedAgentLoop(orchestrator);

        var response = await loop.RunAsync("Hello", overrideOptions: null);

        Assert.Equal("Hi there!", response.Content);
    }

    [Fact]
    public async Task RunAsync_WithNonNullOverrideOptions_ThrowsNotSupported()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var loop = new OrchestratedAgentLoop(orchestrator);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => loop.RunAsync("Hello", new ChatOptions { Temperature = 0.5f }));
    }

    [Fact]
    public async Task RunStreamingAsync_WithNonNullOverrideOptions_ThrowsNotSupported()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        var loop = new OrchestratedAgentLoop(orchestrator);

        async Task Act()
        {
            await foreach (var _ in loop.RunStreamingAsync("Hello", new ChatOptions { Temperature = 0.5f }))
            {
            }
        }

        await Assert.ThrowsAsync<NotSupportedException>(Act);
    }
}
