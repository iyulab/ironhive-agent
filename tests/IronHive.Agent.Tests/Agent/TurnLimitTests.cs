using System.Text.Json;
using AwesomeAssertions;
using IndexThinking.Agents;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A turn's own cap on tool rounds (<see cref="TurnLimits.WithMaxIterations"/>, enforced by <see cref="TurnLimitChatClient"/>):
/// the process-wide <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/> is state of the shared client, so a
/// host that mixes short and long turns could not lower it for one turn without racing the others.
/// </summary>
public sealed class TurnLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Stateless: calls <c>write_note</c> whenever it is offered tools, answers in text otherwise. Under <c>replace</c> alone it
    /// replaces once and then answers. It yields before answering so that turns running at the same time interleave.
    /// </summary>
    private sealed class NoteTaker : IChatClient
    {
        public List<string[]> ToolsSeen { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var offered = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
            lock (ToolsSeen)
            {
                ToolsSeen.Add(offered);
            }

            if (offered.Contains("write_note"))
            {
                return Call("write_note", new() { ["text"] = "n" });
            }

            if (offered.Contains("replace"))
            {
                var replaced = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString() == "replaced"));
                return replaced ? Text("changed") : Call("replace", new() { ["text"] = "new" });
            }

            return Text("out of rounds");
        }

        private static ChatResponse Call(string name, Dictionary<string, object?> args) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
            {
                FinishReason = ChatFinishReason.ToolCalls
            };

        private static ChatResponse Text(string text) =>
            new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatOptions Options(List<string>? replaced = null) => new()
    {
        Tools =
        [
            AIFunctionFactory.Create((string text) => "noted " + text, "write_note"),
            AIFunctionFactory.Create((string text) => { replaced?.Add(text); return "replaced"; }, "replace"),
        ]
    };

    private static IChatClient Pipeline(NoteTaker model, int processCap, bool withTurnLimits = true)
    {
        var builder = new ChatClientBuilder(model).UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = processCap);
        if (withTurnLimits)
        {
            builder.UseTurnLimits();
        }

        return builder.Build();
    }

    // Sends the turn's messages through the pipeline as they are: what the thinking loop builds reaches the model.
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

    private static async Task<TurnRecord> Run(IAgentLoop loop, ChatOptions options, bool streaming)
    {
        if (!streaming)
        {
            var r = await loop.RunAsync("take notes", options, Ct);
            return new TurnRecord { Content = r.Content, ToolCalls = r.ToolCalls, StopReason = r.StopReason, Messages = r.Messages, Usage = r.Usage };
        }

        AgentResponseChunk? last = null;
        await foreach (var chunk in loop.RunStreamingAsync("take notes", options, Ct))
        {
            last = chunk;
        }

        return last!.Turn!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATurnCapBelowTheProcessCap_EndsThatTurnOnly_AndTheNextTurnRunsToTheProcessCap(bool streaming)
    {
        var model = new NoteTaker();
        var loop = new AgentLoop(Pipeline(model, processCap: 10));

        var capped = await Run(loop, Options().WithMaxIterations(2), streaming);
        var uncapped = await Run(loop, Options(), streaming);

        capped.ToolCalls.Should().HaveCount(2);
        capped.StopReason.Should().Be(TurnStopReason.StepLimit, "the turn used its own cap, which reads like the process cap being reached");
        uncapped.ToolCalls.Should().HaveCount(10, "the cap of one turn leaves the next turn on the process default");
        uncapped.StopReason.Should().Be(TurnStopReason.StepLimit);
        model.ToolsSeen.Should().Contain(t => t.Length == 0, "the request after the cap offers no tools");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheThinkingLoop_AppliesAndClassifiesTheTurnCapToo(bool streaming)
    {
        var model = new NoteTaker();
        await using var loop = new ThinkingAgentLoop(Pipeline(model, processCap: 10), PassThrough());

        var capped = await Run(loop, Options().WithMaxIterations(2), streaming);

        capped.ToolCalls.Should().HaveCount(2);
        capped.StopReason.Should().Be(TurnStopReason.StepLimit);
    }

    [Fact]
    public async Task ATurnCapAboveTheProcessCap_DoesNotRaiseIt()
    {
        var loop = new AgentLoop(Pipeline(new NoteTaker(), processCap: 2));

        var turn = await Run(loop, Options().WithMaxIterations(5), streaming: false);

        turn.ToolCalls.Should().HaveCount(2, "the process value is the ceiling");
        turn.StopReason.Should().Be(TurnStopReason.StepLimit);
    }

    [Fact]
    public async Task TwoTurnsRunningAtTheSameTime_KeepTheirOwnCaps()
    {
        var model = new NoteTaker();
        var client = Pipeline(model, processCap: 10);
        var first = new AgentLoop(client);
        var second = new AgentLoop(client);

        for (var i = 0; i < 20; i++)
        {
            var turns = await Task.WhenAll(
                Run(first, Options().WithMaxIterations(1), streaming: false),
                Run(second, Options().WithMaxIterations(4), streaming: false));

            turns[0].ToolCalls.Should().HaveCount(1);
            turns[1].ToolCalls.Should().HaveCount(4);
        }
    }

    [Fact]
    public async Task WithoutTheClientInThePipeline_TheTurnCapIsIgnored_AndTheStopReasonDoesNotClaimIt()
    {
        var model = new NoteTaker();
        var client = Pipeline(model, processCap: 3, withTurnLimits: false);
        var options = Options().WithMaxIterations(1);

        TurnStopReasons.MaximumIterationsOf(client, options).Should().Be(3, "nothing in the pipeline applies the turn's value");

        var turn = await Run(new AgentLoop(client), options, streaming: false);
        turn.ToolCalls.Should().HaveCount(3);
        turn.StopReason.Should().Be(TurnStopReason.StepLimit);
    }

    [Fact]
    public void TheEffectiveCap_IsTheSmallerOfTheProcessCapAndTheTurnCap()
    {
        var client = Pipeline(new NoteTaker(), processCap: 6);

        TurnStopReasons.MaximumIterationsOf(client, null).Should().Be(6);
        TurnStopReasons.MaximumIterationsOf(client, new ChatOptions()).Should().Be(6);
        TurnStopReasons.MaximumIterationsOf(client, new ChatOptions().WithMaxIterations(2)).Should().Be(2);
        TurnStopReasons.MaximumIterationsOf(client, new ChatOptions().WithMaxIterations(9)).Should().Be(6);
    }

    [Fact]
    public async Task TheWrapUpsClosingRequest_StillOffersItsFinishingTool_AfterATurnCap()
    {
        var replaced = new List<string>();
        var model = new NoteTaker();
        var loop = new AgentLoop(
            Pipeline(model, processCap: 10),
            new AgentOptions { WrapUpAtStepLimit = new StepLimitWrapUp { ToolNames = ["replace"] } });

        var turn = await Run(loop, Options(replaced).WithMaxIterations(2), streaming: false);

        replaced.Should().Equal("new");
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        model.ToolsSeen.Last().Should().Equal(["replace"], "the closing request is the one extra request; the turn's cap must not strip it");
    }

    [Fact]
    public void WithMaxIterations_RejectsAValueBelowOne_AndDoesNotChangeTheOptionsItIsGiven()
    {
        var options = new ChatOptions();

        var below = () => options.WithMaxIterations(0);
        below.Should().Throw<ArgumentOutOfRangeException>();
        options.WithMaxIterations(3).AdditionalProperties![TurnLimits.MaxIterationsKey].Should().Be(3);
        options.AdditionalProperties.Should().BeNull("the helper returns a copy, so a shared options instance is not changed for other turns");
    }

    [Fact]
    public void TheCapIsReadFromWhatAJsonRoundTripCarries_AndAnUnusableValueMeansNoCap()
    {
        static ChatOptions With(object? value) => new() { AdditionalProperties = new AdditionalPropertiesDictionary { [TurnLimits.MaxIterationsKey] = value } };

        TurnLimits.MaxIterationsOf(With(3)).Should().Be(3);
        TurnLimits.MaxIterationsOf(With(3L)).Should().Be(3);
        TurnLimits.MaxIterationsOf(With(JsonDocument.Parse("3").RootElement)).Should().Be(3);
        TurnLimits.MaxIterationsOf(With(0)).Should().BeNull();
        TurnLimits.MaxIterationsOf(With(-2)).Should().BeNull();
        TurnLimits.MaxIterationsOf(With("3")).Should().BeNull();
        TurnLimits.MaxIterationsOf(With(JsonDocument.Parse("2.5").RootElement)).Should().BeNull();
        TurnLimits.MaxIterationsOf(null).Should().BeNull();
        TurnLimits.MaxIterationsOf(new ChatOptions()).Should().BeNull();
    }

    [Fact]
    public void TheRoundsOfATurn_AreCountedAfterTheLastRealUserMessage_NotAnInjectedReminder()
    {
        ChatMessage Round(string id) => new(ChatRole.Assistant, [new FunctionCallContent(id, "write_note")]);
        var reminder = new ChatMessage(ChatRole.User, "goal reminder")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ContextManager.InjectedBlockKey] = true }
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "earlier turn"), Round("old-1"), new(ChatRole.Tool, [new FunctionResultContent("old-1", "r")]),
            new(ChatRole.User, "this turn"), Round("a"), new(ChatRole.Tool, [new FunctionResultContent("a", "r")]),
            reminder, Round("b"), new(ChatRole.Tool, [new FunctionResultContent("b", "r")]),
        };

        TurnLimitChatClient.ToolRoundsOfTheTurn(messages).Should().Be(2);
    }
}
