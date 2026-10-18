using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Abstractions.Messages;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="AgentOptions.Suggestions"/>: the IronHive suggestion instruction goes with every call, the model's
/// <c>&lt;suggestion&gt;</c> blocks leave the answer — streamed or not — and come back as <see cref="AgentResponse.Suggestions"/> /
/// <see cref="TurnRecord.Suggestions"/>, and the next turn's history carries the text without them.
/// </summary>
public class AgentSuggestionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Answer = "The report is ready.\n<suggestion>\nQ: What next?\nA: Send it\nA: Edit it\n</suggestion>";

    private static AgentOptions WithSuggestions() => new() { Suggestions = new SuggestionOptions { Mode = SuggestionMode.Always } };

    [Fact]
    public async Task A_turn_returns_the_suggestions_apart_from_the_text()
    {
        var mock = new MockChatClient().EnqueueResponse(Answer).EnqueueResponse("ok");
        var loop = new AgentLoop(mock, WithSuggestions());

        var response = await loop.RunAsync("write it", Ct);
        await loop.RunAsync("thanks", Ct);

        mock.ReceivedOptions[0]!.Instructions.Should().Contain("Always end your response").And.Contain("<suggestion>");
        response.Content.Should().Be("The report is ready.\n");
        var suggestion = response.Suggestions.Should().ContainSingle().Subject;
        suggestion.Question.Should().Be("What next?");
        suggestion.Items.Should().Equal("Send it", "Edit it");
        mock.ReceivedMessages[1].Should().NotContain(m => m.Text.Contains("<suggestion>"), "the history keeps the text without the block");
    }

    [Fact]
    public async Task A_streamed_turn_never_shows_the_block_and_closes_with_the_suggestions()
    {
        var mock = new MockChatClient().EnqueueResponse(Answer + " <"); // pieces of 10 characters split the tags
        var loop = new AgentLoop(mock, WithSuggestions());

        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in loop.RunStreamingAsync("write it", Ct))
        {
            chunks.Add(chunk);
        }

        string.Concat(chunks.Select(c => c.TextDelta)).Should().Be("The report is ready.\n <", "a trailing '<' is text once the answer ends");
        var turn = chunks[^1].Turn!;
        turn.Content.Should().Be("The report is ready.\n <");
        turn.Suggestions.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        loop.History.Should().NotContain(m => m.Text.Contains("<suggestion>"));
    }

    [Fact]
    public async Task Unset_adds_no_instruction_and_leaves_the_text()
    {
        var mock = new MockChatClient().EnqueueResponse(Answer);
        var loop = new AgentLoop(mock, new AgentOptions());

        var response = await loop.RunAsync("write it", Ct);

        mock.ReceivedOptions[0]!.Instructions.Should().BeNull();
        response.Content.Should().Be(Answer);
        response.Suggestions.Should().BeNull();
    }

    [Fact]
    public async Task A_per_turn_instruction_override_keeps_the_suggestion_instruction()
    {
        var mock = new MockChatClient().EnqueueResponse(Answer);
        var loop = new AgentLoop(mock, WithSuggestions());

        await loop.RunAsync("write it", new ChatOptions { Instructions = "Be brief." }, Ct);

        mock.ReceivedOptions[0]!.Instructions.Should().StartWith("Be brief.").And.Contain("<suggestion>");
    }

    [Fact]
    public async Task ThinkingAgentLoop_returns_them_too()
    {
        var mock = new MockChatClient().EnqueueResponse(Answer);
        var turnManager = Substitute.For<IThinkingTurnManager>();
        turnManager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(async call =>
            {
                var send = call.Arg<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>();
                var response = await send([new ChatMessage(ChatRole.User, "q")], Ct);
                return TurnResult.Success(response, TurnMetrics.Empty, null);
            });
        await using var loop = new ThinkingAgentLoop(mock, turnManager, WithSuggestions());

        var result = await loop.RunAsync("write it", Ct);

        result.Content.Should().Be("The report is ready.\n");
        result.Suggestions.Should().ContainSingle();
        mock.ReceivedOptions[0]!.Instructions.Should().Contain("<suggestion>");
    }
}
