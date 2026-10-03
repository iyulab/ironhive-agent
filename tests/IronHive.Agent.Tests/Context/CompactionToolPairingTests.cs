using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// What every compactor owes the next request: a tool result never stands without its call (or a call without its
/// result), and nothing the user said disappears without a trace — it is kept, summarized, or replaced by a marker.
/// The history is the one that exposed the defect: a first message with decisions, then five turns that each read a
/// ~1.7k-token log, compacted with an 8k window.
/// </summary>
public class CompactionToolPairingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Decisions = "My decisions: port 8443; region eu-central-2; owner team atlas. Confirm.";

    private sealed class CountingSummarizer : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The user chose port 8443, region eu-central-2, owner atlas.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly ContextTokenCounter Counter = new("test-model", 8_000);

    private static CompactionConfig Config(bool anchored) => new()
    {
        UseAnchoredCompaction = anchored,
        ProtectRecentTokens = 3_000,
        MinimumPruneTokens = 1_500,
        EnableObservationMasking = false,
    };

    private static List<ChatMessage> History(int days = 5)
    {
        var log = string.Concat(Enumerable.Range(0, 110).Select(i =>
            $"2026-09-01T00:{i % 60:00}:00Z INFO worker-{i % 7} processed batch {1000 + i} in {i * 37 % 900} ms\n"));
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are an agent."),
            new(ChatRole.User, Decisions),
            new(ChatRole.Assistant, "Confirmed."),
        };
        for (var day = 1; day <= days; day++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"Read logs/day-{day}.log and count ERROR lines."));
            history.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"c{day}", "ReadFile", new Dictionary<string, object?> { ["path"] = $"logs/day-{day}.log" })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{day}", log)]));
            history.Add(new ChatMessage(ChatRole.Assistant, "12"));
        }

        history.Add(new ChatMessage(ChatRole.User, "Now write config.json."));
        return history;
    }

    private static void AssertEveryToolResultHasItsCallAndBack(IReadOnlyList<ChatMessage> messages)
    {
        var calls = messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId).ToHashSet();
        var results = messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet();
        Assert.Empty(results.Except(calls)); // no result without its call
        Assert.Empty(calls.Except(results)); // no call without its result (none is waiting at the end here)
    }

    public static TheoryData<string> Compactors => ["token-based", "anchored", "threshold"];

    private static IHistoryCompactor Create(string kind, IChatClient? summarizer) => kind switch
    {
        "token-based" => new TokenBasedHistoryCompactor(Counter, Config(anchored: false), summarizer),
        "anchored" => new AnchoredHistoryCompactor(Counter, Config(anchored: true), summarizer),
        _ => new HistoryCompactor(Counter, summarizer),
    };

    [Theory]
    [MemberData(nameof(Compactors))]
    public async Task No_compactor_returns_a_tool_result_without_its_call(string kind)
    {
        foreach (var summarizer in new IChatClient?[] { new CountingSummarizer(), null })
        {
            var result = await Create(kind, summarizer).CompactAsync(History(), targetTokens: 5_600, Ct);

            Assert.True(result.CompactedHistory.Count < History().Count, $"{kind}: nothing was compacted");
            AssertEveryToolResultHasItsCallAndBack(result.CompactedHistory);
        }
    }

    [Fact]
    public async Task Token_based_keeps_the_users_decisions_and_summarizes_what_it_leaves_out()
    {
        var summarizer = new CountingSummarizer();
        var result = await Create("token-based", summarizer).CompactAsync(History(), targetTokens: 5_600, Ct);

        Assert.Contains(result.CompactedHistory, m => m.Role == ChatRole.User && m.Text == Decisions);
        Assert.Equal(1, summarizer.Calls);
        Assert.Contains(result.CompactedHistory, m => m.Text?.Contains("[Previous conversation summary]", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Token_based_without_a_summarizer_says_how_many_messages_it_left_out()
    {
        var result = await Create("token-based", null).CompactAsync(History(), targetTokens: 5_600, Ct);

        Assert.Contains(result.CompactedHistory, m => m.Role == ChatRole.System
            && m.Text?.Contains("earlier messages omitted", StringComparison.Ordinal) == true);
        Assert.Contains(result.CompactedHistory, m => m.Role == ChatRole.User && m.Text == Decisions);
    }

    [Fact]
    public async Task Token_based_reaches_its_target_even_when_the_protected_tool_outputs_alone_exceed_it()
    {
        // Five protected ReadFile results (~8.6k) cannot all stay under a 5.6k target: the oldest join the summary.
        var result = await Create("token-based", new CountingSummarizer()).CompactAsync(History(), targetTokens: 5_600, Ct);

        Assert.True(result.CompactedTokens <= 5_600, $"compacted to {result.CompactedTokens} tokens");
    }

    [Theory]
    [MemberData(nameof(Compactors))]
    public async Task A_protected_region_that_would_start_between_a_call_and_its_result_takes_the_call_too(string kind)
    {
        // Protect budget sized to hold the last result and the messages after it, but not the call before it.
        var history = History(days: 2);
        var config = Config(anchored: kind == "anchored");
        var tail = Counter.CountTokens(history.Skip(history.Count - 3));
        config.ProtectRecentTokens = tail + 10;
        config.MinimumPruneTokens = 1;
        IHistoryCompactor compactor = kind switch
        {
            "token-based" => new TokenBasedHistoryCompactor(Counter, config),
            "anchored" => new AnchoredHistoryCompactor(Counter, config),
            _ => new HistoryCompactor(Counter),
        };

        var result = await compactor.CompactAsync(history, targetTokens: tail + 200, Ct);

        AssertEveryToolResultHasItsCallAndBack(result.CompactedHistory);
    }
}
