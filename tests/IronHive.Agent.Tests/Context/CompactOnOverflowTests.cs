using IronHive.Abstractions.Exceptions;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// When the model's context window is not known, the counter guesses 8192 and a manager would compact against the guess
/// long before a larger server needs it. With <see cref="CompactionConfig.CompactOnOverflow"/>, pre-emptive compaction
/// waits for the server: an overflow is compacted once and retried by <see cref="ToolRoundContextChatClient"/>, and the
/// window it states is learned for the rest of the session.
/// </summary>
public class CompactOnOverflowTests
{
    private const string UnknownModel = "some-local-model-not-in-the-catalog";

    private static List<ChatMessage> LongConversation(int exchanges)
    {
        var history = new List<ChatMessage>();
        for (var i = 1; i <= exchanges; i++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"question {i}: " + new string('q', 2_000)));
            history.Add(new ChatMessage(ChatRole.Assistant, $"answer {i}: " + new string('a', 2_000)));
        }
        history.Add(new ChatMessage(ChatRole.User, "latest question"));
        return history;
    }

    private static ContextManager Manager(bool compactOnOverflow = true, int? maxContextTokens = null)
        => ContextManager.ForModel(UnknownModel, new CompactionConfig
        {
            CompactOnOverflow = compactOnOverflow,
            MaxContextTokens = maxContextTokens,
            EnableObservationMasking = false,
            EnableToolResultCompaction = false,
        });

    [Fact]
    public void An_Estimated_Window_Defers_Compaction_Only_When_A_Client_Will_Catch_The_Overflow()
    {
        var history = LongConversation(10); // well past 92 % of the 8192 guess
        var unbound = Manager();
        Assert.True(unbound.TokenCounter.IsContextWindowEstimated);
        Assert.True(unbound.ShouldCompact(history)); // nothing would catch an overflow: the guess still rules

        var bound = Manager();
        _ = new ToolRoundContextChatClient(new OverflowingModel(window: 100_000), bound);

        Assert.True(bound.DefersCompactionToOverflow);
        Assert.False(bound.ShouldCompact(history));
    }

    [Fact]
    public void A_Known_Window_Is_Never_Deferred()
    {
        var manager = Manager(maxContextTokens: 8192);
        _ = new ToolRoundContextChatClient(new OverflowingModel(window: 100_000), manager);

        Assert.False(manager.TokenCounter.IsContextWindowEstimated);
        Assert.False(manager.DefersCompactionToOverflow);
        Assert.True(manager.ShouldCompact(LongConversation(10)));
    }

    [Fact]
    public async Task An_Overflow_Is_Compacted_Once_Retried_And_Its_Window_Learned()
    {
        var manager = Manager();
        var model = new OverflowingModel(window: 4_096);
        var client = new ToolRoundContextChatClient(model, manager);
        var history = LongConversation(10);

        var response = await client.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, model.Calls.Count);
        Assert.True(Count(manager, model.Calls[1]) < Count(manager, model.Calls[0]));
        Assert.Equal(4_096, manager.MaxContextTokens);
        Assert.False(manager.TokenCounter.IsContextWindowEstimated);
        Assert.False(manager.DefersCompactionToOverflow); // learned: pre-emptive compaction resumes against the real window
        Assert.Equal("latest question", model.Calls[1][^1].Text);
    }

    [Fact]
    public async Task Without_A_Stated_Window_The_Request_Size_Is_Learned_As_A_Bound()
    {
        var manager = Manager();
        var model = new OverflowingModel(window: 4_096, statesWindow: false, reportsRequestTokens: 9_000);
        var client = new ToolRoundContextChatClient(model, manager);

        // 70 % of the bound is still above the real window, so this call's retry overflows too and propagates; the bound
        // is what the rest of the session compacts against until a smaller one is learned.
        await Assert.ThrowsAsync<ContextOverflowException>(() =>
            client.GetResponseAsync(LongConversation(10), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(9_000, manager.MaxContextTokens);
        Assert.False(manager.TokenCounter.IsContextWindowEstimated);
    }

    [Fact]
    public async Task A_Second_Overflow_Propagates()
    {
        var manager = Manager();
        var model = new OverflowingModel(window: 10); // nothing fits
        var client = new ToolRoundContextChatClient(model, manager);

        await Assert.ThrowsAsync<ContextOverflowException>(() =>
            client.GetResponseAsync(LongConversation(10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, model.Calls.Count);
    }

    [Fact]
    public async Task With_The_Option_Off_An_Overflow_Propagates_Untouched()
    {
        var manager = Manager(compactOnOverflow: false);
        var model = new OverflowingModel(window: 4_096);
        var client = new ToolRoundContextChatClient(model, manager);

        await Assert.ThrowsAsync<ContextOverflowException>(() =>
            client.GetResponseAsync(LongConversation(10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(model.Calls);
        Assert.True(manager.TokenCounter.IsContextWindowEstimated);
    }

    [Fact]
    public async Task A_Streaming_Call_That_Overflows_Before_Its_First_Update_Is_Retried()
    {
        var manager = Manager();
        var model = new OverflowingModel(window: 4_096);
        var client = new ToolRoundContextChatClient(model, manager);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(LongConversation(10), cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal("ok", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(2, model.Calls.Count);
    }

    [Fact]
    public async Task The_Next_Tool_Round_Reuses_The_Compaction_Instead_Of_Compacting_Again()
    {
        var manager = Manager();
        var model = new OverflowingModel(window: 4_096);
        var client = new ToolRoundContextChatClient(model, manager);
        var round = LongConversation(10);

        await client.GetResponseAsync(round, cancellationToken: TestContext.Current.CancellationToken);
        // Function invocation re-sends the same list with the round's call and result appended.
        round.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read")]));
        round.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "short result")]));
        await client.GetResponseAsync(round, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Calls.Count); // overflow + retry, then the next round goes through first time
        var next = model.Calls[2];
        Assert.True(Count(manager, next) < 4_096);
        Assert.IsType<FunctionResultContent>(next[^1].Contents.Single());
    }

    [Fact]
    public void Target_Ratio_Reaches_The_Manager()
    {
        var manager = ContextManager.ForModel("gpt-4o", new CompactionConfig { TargetRatio = 0.5f });

        Assert.Equal(0.5f, manager.TargetRatio);
    }

    [Fact]
    public async Task Pre_Emptive_Compaction_Targets_The_Configured_Ratio()
    {
        var compactor = new RecordingCompactor();
        var manager = new ContextManager(
            new ContextTokenCounter("gpt-4o", 10_000), new ThresholdCompactionTrigger(0.5f), compactor)
        { TargetRatio = 0.5f };

        await manager.CompactIfNeededAsync(LongConversation(10), TestContext.Current.CancellationToken);

        Assert.Equal(5_000, compactor.Target);
    }

    private static int Count(ContextManager manager, IReadOnlyList<ChatMessage> messages)
        => manager.TokenCounter.CountTokens(messages);

    private sealed class OverflowingModel(int window, bool statesWindow = true, int? reportsRequestTokens = null) : IChatClient
    {
        private readonly ContextTokenCounter _counter = new("gpt-4o", int.MaxValue);

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        private void Check(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            Calls.Add(list);
            var tokens = _counter.CountTokens(list);
            if (tokens > window)
            {
                throw new ContextOverflowException($"request of {tokens} tokens exceeds {window}")
                {
                    ContextWindow = statesWindow ? window : null,
                    RequestTokens = reportsRequestTokens ?? (statesWindow ? tokens : null),
                };
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Check(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Check(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "o");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "k");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingCompactor : IHistoryCompactor
    {
        public int Target { get; private set; }

        public Task<CompactionResult> CompactAsync(IReadOnlyList<ChatMessage> history, int targetTokens, CancellationToken cancellationToken = default)
        {
            Target = targetTokens;
            return Task.FromResult(new CompactionResult
            {
                CompactedHistory = history, OriginalTokens = 0, CompactedTokens = 0, MessagesCompacted = 0,
            });
        }
    }
}
