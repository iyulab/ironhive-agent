using IronHive.Agent.Memory;
using MemoryIndexer.Interfaces;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Memory;

public class TextCompletionServiceAdapterTests
{
    // --- Constructor ---

    [Fact]
    public void Constructor_NullChatClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TextCompletionServiceAdapter(null!));
    }

    // --- CompleteAsync ---

    [Fact]
    public async Task CompleteAsync_ReturnsResponseText()
    {
        var chatClient = Substitute.For<IChatClient>();
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hello world")]);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var result = await adapter.CompleteAsync("Say hello");

        Assert.Equal("Hello world", result);
    }

    [Fact]
    public async Task CompleteAsync_NullText_ReturnsEmptyString()
    {
        var chatClient = Substitute.For<IChatClient>();
        // ChatResponse with no text content
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("id", "func")])]);
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var result = await adapter.CompleteAsync("test");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task CompleteAsync_PassesPromptAsUserMessage()
    {
        var chatClient = Substitute.For<IChatClient>();
        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        await adapter.CompleteAsync("my prompt");

        Assert.NotNull(capturedMessages);
        var messages = capturedMessages!.ToList();
        Assert.Single(messages);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("my prompt", messages[0].Text);
    }

    [Fact]
    public async Task CompleteAsync_NullOptions_PassesNullChatOptions()
    {
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        await adapter.CompleteAsync("test", options: null);

        Assert.Null(capturedOptions);
    }

    [Fact]
    public async Task CompleteAsync_WithOptions_MapsToChatOptions()
    {
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var options = new TextCompletionOptions
        {
            Temperature = 0.5f,
            MaxTokens = 200,
            TopP = 0.9f,
            PresencePenalty = 0.1f,
            FrequencyPenalty = 0.2f,
            StopSequences = ["stop1", "stop2"]
        };
        await adapter.CompleteAsync("test", options);

        Assert.NotNull(capturedOptions);
        Assert.Equal(0.5f, capturedOptions!.Temperature);
        Assert.Equal(200, capturedOptions.MaxOutputTokens);
        Assert.Equal(0.9f, capturedOptions.TopP);
        Assert.Equal(0.1f, capturedOptions.PresencePenalty);
        Assert.Equal(0.2f, capturedOptions.FrequencyPenalty);
        Assert.NotNull(capturedOptions.StopSequences);
        Assert.Equal(2, capturedOptions.StopSequences!.Count);
        Assert.Contains("stop1", capturedOptions.StopSequences);
        Assert.Contains("stop2", capturedOptions.StopSequences);
    }

    [Fact]
    public async Task CompleteAsync_PassesCancellationToken()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        using var cts = new CancellationTokenSource();

        await adapter.CompleteAsync("test", cancellationToken: cts.Token);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            cts.Token);
    }

    // --- CompleteBatchAsync ---

    [Fact]
    public async Task CompleteBatch_ReturnsResultsInOrder()
    {
        var chatClient = Substitute.For<IChatClient>();
        var callCount = 0;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                var text = $"response-{callCount}";
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));
            });

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var results = await adapter.CompleteBatchAsync(["prompt1", "prompt2", "prompt3"]);

        Assert.Equal(3, results.Count);
        Assert.Equal("response-1", results[0]);
        Assert.Equal("response-2", results[1]);
        Assert.Equal("response-3", results[2]);
    }

    [Fact]
    public async Task CompleteBatch_EmptyInput_ReturnsEmptyList()
    {
        var chatClient = Substitute.For<IChatClient>();

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var results = await adapter.CompleteBatchAsync([]);

        Assert.Empty(results);
        await chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteBatch_SharesOptionsAcrossAllCalls()
    {
        var chatClient = Substitute.For<IChatClient>();
        var capturedOptionsList = new List<ChatOptions?>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptionsList.Add(o)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var options = new TextCompletionOptions { Temperature = 0.3f };
        await adapter.CompleteBatchAsync(["a", "b"], options);

        Assert.Equal(2, capturedOptionsList.Count);
        // Both calls should get the same mapped options (same Temperature)
        Assert.Equal(0.3f, capturedOptionsList[0]!.Temperature);
        Assert.Equal(0.3f, capturedOptionsList[1]!.Temperature);
    }

    // --- MapOptions edge cases ---

    [Fact]
    public async Task CompleteAsync_OptionsWithNullStopSequences_MapsNullStopSequences()
    {
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? capturedOptions = null;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Do<ChatOptions?>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])));

        var adapter = new TextCompletionServiceAdapter(chatClient);
        var options = new TextCompletionOptions
        {
            Temperature = 0.7f,
            MaxTokens = 500,
            StopSequences = null
        };
        await adapter.CompleteAsync("test", options);

        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions!.StopSequences);
    }
}
