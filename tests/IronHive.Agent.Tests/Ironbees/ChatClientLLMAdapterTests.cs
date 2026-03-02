using FluentAssertions;
using Ironbees.Core;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

public class ChatClientLLMAdapterTests
{
    private readonly IChatClientFactory _clientFactory;
    private readonly IChatClient _chatClient;
    private readonly ChatClientLLMAdapter _adapter;

    public ChatClientLLMAdapterTests()
    {
        _clientFactory = Substitute.For<IChatClientFactory>();
        _chatClient = Substitute.For<IChatClient>();
        _adapter = new ChatClientLLMAdapter(
            _clientFactory, Substitute.For<ILogger<ChatClientLLMAdapter>>());
    }

    #region Constructor

    [Fact]
    public void Constructor_NullClientFactory_ThrowsArgumentNull()
    {
        var act = () => new ChatClientLLMAdapter(
            null!, Substitute.For<ILogger<ChatClientLLMAdapter>>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        var factory = Substitute.For<IChatClientFactory>();
        var act = () => new ChatClientLLMAdapter(factory, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region CreateAgentAsync

    [Fact]
    public async Task CreateAgentAsync_ValidConfig_ReturnsAgent()
    {
        var config = CreateConfig("test-agent", "Test agent description");

        var agent = await _adapter.CreateAgentAsync(config);

        agent.Name.Should().Be("test-agent");
        agent.Description.Should().Be("Test agent description");
        agent.Config.Should().BeSameAs(config);
    }

    [Fact]
    public async Task CreateAgentAsync_NullConfig_ThrowsArgumentNull()
    {
        var act = () => _adapter.CreateAgentAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAgentAsync_ReturnsSimpleAgent()
    {
        var config = CreateConfig("my-agent", "My description");

        var agent = await _adapter.CreateAgentAsync(config);

        agent.Should().BeOfType<ChatClientLLMAdapter.SimpleAgent>();
    }

    #endregion

    #region RunAsync

    [Fact]
    public async Task RunAsync_ValidInput_ReturnsResponse()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupChatResponse("Analysis complete.");

        var result = await _adapter.RunAsync(agent, "analyze this code");

        result.Should().Be("Analysis complete.");
    }

    [Fact]
    public async Task RunAsync_NullAgent_ThrowsArgumentNull()
    {
        var act = () => _adapter.RunAsync(null!, "task");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_EmptyInput_ThrowsArgumentException()
    {
        var agent = await CreateAgentAsync("test");
        var act = () => _adapter.RunAsync(agent, "");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_WhitespaceInput_ThrowsArgumentException()
    {
        var agent = await CreateAgentAsync("test");
        var act = () => _adapter.RunAsync(agent, "   ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunAsync_WithHistory_IncludesHistoryInMessages()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupChatResponse("Follow-up result.");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "first question"),
            new(ChatRole.Assistant, "first answer"),
        };

        var result = await _adapter.RunAsync(agent, "follow-up", history);

        result.Should().Be("Follow-up result.");
        // Verify chat client was called with messages including history
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Count == 4), // system + 2 history + user
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithNullHistory_OmitsHistory()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupChatResponse("Direct result.");

        var result = await _adapter.RunAsync(agent, "direct question", (IReadOnlyList<ChatMessage>?)null);

        result.Should().Be("Direct result.");
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Count == 2), // system + user
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NullResponseText_ReturnsEmpty()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupChatResponse(null);

        var result = await _adapter.RunAsync(agent, "test input");

        result.Should().BeEmpty();
    }

    #endregion

    #region StreamAsync

    [Fact]
    public async Task StreamAsync_ValidInput_StreamsChunks()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupStreamingResponse("Hello", " world");

        var chunks = new List<string>();
        await foreach (var chunk in _adapter.StreamAsync(agent, "test"))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task StreamAsync_WithHistory_StreamsChunks()
    {
        var agent = await CreateAgentAsync("code-analyst");
        SetupClientFactory("openai");
        SetupStreamingResponse("Reply", " here");

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "previous"),
            new(ChatRole.Assistant, "response"),
        };

        var chunks = new List<string>();
        await foreach (var chunk in _adapter.StreamAsync(agent, "follow-up", history))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Equal("Reply", " here");
    }

    [Fact]
    public async Task StreamAsync_NullAgent_ThrowsArgumentNull()
    {
        var act = async () =>
        {
            await foreach (var _ in _adapter.StreamAsync(null!, "task")) { }
        };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StreamAsync_EmptyInput_ThrowsArgumentException()
    {
        var agent = await CreateAgentAsync("test");
        var act = async () =>
        {
            await foreach (var _ in _adapter.StreamAsync(agent, "")) { }
        };
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region NormalizeProviderName

    [Theory]
    [InlineData("azure-openai", "openai")]
    [InlineData("azureopenai", "openai")]
    [InlineData("gpt", "openai")]
    [InlineData("claude", "anthropic")]
    [InlineData("gemini", "google")]
    [InlineData("openai", "openai")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("ollama", "ollama")]
    [InlineData("Azure-OpenAI", "openai")]
    [InlineData("GPT", "openai")]
    public void NormalizeProviderName_MapsCorrectly(string input, string expected)
    {
        ChatClientLLMAdapter.NormalizeProviderName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("azure-openai", "openai")]
    [InlineData("gpt", "openai")]
    [InlineData("claude", "anthropic")]
    [InlineData("gemini", "google")]
    [InlineData("openai", "openai")]
    public async Task RunAsync_NormalizesProviderName(string inputProvider, string expectedProvider)
    {
        var config = CreateConfig("test", "test", inputProvider);
        var agent = await _adapter.CreateAgentAsync(config);
        _clientFactory.CreateAsync(expectedProvider, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_chatClient);
        SetupChatResponse("ok");

        await _adapter.RunAsync(agent, "test input");

        await _clientFactory.Received(1).CreateAsync(
            expectedProvider, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region BuildMessages

    [Fact]
    public void BuildMessages_SystemAndUser_ReturnsCorrectOrder()
    {
        var messages = ChatClientLLMAdapter.BuildMessages("You are a bot.", "Hello");

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Be("You are a bot.");
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("Hello");
    }

    [Fact]
    public void BuildMessages_WithHistory_InsertsHistoryBetweenSystemAndUser()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "prev question"),
            new(ChatRole.Assistant, "prev answer"),
        };

        var messages = ChatClientLLMAdapter.BuildMessages("system", "current", history);

        messages.Should().HaveCount(4);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("prev question");
        messages[2].Role.Should().Be(ChatRole.Assistant);
        messages[3].Role.Should().Be(ChatRole.User);
        messages[3].Text.Should().Be("current");
    }

    [Fact]
    public void BuildMessages_NullHistory_IgnoresHistory()
    {
        var messages = ChatClientLLMAdapter.BuildMessages("system", "input", null);

        messages.Should().HaveCount(2);
    }

    #endregion

    #region BuildChatOptions

    [Fact]
    public void BuildChatOptions_MapsAllProperties()
    {
        var model = new ModelConfig
        {
            Deployment = "gpt-4o",
            Temperature = 0.5,
            MaxTokens = 2000,
            TopP = 0.9,
            FrequencyPenalty = 0.1,
            PresencePenalty = 0.2
        };

        var options = ChatClientLLMAdapter.BuildChatOptions(model);

        options.Temperature.Should().Be(0.5f);
        options.MaxOutputTokens.Should().Be(2000);
        options.TopP.Should().Be(0.9f);
        options.FrequencyPenalty.Should().Be(0.1f);
        options.PresencePenalty.Should().Be(0.2f);
    }

    [Fact]
    public void BuildChatOptions_NullOptionalParams_MapsToNull()
    {
        var model = new ModelConfig
        {
            Deployment = "gpt-4o",
            Temperature = 0.7,
            MaxTokens = 4000,
            TopP = null,
            FrequencyPenalty = null,
            PresencePenalty = null
        };

        var options = ChatClientLLMAdapter.BuildChatOptions(model);

        options.TopP.Should().BeNull();
        options.FrequencyPenalty.Should().BeNull();
        options.PresencePenalty.Should().BeNull();
    }

    #endregion

    #region Helpers

    private static AgentConfig CreateConfig(
        string name, string description, string provider = "openai") => new()
    {
        Name = name,
        Description = description,
        Version = "1.0.0",
        SystemPrompt = $"You are {name}.",
        Model = new ModelConfig { Provider = provider, Deployment = "gpt-4o" }
    };

    private async Task<IAgent> CreateAgentAsync(string name)
        => await _adapter.CreateAgentAsync(CreateConfig(name, $"{name} agent"));

    private void SetupClientFactory(string expectedProvider)
    {
        _clientFactory.CreateAsync(expectedProvider, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_chatClient);
    }

    private void SetupChatResponse(string? text)
    {
        var responseMessage = new ChatMessage(ChatRole.Assistant, text);
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(responseMessage));
    }

    private void SetupStreamingResponse(params string[] chunks)
    {
        _chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunks));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    #endregion
}
