using Ironbees.Core;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Tests.Mocks;

namespace IronHive.Agent.Tests.Ironbees;

public class ChatClientFrameworkAdapterTests
{
    [Fact]
    public async Task CreateAgentAsync_CreatesAgent()
    {
        // Arrange
        var mockClient = new MockChatClient().EnqueueResponse("Test response");
        var adapter = new ChatClientFrameworkAdapter(mockClient);

        var config = new AgentConfig
        {
            Name = "test-agent",
            Description = "A test agent",
            Version = "1.0.0",
            SystemPrompt = "You are a test assistant.",
            Model = new ModelConfig { Deployment = "test-model" }
        };

        // Act
        var agent = await adapter.CreateAgentAsync(config);

        // Assert
        Assert.NotNull(agent);
        Assert.Equal("test-agent", agent.Name);
        Assert.Equal("A test agent", agent.Description);
    }

    [Fact]
    public async Task RunAsync_ReturnsResponse()
    {
        // Arrange
        var mockClient = new MockChatClient().EnqueueResponse("Hello from agent!");
        var adapter = new ChatClientFrameworkAdapter(mockClient);

        var config = new AgentConfig
        {
            Name = "test-agent",
            Description = "A test agent",
            Version = "1.0.0",
            SystemPrompt = "You are a test assistant.",
            Model = new ModelConfig { Deployment = "test-model" }
        };

        var agent = await adapter.CreateAgentAsync(config);

        // Act
        var response = await adapter.RunAsync(agent, "Hello");

        // Assert
        Assert.Equal("Hello from agent!", response);
    }

    [Fact]
    public async Task StreamAsync_YieldsChunks()
    {
        // Arrange - MockChatClient streams text in 10-char chunks from EnqueueResponse
        var mockClient = new MockChatClient().EnqueueResponse("Hello World!");
        var adapter = new ChatClientFrameworkAdapter(mockClient);

        var config = new AgentConfig
        {
            Name = "test-agent",
            Description = "A test agent",
            Version = "1.0.0",
            SystemPrompt = "You are a test assistant.",
            Model = new ModelConfig { Deployment = "test-model" }
        };

        var agent = await adapter.CreateAgentAsync(config);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in adapter.StreamAsync(agent, "Hello"))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.True(chunks.Count >= 1);
        Assert.Equal("Hello World!", string.Concat(chunks));
    }

    [Fact]
    public async Task RunAsync_ThrowsForWrongAgentType()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var adapter = new ChatClientFrameworkAdapter(mockClient);
        var wrongAgent = new FakeAgent();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.RunAsync(wrongAgent, "Hello"));
    }

    private sealed class FakeAgent : IAgent
    {
        public string Name => "fake";
        public string Description => "Fake agent";
        public AgentConfig Config => new()
        {
            Name = "fake",
            Description = "Fake",
            Version = "1.0.0",
            SystemPrompt = "",
            Model = new ModelConfig { Deployment = "fake" }
        };
    }
}
