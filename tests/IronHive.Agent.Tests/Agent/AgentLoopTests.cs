using System.Text.Json;
using IronHive.Agent.Loop;
using IronHive.Agent.Tracking;
using IronHive.Agent.Context;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

public class AgentLoopTests
{
    [Fact]
    public async Task RunAsync_WithSimplePrompt_ReturnsResponse()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Hello! I'm here to help.");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Hello", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Hello! I'm here to help.", response.Content);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task RunAsync_WithSystemPrompt_IncludesSystemMessageInHistory()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("I'm a helpful assistant.");

        var options = new AgentOptions
        {
            SystemPrompt = "You are a helpful assistant."
        };

        var agentLoop = new AgentLoop(mockClient, options);

        // Act
        await agentLoop.RunAsync("Who are you?", TestContext.Current.CancellationToken);

        // Assert
        var history = agentLoop.History;
        Assert.Equal(3, history.Count); // System + User + Assistant
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal("You are a helpful assistant.", history[0].Text);
    }

    [Fact]
    public async Task RunAsync_MaintainsConversationHistory()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Hello!")
            .EnqueueResponse("Nice to meet you too!");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        await agentLoop.RunAsync("Hello", TestContext.Current.CancellationToken);
        await agentLoop.RunAsync("Nice to meet you", TestContext.Current.CancellationToken);

        // Assert
        var history = agentLoop.History;
        Assert.Equal(4, history.Count); // User1 + Assistant1 + User2 + Assistant2

        // Verify the mock received the full history on second call
        Assert.Equal(2, mockClient.ReceivedMessages.Count);
        Assert.Single(mockClient.ReceivedMessages[0]); // First call: just "Hello"
        Assert.Equal(3, mockClient.ReceivedMessages[1].Count); // Second call: includes previous exchange
    }

    [Fact]
    public async Task RunAsync_WithUsage_ReturnsTokenUsage()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Test response", new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5
            });

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Test", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(5, response.Usage.OutputTokens);
        Assert.Equal(15, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task ClearHistory_RemovesAllMessages()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Response 1");

        var options = new AgentOptions
        {
            SystemPrompt = "System prompt"
        };

        var agentLoop = new AgentLoop(mockClient, options);

        // Act - Add a message then clear
        await agentLoop.RunAsync("Hello", TestContext.Current.CancellationToken);
        agentLoop.ClearHistory();

        // Assert - Only system prompt remains
        Assert.Single(agentLoop.History);
        Assert.Equal(ChatRole.System, agentLoop.History[0].Role);
    }

    [Fact]
    public async Task RunAsync_WithEmptyPrompt_ThrowsArgumentException()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var agentLoop = new AgentLoop(mockClient);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => agentLoop.RunAsync("", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => agentLoop.RunAsync("   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Should not see this");

        var agentLoop = new AgentLoop(mockClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => agentLoop.RunAsync("Test", cts.Token));
    }

    [Fact]
    public async Task RunAsync_WithToolCall_ExtractsToolCallInfo()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("read_file", """{"path": "test.txt"}""", "Reading file...");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Read the test.txt file", TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(response.ToolCalls);
        Assert.Equal("read_file", response.ToolCalls[0].ToolName);
        Assert.Contains("test.txt", response.ToolCalls[0].Arguments);
    }

    [Fact]
    public async Task RunAsync_WithMultipleToolCalls_ExtractsAllToolCalls()
    {
        // Arrange - Create a response with multiple tool calls
        var mockClient = new MockChatClient();

        // First enqueue a tool call, then a final response
        mockClient.EnqueueToolCallResponse("list_directory", """{"path": "."}""");
        mockClient.EnqueueToolCallResponse("read_file", """{"path": "README.md"}""");

        var agentLoop = new AgentLoop(mockClient);

        // Act - First call returns tool call
        var response1 = await agentLoop.RunAsync("List directory and read README", TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(response1.ToolCalls);
        Assert.Equal("list_directory", response1.ToolCalls[0].ToolName);
    }

    [Fact]
    public async Task RunAsync_WithUnresolvedToolCall_ReportsUnknownSuccess()
    {
        // Arrange - no function-invocation middleware wraps the client, so the loop never learns
        // whether the call actually ran.
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("read_file", """{"path": "test.txt"}""");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Read the test.txt file", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(response.ToolCalls[0].Success);
    }

    [Fact]
    public async Task RunAsync_WithMiddlewareResolvedToolCall_ReportsActualResultAndSuccess()
    {
        // Arrange - simulates a chatClient wrapped with UseFunctionInvocation(): the returned
        // ChatResponse already contains a FunctionResultContent matching the call's CallId.
        var mockClient = new MockChatClient()
            .EnqueueResolvedToolCallResponse("read_file", """{"path": "test.txt"}""", "file contents");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Read the test.txt file", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.ToolCalls[0].Success);
        Assert.Equal("file contents", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task RunAsync_WithMiddlewareResolvedToolCallException_ReportsFailure()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResolvedToolCallResponse(
                "read_file", """{"path": "missing.txt"}""", "file not found",
                resultException: new FileNotFoundException("file not found"));

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Read the missing.txt file", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.ToolCalls[0].Success);
    }

    [Fact]
    public async Task RunAsync_WithMalformedToolCallArguments_ReportsFailure()
    {
        // Arrange - the provider produced a call whose arguments Microsoft.Extensions.AI could not
        // parse; it surfaces this as FunctionCallContent.Exception rather than a valid call.
        var mockClient = new MockChatClient()
            .EnqueueMalformedToolCallResponse("read_file", new JsonException("invalid arguments"));

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var response = await agentLoop.RunAsync("Read a file", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.ToolCalls[0].Success);
        Assert.Equal("invalid arguments", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task RunStreamingAsync_YieldsTextChunks()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Hello, how can I help you today?");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("Hello", TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.NotEmpty(chunks);
        var textChunks = chunks.Where(c => c.TextDelta != null).ToList();
        Assert.NotEmpty(textChunks);

        // Verify all text is captured
        var fullText = string.Join("", textChunks.Select(c => c.TextDelta));
        Assert.Equal("Hello, how can I help you today?", fullText);
    }

    [Fact]
    public async Task RunStreamingAsync_WithToolCall_YieldsToolCallChunk()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("write_file", """{"path": "output.txt", "content": "Hello"}""");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("Write hello to output.txt", TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        // Assert
        var toolCallChunks = chunks.Where(c => c.ToolCallDelta != null).ToList();
        Assert.NotEmpty(toolCallChunks);
        Assert.Equal("write_file", toolCallChunks[0].ToolCallDelta!.NameDelta);
    }

    [Fact]
    public async Task RunStreamingAsync_UpdatesHistory()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Streaming response");

        var agentLoop = new AgentLoop(mockClient);

        // Act
        await foreach (var _ in agentLoop.RunStreamingAsync("Test prompt", TestContext.Current.CancellationToken))
        {
            // Consume all chunks
        }

        // Assert - History should be updated after streaming completes
        Assert.Equal(2, agentLoop.History.Count); // User + Assistant
        Assert.Equal(ChatRole.User, agentLoop.History[0].Role);
        Assert.Equal("Test prompt", agentLoop.History[0].Text);
        Assert.Equal(ChatRole.Assistant, agentLoop.History[1].Role);
    }

    [Fact]
    public async Task RunStreamingAsync_WithCancellation_StopsStreaming()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("This is a long response that should be cancelled");

        var agentLoop = new AgentLoop(mockClient);
        using var cts = new CancellationTokenSource();

        // Act
        var chunks = new List<AgentResponseChunk>();
        var enumerator = agentLoop.RunStreamingAsync("Test", cts.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Get first chunk then cancel
        if (await enumerator.MoveNextAsync())
        {
            chunks.Add(enumerator.Current);
            cts.Cancel();
        }

        // Try to get more - should throw (TaskCanceledException is a subclass of OperationCanceledException)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task RunAsync_WithOptions_UsesProvidedSettings()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Response");

        var options = new AgentOptions
        {
            Temperature = 0.5f,
            MaxTokens = 1000,
            SystemPrompt = "Be concise"
        };

        var agentLoop = new AgentLoop(mockClient, options);

        // Act
        await agentLoop.RunAsync("Test", TestContext.Current.CancellationToken);

        // Assert - Verify system prompt was included
        var history = agentLoop.History;
        Assert.Equal(3, history.Count);
        Assert.Equal("Be concise", history[0].Text);
    }

    [Fact]
    public void InitializeHistory_RestoresMessages()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var options = new AgentOptions { SystemPrompt = "You are helpful." };
        var agentLoop = new AgentLoop(mockClient, options);

        var restoredMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!"),
            new(ChatRole.User, "How are you?")
        };

        // Act
        agentLoop.InitializeHistory(restoredMessages);

        // Assert
        var history = agentLoop.History;
        Assert.Equal(4, history.Count); // System + 3 restored messages
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal("You are helpful.", history[0].Text);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal("Hello", history[1].Text);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
        Assert.Equal("Hi there!", history[2].Text);
        Assert.Equal(ChatRole.User, history[3].Role);
        Assert.Equal("How are you?", history[3].Text);
    }

    [Fact]
    public void InitializeHistory_SkipsSystemMessagesFromRestored()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var options = new AgentOptions { SystemPrompt = "Original system prompt" };
        var agentLoop = new AgentLoop(mockClient, options);

        var restoredMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "Old system prompt"), // Should be skipped
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        // Act
        agentLoop.InitializeHistory(restoredMessages);

        // Assert
        var history = agentLoop.History;
        Assert.Equal(3, history.Count); // Original system + User + Assistant
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal("Original system prompt", history[0].Text); // Original preserved
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
    }

    [Fact]
    public void InitializeHistory_WithoutSystemPrompt_AddsMessagesDirectly()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var agentLoop = new AgentLoop(mockClient); // No system prompt

        var restoredMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi!")
        };

        // Act
        agentLoop.InitializeHistory(restoredMessages);

        // Assert
        var history = agentLoop.History;
        Assert.Equal(2, history.Count);
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal(ChatRole.Assistant, history[1].Role);
    }

    [Fact]
    public async Task RunAsync_AfterInitializeHistory_ContinuesConversation()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("I'm doing great, thanks for asking!");

        var options = new AgentOptions { SystemPrompt = "Be friendly." };
        var agentLoop = new AgentLoop(mockClient, options);

        var restoredMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!")
        };
        agentLoop.InitializeHistory(restoredMessages);

        // Act
        var response = await agentLoop.RunAsync("How are you?", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("I'm doing great, thanks for asking!", response.Content);
        var history = agentLoop.History;
        Assert.Equal(5, history.Count); // System + 2 restored + new User + new Assistant
    }

    [Fact]
    public async Task RunAsync_WithContextManager_PreparesHistory()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("I'll help with your code.");

        var tokenCounter = new ContextTokenCounter("gpt-4", 8000);
        var contextManager = new ContextManager(tokenCounter);

        var agentLoop = new AgentLoop(mockClient, contextManager: contextManager);

        // Act
        var response = await agentLoop.RunAsync("Help me write code", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(agentLoop.ContextManager);
        Assert.Equal("I'll help with your code.", response.Content);
    }

    [Fact]
    public void GetContextUsage_WithContextManager_ReturnsUsage()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var tokenCounter = new ContextTokenCounter("gpt-4", 8000);
        var contextManager = new ContextManager(tokenCounter);

        var options = new AgentOptions { SystemPrompt = "You are helpful." };
        var agentLoop = new AgentLoop(mockClient, options, contextManager: contextManager);

        // Act
        var usage = agentLoop.GetContextUsage();

        // Assert
        Assert.NotNull(usage);
        Assert.True(usage.CurrentTokens > 0); // System prompt has tokens
        Assert.Equal(8000, usage.MaxTokens);
        Assert.False(usage.NeedsCompaction);
    }

    [Fact]
    public void GetContextUsage_WithoutContextManager_ReturnsNull()
    {
        // Arrange
        var mockClient = new MockChatClient();
        var agentLoop = new AgentLoop(mockClient);

        // Act
        var usage = agentLoop.GetContextUsage();

        // Assert
        Assert.Null(usage);
    }

    [Fact]
    public async Task RunAsync_WithContextManager_SetsGoalFromFirstMessage()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Got it, I'll help optimize performance.");

        var tokenCounter = new ContextTokenCounter("gpt-4", 8000);
        var goalOptions = new GoalReminderOptions { Enabled = true };
        var contextManager = new ContextManager(tokenCounter, goalReminderOptions: goalOptions);

        var agentLoop = new AgentLoop(mockClient, contextManager: contextManager);

        // Act
        await agentLoop.RunAsync("Optimize the database queries for better performance", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Optimize the database queries for better performance", contextManager.GoalReminder.CurrentGoal);
    }

    [Fact]
    public async Task RunStreamingAsync_WithContextManager_PreparesHistory()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("Streaming response with context management");

        var tokenCounter = new ContextTokenCounter("gpt-4", 8000);
        var contextManager = new ContextManager(tokenCounter);

        var agentLoop = new AgentLoop(mockClient, contextManager: contextManager);

        // Act
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("Test streaming", TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.NotNull(agentLoop.ContextManager);
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task RunStreamingAsync_SecondCallWithOverrideOptions_OnlySecondCallCarriesOverride()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("first")
            .EnqueueResponse("second");

        var agentLoop = new AgentLoop(mockClient, new AgentOptions { Temperature = 0.7f });

        var overrideOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["enable_thinking"] = false }
        };

        // Act
        await foreach (var _ in agentLoop.RunStreamingAsync("first prompt", TestContext.Current.CancellationToken)) { }
        await foreach (var _ in agentLoop.RunStreamingAsync("second prompt", overrideOptions, TestContext.Current.CancellationToken)) { }

        // Assert
        Assert.Equal(2, mockClient.ReceivedOptions.Count);
        Assert.Null(mockClient.ReceivedOptions[0]!.AdditionalProperties);
        Assert.Equal(0.7f, mockClient.ReceivedOptions[0]!.Temperature);

        Assert.NotNull(mockClient.ReceivedOptions[1]!.AdditionalProperties);
        Assert.False((bool)mockClient.ReceivedOptions[1]!.AdditionalProperties!["enable_thinking"]!);
        Assert.Equal(0.7f, mockClient.ReceivedOptions[1]!.Temperature); // inherited, not overridden
    }

    [Fact]
    public async Task RunAsync_WithOverrideTemperature_ReplacesConfiguredDefault()
    {
        // Arrange
        var mockClient = new MockChatClient().EnqueueResponse("ok");
        var agentLoop = new AgentLoop(mockClient, new AgentOptions { Temperature = 0.7f });

        // Act
        await agentLoop.RunAsync("prompt", new ChatOptions { Temperature = 0.1f }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0.1f, mockClient.ReceivedOptions[0]!.Temperature);
    }

    [Fact]
    public async Task RunAsync_TwoArgOverload_StillCompilesAndBehavesUnchanged()
    {
        // Arrange — existing positional-CancellationToken call site must keep compiling and working
        var mockClient = new MockChatClient().EnqueueResponse("ok");
        var agentLoop = new AgentLoop(mockClient);
        using var cts = new CancellationTokenSource();

        // Act
        var response = await agentLoop.RunAsync("prompt", cts.Token);

        // Assert
        Assert.Equal("ok", response.Content);
        Assert.Null(mockClient.ReceivedOptions[0]!.AdditionalProperties);
    }
}
