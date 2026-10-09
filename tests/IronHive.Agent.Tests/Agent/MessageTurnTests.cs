using AwesomeAssertions;
using IndexThinking.Agents;
using Ironbees.Core;
using IronHive.Agent.Context;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A turn started from a <see cref="ChatMessage"/> — a request that carries more than text — on every built-in loop.
/// </summary>
public class MessageTurnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ChatMessage LookAtThis() =>
        new(ChatRole.User, [new TextContent("Polish this screen"), new DataContent(Png, "image/png")]);

    public static TheoryData<string> Loops => new() { "agent", "thinking" };

    private static IAgentLoop Create(string kind, MockChatClient mock) => kind switch
    {
        "agent" => new AgentLoop(mock, new AgentOptions { SystemPrompt = "You are helpful." }),
        _ => new ThinkingAgentLoop(mock, PassThrough(), new AgentOptions { SystemPrompt = "You are helpful." }),
    };

    // Sends the turn's messages as they are: what reaches the model is what the loop built.
    private static IThinkingTurnManager PassThrough()
    {
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(async call =>
            {
                var context = call.Arg<ThinkingContext>();
                var send = call.Arg<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>();
                return TurnResult.Success(await send(context.Messages, context.CancellationToken), TurnMetrics.Empty, null);
            });
        return manager;
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task The_image_reaches_the_model_with_the_text_and_the_system_prompt(string kind)
    {
        var mock = new MockChatClient().EnqueueResponse("first").EnqueueResponse("second");
        var loop = Create(kind, mock);

        await loop.RunAsync("hello", Ct);
        var response = await loop.RunAsync(LookAtThis(), Ct);

        response.Content.Should().Be("second");
        var sent = mock.ReceivedMessages[^1];
        sent[0].Role.Should().Be(ChatRole.System);
        sent.Should().Contain(m => m.Text == "hello", "the earlier turn stays in the history");
        var request = sent.Last(m => m.Role == ChatRole.User);
        request.Contents.OfType<DataContent>().Should().ContainSingle().Which.MediaType.Should().Be("image/png");
        request.Text.Should().Be("Polish this screen");
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task The_streamed_turn_takes_the_message_too(string kind)
    {
        var mock = new MockChatClient().EnqueueResponse("looks fine");
        var loop = Create(kind, mock);

        var text = "";
        await foreach (var chunk in loop.RunStreamingAsync(LookAtThis(), Ct))
        {
            text += chunk.TextDelta;
        }

        text.Should().Be("looks fine");
        mock.ReceivedMessages[^1].Last(m => m.Role == ChatRole.User).Contents.OfType<DataContent>().Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task An_image_only_message_is_a_valid_request(string kind)
    {
        var mock = new MockChatClient().EnqueueResponse("a login form");
        var loop = Create(kind, mock);

        var response = await loop.RunAsync(new ChatMessage(ChatRole.User, [new DataContent(Png, "image/png")]), Ct);

        response.Content.Should().Be("a login form");
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task A_message_that_is_not_a_user_request_or_is_empty_is_rejected_before_anything_is_added(string kind)
    {
        var loop = Create(kind, new MockChatClient());
        var before = loop.History.Count;

        var assistant = () => loop.RunAsync(new ChatMessage(ChatRole.Assistant, "hi"), Ct);
        var empty = () => loop.RunAsync(new ChatMessage(ChatRole.User, (IList<AIContent>)[]), Ct);
        var streamedEmpty = () => loop.RunStreamingAsync(new ChatMessage(ChatRole.User, (IList<AIContent>)[]), Ct);

        await assistant.Should().ThrowAsync<ArgumentException>();
        await empty.Should().ThrowAsync<ArgumentException>();
        streamedEmpty.Should().Throw<ArgumentException>("the argument is checked when the call is made, not on enumeration");
        loop.History.Should().HaveCount(before);
    }

    [Fact]
    public void An_image_the_user_sent_counts_toward_the_context_budget()
    {
        var counter = ContextTokenCounter.ForGpt4o();
        var textOnly = counter.CountTokens(new ChatMessage(ChatRole.User, "Polish this screen"));

        counter.CountTokens(LookAtThis()).Should().BeGreaterThan(textOnly);
        counter.CountTokens(new ChatMessage(ChatRole.User, [new UriContent("https://example.com/a.png", "image/png")]))
            .Should().BeGreaterThan(counter.CountTokens(new ChatMessage(ChatRole.User, (IList<AIContent>)[])));
    }

    [Fact]
    public async Task The_orchestrated_loop_takes_a_text_message_and_refuses_one_that_would_lose_its_image()
    {
        var orchestrator = Substitute.For<IAgentOrchestrator>();
        orchestrator.ProcessAsync("Polish this screen", Arg.Any<CancellationToken>()).Returns("done");
        var loop = new OrchestratedAgentLoop(orchestrator);

        var response = await loop.RunAsync(new ChatMessage(ChatRole.User, "Polish this screen"), Ct);
        var withImage = () => loop.RunAsync(LookAtThis(), Ct);

        response.Content.Should().Be("done");
        await withImage.Should().ThrowAsync<NotSupportedException>().WithMessage("*DataContent*");
    }
}
