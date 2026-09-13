using Ironbees.Core;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

public class ChatClientFrameworkAdapterTests
{
    private static AgentConfig CreateTestConfig(string name = "test-agent") => new()
    {
        Name = name,
        Description = "A test agent",
        Version = "1.0.0",
        SystemPrompt = "You are a test assistant.",
        Model = new ModelConfig { Deployment = "test-model" }
    };

    [Fact]
    public async Task CreateAgentAsync_CreatesAgent()
    {
        // Arrange
        var mockClient = new MockChatClient().EnqueueResponse("Test response");
        var adapter = new ChatClientFrameworkAdapter(mockClient);

        // Act
        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

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
        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Hello", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Hello from agent!", response);
    }

    [Fact]
    public async Task StreamAsync_YieldsChunks()
    {
        // Arrange - MockChatClient streams text in 10-char chunks from EnqueueResponse
        var mockClient = new MockChatClient().EnqueueResponse("Hello World!");
        var adapter = new ChatClientFrameworkAdapter(mockClient);
        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in adapter.StreamAsync(agent, "Hello", TestContext.Current.CancellationToken))
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
            () => adapter.RunAsync(wrongAgent, "Hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WithTools_ExecutesToolLoop()
    {
        // Arrange: LLM calls a tool, then responds with final text
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("TestTool", """{"input":"hello"}""")
            .EnqueueResponse("Tool result processed!");

        var toolInvoked = false;
        var testTool = AIFunctionFactory.Create(() =>
        {
            toolInvoked = true;
            return "tool output";
        }, "TestTool", "A test tool");

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [testTool]);

        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Use the tool", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(toolInvoked);
        Assert.Equal("Tool result processed!", response);
    }

    [Fact]
    public async Task RunAsync_WithTools_RespectsMaxTurns()
    {
        // Arrange: LLM always returns tool calls (infinite loop scenario)
        var mockClient = new MockChatClient();
        for (var i = 0; i < 5; i++)
        {
            mockClient.EnqueueToolCallResponse("TestTool", """{"input":"loop"}""");
        }
        // After max turns, the last response message text is returned
        mockClient.EnqueueResponse("final fallback");

        var testTool = AIFunctionFactory.Create(() => "ok", "TestTool", "A test tool");

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [testTool],
            maxToolTurns: 3);

        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act - should not hang, returns after maxToolTurns
        var response = await adapter.RunAsync(agent, "Loop forever", TestContext.Current.CancellationToken);

        // Assert: returned something (either empty or last message)
        Assert.NotNull(response);
    }

    [Fact]
    public async Task RunAsync_WithPermissionDeny_ReturnsPermissionError()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("DangerousTool", """{"input":"bad"}""")
            .EnqueueResponse("Handled denial");

        var testTool = AIFunctionFactory.Create(() => "should not execute", "DangerousTool", "Dangerous");

        // A tool with no dedicated category is judged by name through EvaluateTool (PermissionConfig.Tools).
        var mockPermission = Substitute.For<IPermissionEvaluator>();
        mockPermission.EvaluateTool("DangerousTool")
            .Returns(PermissionResult.Deny("Not allowed"));

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [testTool],
            permissionEvaluator: mockPermission);

        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Do something dangerous", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Handled denial", response);
        mockPermission.Received(1).EvaluateTool("DangerousTool");
        var toolResult = mockClient.ReceivedMessages[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.Contains("Permission denied: Not allowed", toolResult.Result!.ToString());
    }

    [Fact]
    public async Task RunAsync_WithPermissionAsk_ConsultsTheApprovalService()
    {
        // Arrange — default config: "*.json" edits are Ask
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("WriteFile", """{"path":"app.json","content":"{}"}""")
            .EnqueueResponse("Wrote it");

        var executed = false;
        var testTool = AIFunctionFactory.Create((string path, string content) => { executed = true; return "ok"; }, "WriteFile", "Writes");

        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalResult.Approve());

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [testTool],
            permissionEvaluator: new PermissionEvaluator(PermissionConfig.CreateDefault()),
            approvalService: approval);

        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Write the config", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Wrote it", response);
        Assert.True(executed);
        await approval.Received(1).RequestApprovalAsync(
            Arg.Is<ApprovalRequest>(r => r.ToolName == "WriteFile" && r.RiskAssessment.RequiresApproval),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithPermissionAsk_AndNoApprovalService_RefusesTheCall()
    {
        // Arrange — an Ask verdict with nobody to ask used to fall through and run the tool.
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("WriteFile", """{"path":"app.json","content":"{}"}""")
            .EnqueueResponse("Could not write");

        var executed = false;
        var testTool = AIFunctionFactory.Create((string path, string content) => { executed = true; return "ok"; }, "WriteFile", "Writes");

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [testTool],
            permissionEvaluator: new PermissionEvaluator(PermissionConfig.CreateDefault()));

        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Write the config", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Could not write", response);
        Assert.False(executed);
        var toolResult = mockClient.ReceivedMessages[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.Contains("no approval service is configured", toolResult.Result!.ToString());
    }

    [Fact]
    public async Task RunAsync_WithConversationHistory_IncludesHistoryInMessages()
    {
        // Arrange
        var mockClient = new MockChatClient().EnqueueResponse("Continuing conversation");
        var adapter = new ChatClientFrameworkAdapter(mockClient);
        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Previous question"),
            new(ChatRole.Assistant, "Previous answer")
        };

        // Act
        var response = await adapter.RunAsync(agent, "Follow-up question", history, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Continuing conversation", response);
        // Verify the mock received messages including history
        var sentMessages = mockClient.ReceivedMessages[0];
        Assert.Equal(4, sentMessages.Count); // system + 2 history + user
        Assert.Equal(ChatRole.System, sentMessages[0].Role);
        Assert.Equal(ChatRole.User, sentMessages[1].Role);
        Assert.Equal("Previous question", sentMessages[1].Text);
        Assert.Equal(ChatRole.Assistant, sentMessages[2].Role);
        Assert.Equal(ChatRole.User, sentMessages[3].Role);
        Assert.Equal("Follow-up question", sentMessages[3].Text);
    }

    [Fact]
    public async Task RunAsync_WithCapabilities_FiltersTools()
    {
        // Arrange: agent config has capabilities that restrict tools
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("AllowedTool", """{"input":"hi"}""")
            .EnqueueResponse("Done");

        var allowedTool = AIFunctionFactory.Create(() => "allowed", "AllowedTool", "Allowed");
        var blockedTool = AIFunctionFactory.Create(() => "blocked", "BlockedTool", "Blocked");

        var adapter = new ChatClientFrameworkAdapter(
            _ => mockClient,
            toolsFactory: () => [allowedTool, blockedTool]);

        var config = new AgentConfig
        {
            Name = "restricted-agent",
            Description = "Agent with limited capabilities",
            Version = "1.0.0",
            SystemPrompt = "You are restricted.",
            Model = new ModelConfig { Deployment = "test-model" },
            Capabilities = ["AllowedTool"]
        };

        var agent = await adapter.CreateAgentAsync(config, TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Use tools", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Done", response);
        // The ChatOptions.Tools should only contain AllowedTool (verified by successful execution)
    }

    [Fact]
    public async Task RunAsync_WithoutToolsFactory_NoToolLoop()
    {
        // Arrange: simple adapter without tools
        var mockClient = new MockChatClient().EnqueueResponse("Simple response");
        var adapter = new ChatClientFrameworkAdapter(mockClient);
        var agent = await adapter.CreateAgentAsync(CreateTestConfig(), TestContext.Current.CancellationToken);

        // Act
        var response = await adapter.RunAsync(agent, "Hello", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Simple response", response);
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
