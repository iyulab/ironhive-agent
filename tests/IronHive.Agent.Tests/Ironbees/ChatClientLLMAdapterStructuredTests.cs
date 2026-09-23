using AwesomeAssertions;
using Ironbees.Core;
using Ironbees.Core.Streaming;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace IronHive.Agent.Tests.Ironbees;

/// <summary>
/// The structured run surface carries what the chat response reports. Before, both members fell through to the
/// interface defaults: <c>RunStructuredAsync</c> returned the text alone (Usage null) and <c>StreamStructuredAsync</c>
/// forwarded text only — no usage, no reasoning, no finish reason.
/// </summary>
public class ChatClientLLMAdapterStructuredTests
{
    private readonly IChatClientFactory _factory = Substitute.For<IChatClientFactory>();
    private readonly IChatClient _client = Substitute.For<IChatClient>();
    private readonly ILLMFrameworkAdapter _adapter;

    public ChatClientLLMAdapterStructuredTests()
    {
        _factory.CreateAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_client);
        _adapter = new ChatClientLLMAdapter(_factory, Substitute.For<ILogger<ChatClientLLMAdapter>>());
    }

    private Task<IAgent> Agent() => _adapter.CreateAgentAsync(new AgentConfig
    {
        Name = "a", Description = "a", Version = "1.0.0", SystemPrompt = "s",
        Model = new ModelConfig { Provider = "openai", Deployment = "gpt-4o" },
    });

    [Fact]
    public async Task RunStructured_carries_the_response_usage()
    {
        var usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 5, TotalTokenCount = 17 };
        _client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")) { Usage = usage });

        var result = await _adapter.RunStructuredAsync(await Agent(), "q", cancellationToken: TestContext.Current.CancellationToken);

        result.Text.Should().Be("hi");
        result.Usage!.TotalTokenCount.Should().Be(17);
    }

    [Fact]
    public async Task StreamStructured_emits_reasoning_text_usage_and_the_finish_reason()
    {
        _client.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Updates());

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in _adapter.StreamStructuredAsync(await Agent(), "q", cancellationToken: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        chunks.OfType<ThinkingChunk>().Single().Content.Should().Be("pondering");
        string.Concat(chunks.OfType<TextChunk>().Select(c => c.Content)).Should().Be("hello");
        chunks.OfType<UsageChunk>().Single().TotalTokens.Should().Be(9);
        chunks.Last().Should().BeOfType<CompletionChunk>().Which.FinishReason.Should().Be("length");
    }

    [Fact]
    public async Task Unsupported_options_are_refused_as_before()
    {
        var act = async () => await _adapter.RunStructuredAsync(await Agent(), "q", options: new AgentRunOptions { MaxToolTurns = 2 },
            cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("pondering")]);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "hel");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "lo");
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 4, OutputTokenCount = 5, TotalTokenCount = 9 })])
        {
            FinishReason = ChatFinishReason.Length,
        };
        await Task.CompletedTask;
    }
}
