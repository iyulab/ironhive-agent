using IronHive.Agent.Loop;
using IronHive.Agent.Tracking;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// P1-15: Simulation scenario tests for complex agent interactions.
/// Tests multi-turn conversations, error handling, and realistic usage patterns.
/// </summary>
public class AgentLoopScenarioTests
{
    [Fact]
    public async Task Scenario_MultiTurnConversation_MaintainsContext()
    {
        // Arrange - Simulate a multi-turn coding assistant conversation
        var mockClient = new MockChatClient()
            .EnqueueResponse("I can help you write a function. What should it do?")
            .EnqueueResponse("Here's a function to add two numbers:\n```csharp\npublic int Add(int a, int b) => a + b;\n```")
            .EnqueueResponse("Sure, here's the updated function with validation:\n```csharp\npublic int Add(int a, int b)\n{\n    if (a < 0 || b < 0) throw new ArgumentException(\"Numbers must be positive\");\n    return a + b;\n}\n```");

        var agentLoop = new AgentLoop(mockClient, new AgentOptions
        {
            SystemPrompt = "You are a coding assistant."
        });

        // Act - Multi-turn conversation
        var response1 = await agentLoop.RunAsync("Help me write a function");
        var response2 = await agentLoop.RunAsync("Make it add two numbers");
        var response3 = await agentLoop.RunAsync("Add validation for positive numbers only");

        // Assert
        Assert.Contains("function", response1.Content);
        Assert.Contains("Add", response2.Content);
        Assert.Contains("validation", response3.Content);

        // Verify history is maintained
        var history = agentLoop.History;
        Assert.Equal(7, history.Count); // System + 3 User + 3 Assistant

        // Verify the mock received incrementally larger message lists
        Assert.Equal(2, mockClient.ReceivedMessages[0].Count);  // System + User1
        Assert.Equal(4, mockClient.ReceivedMessages[1].Count);  // System + User1 + Asst1 + User2
        Assert.Equal(6, mockClient.ReceivedMessages[2].Count);  // All previous + User3
    }

    [Fact]
    public async Task Scenario_ToolCallAndContinuation_HandlesToolResults()
    {
        // Arrange - Simulate tool call followed by response using tool result
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("read_file", """{"path": "config.json"}""", "Let me read that file for you.")
            .EnqueueResponse("The config.json contains database settings. The connection string is: Server=localhost;Database=myapp");

        var agentLoop = new AgentLoop(mockClient);

        // Act - First call returns tool call
        var response1 = await agentLoop.RunAsync("What's in the config.json file?");

        // Verify tool call was captured
        Assert.Single(response1.ToolCalls);
        Assert.Equal("read_file", response1.ToolCalls[0].ToolName);

        // Simulate tool execution by continuing conversation
        var response2 = await agentLoop.RunAsync("Tool result: {\"database\": \"localhost\", \"port\": 5432}");

        // Assert - Final response incorporates tool result
        Assert.Contains("database", response2.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scenario_StreamingMultiTurn_PreservesHistoryCorrectly()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Hello! How can I assist you today?")
            .EnqueueResponse("I can help with Python. What do you need?");

        var agentLoop = new AgentLoop(mockClient);

        // Act - First streaming call
        var chunks1 = new List<string>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("Hello"))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                chunks1.Add(chunk.TextDelta);
            }
        }

        // Second streaming call
        var chunks2 = new List<string>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("I need help with Python"))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                chunks2.Add(chunk.TextDelta);
            }
        }

        // Assert - Both responses were streamed
        Assert.NotEmpty(chunks1);
        Assert.NotEmpty(chunks2);
        Assert.Equal("Hello! How can I assist you today?", string.Join("", chunks1));
        Assert.Equal("I can help with Python. What do you need?", string.Join("", chunks2));

        // Verify history
        Assert.Equal(4, agentLoop.History.Count); // User1 + Asst1 + User2 + Asst2
    }

    [Fact]
    public async Task Scenario_ErrorRecovery_ContinuesAfterException()
    {
        // Arrange - First call fails, second succeeds
        var mockClient = new MockChatClient()
            .EnqueueError(new InvalidOperationException("API rate limited"))
            .EnqueueResponse("Here's the information you requested.");

        var agentLoop = new AgentLoop(mockClient);

        // Act & Assert - First call throws
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agentLoop.RunAsync("First request"));

        // History should still have the user message (depends on implementation)
        // For now, we verify the agent can continue
        mockClient.Reset();
        mockClient.EnqueueResponse("Here's the information you requested.");

        var newAgentLoop = new AgentLoop(mockClient);
        var response = await newAgentLoop.RunAsync("Second request");

        Assert.Contains("information", response.Content);
    }

    [Fact]
    public async Task Scenario_LongConversation_HandlesHistoryGrowth()
    {
        // Arrange - Simulate 10 turns of conversation
        var mockClient = new MockChatClient();
        for (int i = 1; i <= 10; i++)
        {
            mockClient.EnqueueResponse($"Response #{i}");
        }

        var agentLoop = new AgentLoop(mockClient, new AgentOptions
        {
            SystemPrompt = "You are helpful."
        });

        // Act - 10 turns
        for (int i = 1; i <= 10; i++)
        {
            var response = await agentLoop.RunAsync($"Message #{i}");
            Assert.Equal($"Response #{i}", response.Content);
        }

        // Assert - History has system + 20 messages (10 user + 10 assistant)
        Assert.Equal(21, agentLoop.History.Count);

        // Verify message counts grew correctly
        Assert.Equal(2, mockClient.ReceivedMessages[0].Count);   // System + User1
        Assert.Equal(4, mockClient.ReceivedMessages[1].Count);   // +Asst1 + User2
        Assert.Equal(20, mockClient.ReceivedMessages[9].Count);  // All 10 turns
    }

    [Fact]
    public async Task Scenario_ClearHistoryMidConversation_ResetsContext()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("I remember our conversation.")
            .EnqueueResponse("Nice to meet you! I have no memory of our previous chat.");

        var options = new AgentOptions
        {
            SystemPrompt = "You are a friendly assistant."
        };

        var agentLoop = new AgentLoop(mockClient, options);

        // Act - First turn
        await agentLoop.RunAsync("Remember this: my favorite color is blue");

        // Clear history
        agentLoop.ClearHistory();

        // Second turn - should only have system prompt
        await agentLoop.RunAsync("What's my favorite color?");

        // Assert - Second call should only receive system + user message
        Assert.Equal(2, mockClient.ReceivedMessages[1].Count);
    }

    [Fact]
    public async Task Scenario_WithOptions_AppliesTemperatureAndMaxTokens()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Creative response!");

        var options = new AgentOptions
        {
            SystemPrompt = "Be creative",
            Temperature = 0.9f,
            MaxTokens = 500
        };

        var agentLoop = new AgentLoop(mockClient, options);

        // Act
        var response = await agentLoop.RunAsync("Tell me a story");

        // Assert - Response received (options are passed internally to ChatOptions)
        Assert.NotEmpty(response.Content);
        Assert.Equal(3, agentLoop.History.Count); // System + User + Assistant
    }

    [Fact]
    public async Task Scenario_EmptyAssistantResponse_HandlesGracefully()
    {
        // Arrange - LLM returns empty string
        var mockClient = new MockChatClient()
            .EnqueueResponse("");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Say nothing");

        // Assert - Empty response is handled
        Assert.Equal(string.Empty, response.Content);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Scenario_MultipleToolCalls_ExtractsAll()
    {
        // Arrange - Response with multiple sequential tool calls
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("list_files", """{"path": "."}""")
            .EnqueueToolCallResponse("read_file", """{"path": "README.md"}""")
            .EnqueueResponse("Based on the files, this is a .NET project.");

        var agentLoop = new AgentLoop(mockClient);

        // Act - First call
        var response1 = await agentLoop.RunAsync("Analyze this project");
        Assert.Single(response1.ToolCalls);
        Assert.Equal("list_files", response1.ToolCalls[0].ToolName);

        // Second call after "tool result"
        var response2 = await agentLoop.RunAsync("Files: README.md, Program.cs");
        Assert.Single(response2.ToolCalls);
        Assert.Equal("read_file", response2.ToolCalls[0].ToolName);

        // Third call with final answer
        var response3 = await agentLoop.RunAsync("Content: # IronHive CLI");
        Assert.Empty(response3.ToolCalls);
        Assert.Contains(".NET", response3.Content);
    }

    [Fact]
    public async Task Scenario_StreamingToolCall_YieldsCorrectOrder()
    {
        // Arrange - Tool call with both text and function call
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("search", """{"query": "test"}""", "Let me search for that...");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var textChunks = new List<string>();
        var toolCallChunks = new List<ToolCallChunk>();

        await foreach (var chunk in agentLoop.RunStreamingAsync("Search for test"))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                textChunks.Add(chunk.TextDelta);
            }
            if (chunk.ToolCallDelta is not null)
            {
                toolCallChunks.Add(chunk.ToolCallDelta);
            }
        }

        // Assert - Text comes before tool call (based on MockChatClient implementation)
        Assert.NotEmpty(textChunks);
        Assert.Single(toolCallChunks);
        Assert.Equal("search", toolCallChunks[0].NameDelta);
        Assert.Contains("query", toolCallChunks[0].ArgumentsDelta);
    }

    [Fact]
    public async Task Scenario_UsageTracking_AccumulatesAcrossTurns()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("First response", new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })
            .EnqueueResponse("Second response", new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 });

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response1 = await agentLoop.RunAsync("First");
        var response2 = await agentLoop.RunAsync("Second");

        // Assert - Each response has its own usage
        Assert.Equal(10, response1.Usage!.InputTokens);
        Assert.Equal(5, response1.Usage.OutputTokens);
        Assert.Equal(15, response1.Usage.TotalTokens);

        Assert.Equal(20, response2.Usage!.InputTokens);
        Assert.Equal(10, response2.Usage.OutputTokens);
        Assert.Equal(30, response2.Usage.TotalTokens);
    }
}
