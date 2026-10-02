using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using IndexThinking.Agents;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// <see cref="ToolRetrievalOptions.StickyToolLimit"/>: tools a conversation already sent stay first, in the order first
/// sent, so a prefix-cached server re-reads only from the first new tool.
/// </summary>
public class StickyToolSelectionTests
{
    private readonly KeywordToolRetriever _retriever = new();

    private static readonly ToolRetrievalOptions Sticky = new() { MaxTools = 3, MinRelevanceScore = 0.2f, StickyToolLimit = 20 };

    [Fact]
    public async Task Off_ByDefault_TheCarriedNamesAreIgnored()
    {
        var plain = await Retrieve("execute a shell command", Sticky with { StickyToolLimit = 0 });
        var withNames = await Retrieve("execute a shell command", Sticky with { StickyToolLimit = 0, StickyTools = ["ReadFile", "WriteFile"] });

        Assert.Equal(Names(plain), Names(withNames));
        Assert.Equal(Names(plain).Order(StringComparer.Ordinal), Names(plain));
    }

    [Fact]
    public async Task TheToolsSentEarlier_AreSentFirst_InFirstSentOrder_AndTheNewOnesFollow()
    {
        var first = await Retrieve("read a file", Sticky);
        var second = await Retrieve("execute a shell command", Sticky with { StickyTools = Names(first) });

        Assert.Equal(Names(first), Names(second).Take(first.SelectedTools.Count));
        Assert.Contains("ExecuteCommand", Names(second).Skip(first.SelectedTools.Count));
        Assert.Contains(second.Selections, s => s.Reason == ToolSelectionReason.Carried);
    }

    [Fact]
    public async Task ACarriedToolSelectedAgain_KeepsItsPlace_AndItsOwnReason()
    {
        var second = await Retrieve("read a file", Sticky with { StickyTools = ["WriteFile", "ReadFile"] });

        Assert.Equal(["WriteFile", "ReadFile"], Names(second).Take(2));
        Assert.NotEqual(ToolSelectionReason.Carried, Assert.Single(second.Selections, s => s.Name == "ReadFile").Reason);
    }

    [Fact]
    public async Task OverTheLimit_TheSelectionStartsOver()
    {
        var fresh = await Retrieve("execute a shell command", Sticky);
        var full = await Retrieve("execute a shell command",
            Sticky with { StickyToolLimit = 2, StickyTools = ["ReadFile", "WriteFile"] });

        Assert.Equal(Names(fresh), Names(full));
        Assert.DoesNotContain(full.Selections, s => s.Reason == ToolSelectionReason.Carried);
    }

    [Fact]
    public async Task ACarriedNameNoLongerInTheCatalog_IsDropped()
    {
        var result = await Retrieve("read a file", Sticky with { StickyTools = ["gone_tool", "WriteFile"] });

        Assert.DoesNotContain("gone_tool", Names(result));
        Assert.Equal("WriteFile", Names(result)[0]);
    }

    [Fact]
    public async Task AgentLoop_CarriesTheConversation_AndClearHistoryForgetsIt()
    {
        var recording = new RecordingRetriever(_retriever);
        var loop = new AgentLoop(new MockChatClient().EnqueueResponse("a").EnqueueResponse("b").EnqueueResponse("c"),
            new AgentOptions { Tools = Catalogue(), ToolRetrievalOptions = Sticky }, toolRetriever: recording);

        await loop.RunAsync("read a file", TestContext.Current.CancellationToken);
        await loop.RunAsync("execute a shell command", TestContext.Current.CancellationToken);
        loop.ClearHistory();
        await loop.RunAsync("execute a shell command", TestContext.Current.CancellationToken);

        AssertCarried(recording);
    }

    [Fact]
    public async Task ThinkingAgentLoop_CarriesTheConversation_AndClearHistoryForgetsIt()
    {
        var recording = new RecordingRetriever(_retriever);
        var turnManager = Substitute.For<IThinkingTurnManager>();
        turnManager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]), TurnMetrics.Empty, null)));
        var loop = new ThinkingAgentLoop(new MockChatClient().EnqueueResponse("a").EnqueueResponse("b").EnqueueResponse("c"), turnManager,
            new AgentOptions { Tools = Catalogue(), ToolRetrievalOptions = Sticky }, toolRetriever: recording);

        await loop.RunAsync("read a file", TestContext.Current.CancellationToken);
        await loop.RunAsync("execute a shell command", TestContext.Current.CancellationToken);
        loop.ClearHistory();
        await loop.RunAsync("execute a shell command", TestContext.Current.CancellationToken);

        AssertCarried(recording);
    }

    private static void AssertCarried(RecordingRetriever recording)
    {
        Assert.Equal(3, recording.Calls.Count);
        var (firstOptions, first) = recording.Calls[0];
        var (secondOptions, second) = recording.Calls[1];
        var (thirdOptions, third) = recording.Calls[2];

        Assert.Empty(firstOptions!.StickyTools);
        Assert.Equal(Names(first), secondOptions!.StickyTools);
        Assert.Equal(Names(first), Names(second).Take(first.SelectedTools.Count));
        Assert.Empty(thirdOptions!.StickyTools);
        Assert.DoesNotContain(third.Selections, s => s.Reason == ToolSelectionReason.Carried);
    }

    private Task<ToolRetrievalResult> Retrieve(string query, ToolRetrievalOptions options) =>
        _retriever.RetrieveAsync(query, Catalogue(), options, TestContext.Current.CancellationToken);

    private static List<string> Names(ToolRetrievalResult result) => result.SelectedTools.Select(t => t.Name).ToList();

    private static IList<AITool> Catalogue() =>
    [
        AIFunctionFactory.Create(() => "ok", "ReadFile", "Read the content of a file at the specified path."),
        AIFunctionFactory.Create(() => "ok", "WriteFile", "Write content to a file."),
        AIFunctionFactory.Create(() => "ok", "ListDirectory", "List the contents of a directory."),
        AIFunctionFactory.Create(() => "ok", "GrepFiles", "Search for a pattern in files."),
        AIFunctionFactory.Create(() => "ok", "ExecuteCommand", "Execute a shell command and return the output."),
    ];

    private sealed class RecordingRetriever(IToolRetriever inner) : IToolRetriever
    {
        public List<(ToolRetrievalOptions? Options, ToolRetrievalResult Result)> Calls { get; } = [];

        public async Task<ToolRetrievalResult> RetrieveAsync(
            string query, IList<AITool> availableTools, ToolRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.RetrieveAsync(query, availableTools, options, cancellationToken);
            Calls.Add((options, result));
            return result;
        }
    }
}
