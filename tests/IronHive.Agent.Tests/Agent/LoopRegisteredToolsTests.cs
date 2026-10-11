using IndexThinking.Agents;
using Ironbees.Core;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="IAgentLoop.Tools"/> is the catalog a per-turn tool-name override selects from. A host holding only the loop
/// reads it there, instead of keeping a second copy beside its factory.
/// </summary>
public class LoopRegisteredToolsTests
{
    // Read through the interface: that is the surface a host holds.
    private static IReadOnlyList<AITool> ToolsOf(IAgentLoop loop) => loop.Tools;

    private static AITool[] TwoTools() =>
    [
        AIFunctionFactory.Create(() => "a", "read_file"),
        AIFunctionFactory.Create(() => "b", "write_file"),
    ];

    [Fact]
    public void AgentLoop_Tools_AreTheRegisteredTools()
    {
        var tools = TwoTools();
        var loop = new AgentLoop(new MockChatClient(), new AgentOptions { Tools = [.. tools] });

        Assert.Equal(["read_file", "write_file"], ToolsOf(loop).Select(t => t.Name));
    }

    [Fact]
    public void AgentLoop_Tools_IsASnapshot_NotTheOptionsList()
    {
        var options = new AgentOptions { Tools = [.. TwoTools()] };
        var loop = new AgentLoop(new MockChatClient(), options);

        var before = ToolsOf(loop);
        options.Tools!.Clear();

        Assert.Equal(2, before.Count);
    }

    [Fact]
    public void AgentLoop_WithoutTools_IsEmpty()
    {
        var loop = new AgentLoop(new MockChatClient());

        Assert.Empty(ToolsOf(loop));
    }

    [Fact]
    public void ThinkingAgentLoop_Tools_AreTheRegisteredTools()
    {
        var loop = new ThinkingAgentLoop(
            new MockChatClient(), Substitute.For<IThinkingTurnManager>(), new AgentOptions { Tools = [.. TwoTools()] });

        Assert.Equal(["read_file", "write_file"], ToolsOf(loop).Select(t => t.Name));
    }

    [Fact]
    public void OrchestratedAgentLoop_Tools_IsEmpty_TheRoutedAgentsHoldTheirOwn()
    {
        var loop = new OrchestratedAgentLoop(Substitute.For<IAgentOrchestrator>());

        Assert.Empty(ToolsOf(loop));
    }
}
