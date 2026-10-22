using AwesomeAssertions;
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
    /// answers. With no tools offered it answers «out of rounds» — or, when <paramref name="callsWithoutTools"/>, writes a
    /// call anyway (a model whose text-written call a recovery step turns back into a call).
    /// </summary>
    private sealed class Reader(bool callsWithoutTools = false) : IChatClient
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

            return callsWithoutTools
                ? Calls("read_source", new() { ["path"] = "late.cs" })
                : Text("out of rounds");
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

    private static (AgentLoop Loop, Reader Model) Loop(StepLimitWrapUp? wrapUp, bool callsWithoutTools = false)
    {
        var model = new Reader(callsWithoutTools);
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
        var (loop, _) = Loop(new StepLimitWrapUp { ToolNames = ["replace"], UnrunCallNotice = "skipped: no rounds left" }, callsWithoutTools: true);

        var turn = await Run(loop, tools.Options(), streaming);

        tools.Reads.Should().NotContain("late.cs");
        loop.History.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Should().Contain(r => r.Result!.ToString() == "skipped: no rounds left");
        turn.StopReason.Should().Be(TurnStopReason.WrappedUp);
        tools.Replaced.Should().Equal("file1.cs");
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
