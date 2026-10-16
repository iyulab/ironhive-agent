using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// What compaction keeps: the loop's history keeps every message (only what is sent is reduced), a history compacted twice
/// carries one summary (the earlier one is folded into the next), and the summarizer reads tool calls and results - where
/// the file paths and error codes of a tool-using session are. Before, the reduced history replaced the loop's own, each
/// compaction added a summary, and the summarizer read message text only.
/// </summary>
public class CompactionFidelityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string EarlyResult = "error CS0246: The type or namespace name 'Widget' could not be found (src/App/Program.cs:12)";

    /// <summary>100 tokens a message, a 1,000-token window.</summary>
    private sealed class FlatCounter : IContextTokenCounter
    {
        public string ModelName => "test-model";
        public int MaxContextTokens => 1000;
        public int CountTokens(ChatMessage message) => 100;
        public int CountTokens(IEnumerable<ChatMessage> messages) => messages.Count() * 100;
        public int CountTokens(string text) => text.Length / 4;
    }

    private sealed class CompactAbove(int tokens) : ICompactionTrigger
    {
        public bool ShouldCompact(int currentTokens, int maxTokens) => currentTokens > tokens;
        public float ThresholdPercentage => 0.8f;
    }

    private sealed class RecordingModel(string answer) : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingSummarizer : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"SUMMARY {Prompts.Count}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static List<ChatMessage> ToolSession()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Build the app and fix what fails."),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "run_build", new Dictionary<string, object?> { ["project"] = "src/App/App.csproj" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", EarlyResult)]),
        };
        for (var i = 0; i < 4; i++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, $"Step {i} done."));
            history.Add(new ChatMessage(ChatRole.User, $"Continue with step {i + 1}."));
        }

        return history;
    }

    private static (AgentLoop Loop, RecordingModel Model, RecordingSummarizer Summarizer) Build()
    {
        var counter = new FlatCounter();
        var summarizer = new RecordingSummarizer();
        var compactor = new TokenBasedHistoryCompactor(counter, new CompactionConfig { ProtectRecentTokens = 300, MinimumPruneTokens = 100 }, summarizer);
        var manager = new ContextManager(counter, new CompactAbove(800), compactor,
            goalReminderOptions: new GoalReminderOptions { Enabled = false });
        var model = new RecordingModel("ok");
        return (new AgentLoop(model, contextManager: manager), model, summarizer);
    }

    [Fact]
    public async Task AfterCompaction_TheLoopsHistoryStillHoldsTheOriginals()
    {
        var (loop, model, _) = Build();
        loop.InitializeHistory(ToolSession());

        await loop.RunAsync("And the tests?", Ct);

        Assert.DoesNotContain(model.Requests[^1], m => m.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "c1"));
        Assert.Contains(loop.History, m => m.Contents.OfType<FunctionResultContent>().Any(r => (r.Result as string) == EarlyResult));
        Assert.Equal(ToolSession().Count + 2, loop.History.Count);
    }

    [Fact]
    public async Task ASessionCompactedTwice_SendsOneSummary_ThatFoldsTheEarlierOneIn()
    {
        var (loop, model, summarizer) = Build();
        loop.InitializeHistory(ToolSession());

        await loop.RunAsync("And the tests?", Ct);
        for (var i = 0; i < 4; i++)
        {
            await loop.RunAsync($"One more thing, number {i}.", Ct);
        }

        Assert.True(summarizer.Prompts.Count >= 2, $"compacted {summarizer.Prompts.Count} time(s)");
        Assert.Single(model.Requests[^1], m => m.Text?.StartsWith("[Previous conversation summary]", StringComparison.Ordinal) == true);
        Assert.Contains("[Earlier summary]: SUMMARY 1", summarizer.Prompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorCodeInAToolResult_IsKeptInTheAnchoredStateBlock()
    {
        // The anchored compactor keeps error codes word for word, without a summarizer - but read them from message text
        // only, and a build reports them in a tool result
        var compactor = new AnchoredHistoryCompactor(new FlatCounter(), new CompactionConfig { ProtectRecentTokens = 300, MinimumPruneTokens = 100 });

        var result = await compactor.CompactAsync(ToolSession(), targetTokens: 500, Ct);

        Assert.Contains(result.CompactedHistory, m => m.Role == ChatRole.System && m.Text?.Contains("CS0246", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TheSummarizer_ReadsToolCallsAndResults()
    {
        var (loop, _, summarizer) = Build();
        loop.InitializeHistory(ToolSession());

        await loop.RunAsync("And the tests?", Ct);

        var prompt = Assert.Single(summarizer.Prompts);
        Assert.Contains("run_build", prompt, StringComparison.Ordinal);
        Assert.Contains("src/App/App.csproj", prompt, StringComparison.Ordinal);
        Assert.Contains("CS0246", prompt, StringComparison.Ordinal);
    }
}
