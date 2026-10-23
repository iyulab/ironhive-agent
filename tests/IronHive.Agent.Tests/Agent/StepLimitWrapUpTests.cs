using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="AgentOptions.WrapUpAtStepLimit"/>: a turn that reaches the iteration cap while the model is still reading gets
/// one closing request offering only the tools that finish the work, so the work is kept instead of lost with the turn.
/// </summary>
public sealed class StepLimitWrapUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] OnlyReplace = ["replace"];

    /// <summary>
    /// Reads (calls <c>read_source</c>) whenever it is offered; when only <c>replace</c> is offered, calls it once and then
    /// answers. With no tools offered it answers «out of rounds» — or, when <paramref name="lateCall"/> names a tool, writes a
    /// call to it anyway (a model whose text-written call a recovery step turns back into a call).
    /// </summary>
    private sealed class Reader(string? lateCall = null) : IChatClient
    {
        private int _calls;

        public List<string[]> ToolsSeen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var offered = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
            ToolsSeen.Add(offered);
            var list = messages.ToList();
            _calls++;

            if (offered.Contains("read_source"))
            {
                return Calls("read_source", new() { ["path"] = $"file{_calls}.cs" });
            }

            if (offered.Contains("replace"))
            {
                // Once the replacement ran, the closing request ends on text.
                if (list.Any(m => m.Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString() == "replaced")))
                {
                    return Text("changed file1.cs");
                }

                return Calls("replace", new() { ["path"] = "file1.cs", ["text"] = "new" });
            }

            return lateCall switch
            {
                "read_source" => Calls("read_source", new() { ["path"] = "late.cs" }),
                "replace" => Calls("replace", new() { ["path"] = "file1.cs", ["text"] = "new" }),
                _ => Text("out of rounds"),
            };
        }

        private Task<ChatResponse> Calls(string name, Dictionary<string, object?> args) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{_calls}", name, args)]))
            {
                FinishReason = ChatFinishReason.ToolCalls
            });

        private static Task<ChatResponse> Text(string text) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop });

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

    private sealed class Tools
    {
        public List<string> Reads { get; } = [];
        public List<string> Replaced { get; } = [];

        public ChatOptions Options() => new()
        {
            Tools =
            [
                AIFunctionFactory.Create((string path) => { Reads.Add(path); return "content of " + path; }, "read_source"),
                AIFunctionFactory.Create((string path, string text) => { Replaced.Add(path); return "replaced"; }, "replace"),
            ]
        };
    }

    private static (AgentLoop Loop, Reader Model) Loop(StepLimitWrapUp? wrapUp, string? lateCall = null)
    {
        var model = new Reader(lateCall);
        var client = new ChatClientBuilder(model).UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 2).Build();
        return (new AgentLoop(client, new AgentOptions { WrapUpAtStepLimit = wrapUp }), model);
    }

    private static async Task<TurnRecord> Run(AgentLoop loop, ChatOptions options, bool streaming)
    {
        if (!streaming)
        {
            var r = await loop.RunAsync("change file1.cs", options, Ct);
            return new TurnRecord { Content = r.Content, ToolCalls = r.ToolCalls, StopReason = r.StopReason, Messages = r.Messages, Usage = r.Usage };
        }

        AgentResponseChunk? last = null;
        await foreach (var chunk in loop.RunStreamingAsync("change file1.cs", options, Ct))
        {
            last = chunk;
        }

        return last!.Turn!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtTheCap_TheClosingRequestOffersOnlyTheFinishingTool_AndTheWorkIsKept(bool streaming)
    {
        var tools = new Tools();
        var (loop, model) = Loop(new StepLimitWrapUp { ToolNames = ["replace"] });

        var turn = await Run(loop, tools.Options(), streaming);

        tools.Replaced.Should().Equal("file1.cs");
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        model.ToolsSeen.Should().ContainEquivalentOf(OnlyReplace, "the closing request offers only the finishing tool");
        turn.Content.Should().Contain("changed file1.cs");
        turn.ToolCalls.Select(c => c.ToolName).Should().Contain("replace").And.Contain("read_source");
        loop.History.Should().Contain(m => m.Role == ChatRole.User && m.Text.Contains("used all the tool rounds"),
            "the closing instruction is part of the conversation the next turn sends");
        loop.History.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId)
            .Should().BeSubsetOf(loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallMadeAtTheCap_IsAnsweredWithTheNotice_NotRun(bool streaming)
    {
        var tools = new Tools();
        var (loop, _) = Loop(new StepLimitWrapUp { ToolNames = ["replace"], UnrunCallNotice = "skipped: no rounds left" }, lateCall: "read_source");

        var turn = await Run(loop, tools.Options(), streaming);

        tools.Reads.Should().NotContain("late.cs");
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Should().Contain(r => r.Result!.ToString() == "skipped: no rounds left");
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        tools.Replaced.Should().Equal("file1.cs");
    }

    /// <summary>Records the calls the pipeline runs; ends the turn on <c>replace</c> when told to.</summary>
    private sealed class Recording(bool terminateOnReplace = false) : IToolInvocationMiddleware
    {
        public List<string> Seen { get; } = [];

        public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
        {
            Seen.Add(context.Function.Name);
            if (terminateOnReplace && context.Function.Name == "replace")
            {
                context.Terminate = true;
            }

            return next(context, cancellationToken);
        }
    }

    private static (AgentLoop Loop, Reader Model) PipelineLoop(StepLimitWrapUp wrapUp, Recording recording, string? lateCall)
    {
        var model = new Reader(lateCall);
        var client = new ChatClientBuilder(model)
            .UseToolInvocationPipeline(new ToolInvocationPipeline([recording]), c => c.MaximumIterationsPerRequest = 2)
            .Build();
        return (new AgentLoop(client, new AgentOptions { WrapUpAtStepLimit = wrapUp }), model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFinishingCallMadeAtTheCap_RunsThroughThePipeline_AndTheClosingRequestFollows(bool streaming)
    {
        var tools = new Tools();
        var recording = new Recording();
        var (loop, model) = PipelineLoop(new StepLimitWrapUp { ToolNames = ["replace"], UnrunCallNotice = "skipped" }, recording, lateCall: "replace");

        var arrived = new List<string>();
        TurnRecord turn;
        if (streaming)
        {
            AgentResponseChunk? last = null;
            await foreach (var chunk in loop.RunStreamingAsync("change file1.cs", tools.Options(), Ct))
            {
                if (chunk.ToolResult is { } result)
                {
                    arrived.Add(result.ToolName);
                }

                last = chunk;
            }

            turn = last!.Turn!;
        }
        else
        {
            turn = await Run(loop, tools.Options(), streaming: false);
        }

        tools.Replaced.Should().Equal(["file1.cs"], "the write made at the cap runs once and is not asked for again");
        loop.History.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Count(c => c.Name == "replace")
            .Should().Be(1, "the model is not made to write the same change a second time");
        recording.Seen.Should().Contain("replace", "the finishing call goes through the same pipeline as every call");
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Should().NotContain(r => r.Result!.ToString() == "skipped");
        model.ToolsSeen[^1].Should().Equal(OnlyReplace, "the closing request still follows");
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        turn.Content.Should().Contain("changed file1.cs");
        turn.ToolCalls.Should().Contain(c => c.ToolName == "replace" && c.Success == true);
        if (streaming)
        {
            arrived.Should().Contain("replace", "a host streaming the turn sees the finishing call's result arrive");
        }
    }

    [Fact]
    public async Task AFinishingCallThatEndsTheTurn_EndsItWithoutTheClosingRequest()
    {
        var tools = new Tools();
        var (loop, model) = PipelineLoop(new StepLimitWrapUp { ToolNames = ["replace"] }, new Recording(terminateOnReplace: true), lateCall: "replace");

        var turn = await Run(loop, tools.Options(), streaming: false);

        tools.Replaced.Should().Equal("file1.cs");
        turn.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        model.ToolsSeen.Should().NotContainEquivalentOf(OnlyReplace, "no closing request after a call that ended the turn");
        loop.History.Should().NotContain(m => m.Role == ChatRole.User && m.Text.Contains("used all the tool rounds"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAWrapUp_TheTurnStillEndsAtTheCap(bool streaming)
    {
        var tools = new Tools();
        var (loop, model) = Loop(wrapUp: null);

        var turn = await Run(loop, tools.Options(), streaming);

        turn.StopReason.Should().Be(TurnStopReason.StepLimit);
        tools.Replaced.Should().BeEmpty();
        model.ToolsSeen.Should().NotContainEquivalentOf(OnlyReplace);
    }

    [Fact]
    public void TheThinkingLoop_RefusesAWrapUp_RatherThanIgnoringIt()
    {
        var act = () => new ThinkingAgentLoop(
            new Reader(), NSubstitute.Substitute.For<IndexThinking.Agents.IThinkingTurnManager>(),
            new AgentOptions { WrapUpAtStepLimit = new StepLimitWrapUp() });

        act.Should().Throw<NotSupportedException>().WithMessage("*WrapUpAtStepLimit*");
    }

    [Fact]
    public async Task ATextOnlyWrapUp_OffersNoTools_AndEndsOnTheModelsAnswer()
    {
        var tools = new Tools();
        var (loop, model) = Loop(new StepLimitWrapUp());

        var turn = await Run(loop, tools.Options(), streaming: false);

        model.ToolsSeen[^1].Should().BeEmpty();
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        tools.Replaced.Should().BeEmpty();
    }
}
