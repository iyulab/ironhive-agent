using System.ComponentModel;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// CE-06: Integration tests for AgentLoop with tool retrieval and schema compression.
/// </summary>
public class AgentLoopToolIntegrationTests
{
    #region Tool Retrieval Integration

    [Fact]
    public async Task RunAsync_WithToolRetriever_CallsRetrieverWithLatestQuery()
    {
        // Arrange
        var mockClient = new MockChatClient()
            .EnqueueResponse("done");
        var tools = CreateTestTools();
        var retriever = Substitute.For<IToolRetriever>();
        retriever.RetrieveAsync(
                Arg.Any<string>(),
                Arg.Any<IList<AITool>>(),
                Arg.Any<ToolRetrievalOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ToolRetrievalResult
            {
                SelectedTools = tools,
                RelevanceScores = null
            });

        var options = new AgentOptions { Tools = tools };
        var loop = new AgentLoop(mockClient, options, toolRetriever: retriever);

        // Act
        await loop.RunAsync("read the config file");

        // Assert: retriever was called with the user query
        await retriever.Received(1).RetrieveAsync(
            "read the config file",
            tools,
            Arg.Any<ToolRetrievalOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithToolRetriever_UsesFilteredTools()
    {
        // Arrange
        var allTools = CreateTestTools();
        var selectedTool = allTools.Take(1).ToList();

        var retriever = Substitute.For<IToolRetriever>();
        retriever.RetrieveAsync(
                Arg.Any<string>(),
                Arg.Any<IList<AITool>>(),
                Arg.Any<ToolRetrievalOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ToolRetrievalResult
            {
                SelectedTools = selectedTool,
                RelevanceScores = null
            });

        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var options = new AgentOptions { Tools = allTools };
        var loop = new AgentLoop(chatClient, options, toolRetriever: retriever);

        // Act
        await loop.RunAsync("read file");

        // Assert: only filtered tools were sent to chat client
        Assert.NotNull(capturedOptions);
        Assert.Single(capturedOptions!.Tools!);
    }

    [Fact]
    public async Task RunAsync_WithToolRetriever_PassesRetrievalOptions()
    {
        // Arrange
        var tools = CreateTestTools();
        var retrievalOptions = new ToolRetrievalOptions
        {
            MaxTools = 3,
            MinRelevanceScore = 0.5f,
            AlwaysInclude = ["ReadFile"]
        };

        var retriever = Substitute.For<IToolRetriever>();
        retriever.RetrieveAsync(
                Arg.Any<string>(),
                Arg.Any<IList<AITool>>(),
                Arg.Any<ToolRetrievalOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ToolRetrievalResult
            {
                SelectedTools = tools,
                RelevanceScores = null
            });

        var mockClient = new MockChatClient().EnqueueResponse("ok");
        var options = new AgentOptions
        {
            Tools = tools,
            ToolRetrievalOptions = retrievalOptions
        };
        var loop = new AgentLoop(mockClient, options, toolRetriever: retriever);

        // Act
        await loop.RunAsync("test");

        // Assert: retrieval options were passed through
        await retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Any<IList<AITool>>(),
            retrievalOptions,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoToolRetriever_SendsAllTools()
    {
        // Arrange
        var tools = CreateTestTools();
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var options = new AgentOptions { Tools = tools };
        var loop = new AgentLoop(chatClient, options);

        // Act
        await loop.RunAsync("hello");

        // Assert: all tools sent (no retriever = no filtering)
        Assert.NotNull(capturedOptions);
        Assert.Equal(tools.Count, capturedOptions!.Tools!.Count);
    }

    [Fact]
    public async Task RunAsync_NullTools_SkipsRetrieval()
    {
        // Arrange
        var retriever = Substitute.For<IToolRetriever>();
        var mockClient = new MockChatClient().EnqueueResponse("ok");
        var options = new AgentOptions { Tools = null };
        var loop = new AgentLoop(mockClient, options, toolRetriever: retriever);

        // Act
        await loop.RunAsync("hello");

        // Assert: retriever not called when tools is null
        await retriever.DidNotReceive().RetrieveAsync(
            Arg.Any<string>(),
            Arg.Any<IList<AITool>>(),
            Arg.Any<ToolRetrievalOptions?>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Schema Compression Integration

    [Fact]
    public async Task RunAsync_WithSchemaCompression_CompressesTools()
    {
        // Arrange
        var tools = CreateTestTools();
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var options = new AgentOptions
        {
            Tools = tools,
            ToolSchemaCompression = ToolSchemaCompressionLevel.Moderate
        };
        var loop = new AgentLoop(chatClient, options);

        // Act
        await loop.RunAsync("hello");

        // Assert: tools should be wrapped in CompressedAIFunction
        Assert.NotNull(capturedOptions);
        Assert.Equal(tools.Count, capturedOptions!.Tools!.Count);
        foreach (var tool in capturedOptions.Tools)
        {
            Assert.IsType<CompressedAIFunction>(tool);
        }
    }

    [Fact]
    public async Task RunAsync_NoSchemaCompression_ToolsUnchanged()
    {
        // Arrange
        var tools = CreateTestTools();
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var options = new AgentOptions
        {
            Tools = tools,
            ToolSchemaCompression = ToolSchemaCompressionLevel.None
        };
        var loop = new AgentLoop(chatClient, options);

        // Act
        await loop.RunAsync("hello");

        // Assert: tools should be the original references
        Assert.NotNull(capturedOptions);
        Assert.Same(tools, capturedOptions!.Tools);
    }

    #endregion

    #region Combined: Retrieval + Compression

    [Fact]
    public async Task RunAsync_RetrievalThenCompression_AppliesBoth()
    {
        // Arrange
        var allTools = CreateTestTools();
        var selectedTools = allTools.Take(2).ToList();

        var retriever = Substitute.For<IToolRetriever>();
        retriever.RetrieveAsync(
                Arg.Any<string>(),
                Arg.Any<IList<AITool>>(),
                Arg.Any<ToolRetrievalOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ToolRetrievalResult
            {
                SelectedTools = selectedTools,
                RelevanceScores = null
            });

        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        var options = new AgentOptions
        {
            Tools = allTools,
            ToolSchemaCompression = ToolSchemaCompressionLevel.Aggressive
        };
        var loop = new AgentLoop(chatClient, options, toolRetriever: retriever);

        // Act
        await loop.RunAsync("read file");

        // Assert: filtered to 2 tools + compressed
        Assert.NotNull(capturedOptions);
        Assert.Equal(2, capturedOptions!.Tools!.Count);
        foreach (var tool in capturedOptions.Tools)
        {
            Assert.IsType<CompressedAIFunction>(tool);
        }
    }

    #endregion

    #region AgentOptions Defaults

    [Fact]
    public void AgentOptions_DefaultSchemaCompression_IsNone()
    {
        var options = new AgentOptions();
        Assert.Equal(ToolSchemaCompressionLevel.None, options.ToolSchemaCompression);
    }

    [Fact]
    public void AgentOptions_DefaultToolRetrievalOptions_IsNull()
    {
        var options = new AgentOptions();
        Assert.Null(options.ToolRetrievalOptions);
    }

    #endregion

    #region Helpers

    private static IList<AITool> CreateTestTools()
    {
        return
        [
            AIFunctionFactory.Create(SampleTools.ReadFile),
            AIFunctionFactory.Create(SampleTools.WriteFile),
            AIFunctionFactory.Create(SampleTools.GrepFiles),
        ];
    }

    private static class SampleTools
    {
        [Description("Read the content of a file at the specified path. Supports partial reading.")]
        public static string ReadFile(
            [Description("File path to read")] string path) => $"content of {path}";

        [Description("Write content to a file. Creates or overwrites the file.")]
        public static string WriteFile(
            [Description("File path")] string path,
            [Description("Content")] string content) => "ok";

        [Description("Search for a regex pattern in files.")]
        public static string GrepFiles(
            [Description("Pattern")] string pattern) => "matches";
    }

    #endregion
}
