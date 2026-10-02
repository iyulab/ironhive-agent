using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// One user message followed by many tool rounds (read a long document section by section) is a single user turn, so
/// protecting recent user turns protects every result of that turn. <see cref="CompactionConfig.ObservationMaskingProtectedTokens"/>
/// masks the results inside it that no longer fit a size budget, and <see cref="ToolRoundContextChatClient"/> applies that
/// to each model call of the turn.
/// </summary>
public class ToolRoundMaskingTests
{
    private static readonly string LongResult = new('x', 2_000);

    /// <summary>One token per character, so a budget reads as a character count.</summary>
    private static readonly IContextTokenCounter Chars = new CharTokenCounter();

    private static List<ChatMessage> OneTurnWithRounds(int rounds)
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "Read every section and take notes.") };
        for (var r = 1; r <= rounds; r++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{r}", "read_section")]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{r}", $"section {r}: {LongResult}")]));
        }
        return history;
    }

    private static string ResultOf(IReadOnlyList<ChatMessage> history, string callId)
        => history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == callId).Result!.ToString()!;

    private static bool IsMasked(IReadOnlyList<ChatMessage> history, string callId)
        => ResultOf(history, callId).StartsWith("[Masked:", StringComparison.Ordinal);

    [Fact]
    public void A_Token_Budget_Keeps_The_Recent_Results_That_Fit_And_Masks_The_Rest()
    {
        // Each result is ~2,012 chars: a 4,100 budget holds the newest two.
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 4_100, tokenCounter: Chars);

        var masked = masker.MaskObservations(OneTurnWithRounds(5));

        Assert.True(IsMasked(masked, "c1"));
        Assert.True(IsMasked(masked, "c3"));
        Assert.StartsWith("section 4:", ResultOf(masked, "c4"));
        Assert.StartsWith("section 5:", ResultOf(masked, "c5"));
        // The calls and the user message stay: the model still knows what it read.
        Assert.Equal(5, masked.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(ChatRole.User, masked[0].Role);
    }

    [Fact]
    public void Without_A_Budget_One_Turn_Is_Never_Masked()
    {
        // Positive control: the documented user-turn rule alone protects the whole turn.
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200);

        var history = OneTurnWithRounds(5);

        Assert.Same(history, masker.MaskObservations(history));
    }

    [Fact]
    public void Write_Rounds_With_Short_Results_Do_Not_Push_Earlier_Reads_Out()
    {
        // Read six sources, then write one output per source: seven write rounds that each answer "ok". A rule that counted
        // rounds would mask every read three writes in, while their content is still being turned into output.
        var history = new List<ChatMessage> { new(ChatRole.User, "Extract each part into its own file.") };
        for (var p = 1; p <= 6; p++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"read{p}", "read_document",
                new Dictionary<string, object?> { ["part"] = p })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"read{p}", $"part {p}: {new string('y', 3_000)}")]));
        }
        for (var w = 1; w <= 7; w++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"write{w}", "write_file",
                new Dictionary<string, object?> { ["path"] = $"part{w}.json", ["content"] = new string('z', 1_500) })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"write{w}", "ok")]));
        }

        var fits = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 20_000, tokenCounter: Chars)
            .MaskObservations(history);
        Assert.Same(history, fits);

        // Positive control: the same history with a budget smaller than the reads masks the oldest of them.
        var tight = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 7_000, tokenCounter: Chars)
            .MaskObservations(history);
        Assert.True(IsMasked(tight, "read1"));
        Assert.True(IsMasked(tight, "read4"));
        Assert.StartsWith("part 5:", ResultOf(tight, "read5"));
        Assert.StartsWith("part 6:", ResultOf(tight, "read6"));
    }

    [Fact]
    public void The_Newest_Rounds_Results_Are_Kept_Even_When_They_Alone_Exceed_The_Budget()
    {
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 500, tokenCounter: Chars);

        var masked = masker.MaskObservations(OneTurnWithRounds(3));

        Assert.StartsWith("section 3:", ResultOf(masked, "c3"));
        Assert.True(IsMasked(masked, "c2"));
        Assert.True(IsMasked(masked, "c1"));
    }

    [Fact]
    public void The_Budget_Is_Applied_Per_Result_Inside_One_Tool_Message()
    {
        // One round that read five files in parallel, then a later round: the budget keeps the newest results of the
        // parallel round and masks its older ones.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read the five files, then check the index."),
            new(ChatRole.Assistant, [.. Enumerable.Range(1, 5).Select(i => (AIContent)new FunctionCallContent($"f{i}", "read_file"))]),
            new(ChatRole.Tool, [.. Enumerable.Range(1, 5).Select(i => (AIContent)new FunctionResultContent($"f{i}", $"file {i}: {LongResult}"))]),
            new(ChatRole.Assistant, [new FunctionCallContent("idx", "read_index")]),
            new(ChatRole.Tool, [new FunctionResultContent("idx", "index: 5 files")]),
        };
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 4_100, tokenCounter: Chars);

        var masked = masker.MaskObservations(history);

        Assert.True(IsMasked(masked, "f1"));
        Assert.True(IsMasked(masked, "f3"));
        Assert.StartsWith("file 4:", ResultOf(masked, "f4"));
        Assert.StartsWith("file 5:", ResultOf(masked, "f5"));
    }

    [Fact]
    public void A_Masked_Result_Stays_Masked_With_The_Same_Text_As_Rounds_Are_Added()
    {
        // A local server that reuses its prompt cache re-reads everything after the first changed byte: a result that
        // flips back to whole, or a placeholder that changes, would cost a full re-read every round.
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 4_100, tokenCounter: Chars);
        string[]? previous = null;

        for (var rounds = 1; rounds <= 6; rounds++)
        {
            var results = masker.MaskObservations(OneTurnWithRounds(rounds))
                .SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result!.ToString()!).ToArray();
            if (previous is not null)
            {
                for (var i = 0; i < previous.Length; i++)
                {
                    if (previous[i].StartsWith("[Masked:", StringComparison.Ordinal))
                    {
                        Assert.Equal(previous[i], results[i]);
                    }
                }
            }

            previous = results;
        }

        Assert.Equal(4, previous!.Count(r => r.StartsWith("[Masked:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_Placeholder_Names_The_Call_And_Says_How_To_Get_The_Content_Back()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Read part 3."),
            new(ChatRole.Assistant, [new FunctionCallContent("r3", "read_document",
                new Dictionary<string, object?> { ["path"] = "standard.pdf", ["part"] = 3 })]),
            new(ChatRole.Tool, [new FunctionResultContent("r3", LongResult)]),
            new(ChatRole.Assistant, [new FunctionCallContent("w3", "write_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("w3", "ok")]),
        };
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 10, tokenCounter: Chars);

        var placeholder = ResultOf(masker.MaskObservations(history), "r3");

        Assert.Equal(
            "[Masked: read_document {\"path\":\"standard.pdf\",\"part\":3} result, 2,000 chars, ~1 lines. "
            + "The content is no longer visible here; call read_document again with the same arguments if you need it.]",
            placeholder);
    }

    [Fact]
    public void A_Placeholder_Cuts_Long_Arguments()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Search."),
            new(ChatRole.Assistant, [new FunctionCallContent("s", "search",
                new Dictionary<string, object?> { ["query"] = new string('q', 1_000) })]),
            new(ChatRole.Tool, [new FunctionResultContent("s", LongResult)]),
            new(ChatRole.Assistant, [new FunctionCallContent("w", "write_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("w", "ok")]),
        };
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 10, tokenCounter: Chars);

        var placeholder = ResultOf(masker.MaskObservations(history), "s");

        Assert.Contains("…", placeholder, StringComparison.Ordinal);
        Assert.True(placeholder.Length < 500, placeholder);
    }

    [Fact]
    public void A_Budget_Without_A_Token_Counter_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => new ObservationMasker(protectedTokens: 1_000));
    }

    [Fact]
    public void A_Budget_Does_Not_Unmask_What_The_Turn_Rule_Masks()
    {
        // Two turns; the older one is outside the protected turn, and stays masked however large the budget.
        var history = OneTurnWithRounds(1);
        history.Add(new ChatMessage(ChatRole.Assistant, "noted"));
        history.AddRange(OneTurnWithRounds(1).Select(m => m.Role == ChatRole.User ? new ChatMessage(ChatRole.User, "next") : m));
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 200, protectedTokens: 1_000_000, tokenCounter: Chars);

        var masked = masker.MaskObservations(history);

        Assert.StartsWith("[Masked:", ResultOf(masked.Take(4).ToList(), "c1"));
    }

    [Fact]
    public void Config_Carries_The_Budget_Into_The_Context_Manager()
    {
        var counter = new ContextTokenCounter("gpt-4o");
        var perResult = counter.CountTokens($"section 1: {LongResult}");
        var manager = ContextManager.FromConfig(counter, new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedTokens = perResult * 2 + perResult / 2,
            EnableToolResultCompaction = false,
        });

        var reduced = manager.ReduceToolResults(OneTurnWithRounds(4));

        Assert.StartsWith("[Masked:", ResultOf(reduced, "c2"));
        Assert.StartsWith("section 3:", ResultOf(reduced, "c3"));
        Assert.StartsWith("section 4:", ResultOf(reduced, "c4"));
    }

    [Fact]
    public async Task Each_Tool_Round_Of_A_Turn_Is_Sent_With_Older_Results_Masked()
    {
        var manager = TwoResultBudgetManager();
        var model = new ReadingModel(sections: 5);
        var client = new ChatClientBuilder(model).UseFunctionInvocation().UseToolRoundContext(manager).Build();
        var readSection = AIFunctionFactory.Create((int n) => $"section {n}: {LongResult}", "read_section");
        var loop = new AgentLoop(client, new AgentOptions { Tools = [readSection] }, contextManager: manager);

        var response = await loop.RunAsync("Read every section and take notes.", TestContext.Current.CancellationToken);

        Assert.Equal("done", response.Content);
        // The last request (after the 5th round) carries rounds 4-5 in full and rounds 1-3 as placeholders.
        var last = model.Requests[^1];
        var results = last.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result!.ToString()!).ToList();
        Assert.Equal(5, results.Count);
        Assert.All(results.Take(3), r => Assert.StartsWith("[Masked: read_section {\"n\":", r));
        Assert.All(results.Skip(3), r => Assert.StartsWith("section", r));
        // The loop's own history keeps the full results; only what was sent is reduced.
        Assert.All(loop.History.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => Assert.StartsWith("section", r.Result!.ToString()));
    }

    [Fact]
    public async Task A_Factory_Built_Pipeline_Is_Bound_By_The_Loop()
    {
        // The pipeline exists before the loop's manager (a chat client factory's decorator): unbound, then the loop binds
        // its own manager when it is constructed.
        var manager = TwoResultBudgetManager();
        var model = new ReadingModel(sections: 5);
        var client = new ChatClientBuilder(model).UseFunctionInvocation().UseToolRoundContext().Build();
        var readSection = AIFunctionFactory.Create((int n) => $"section {n}: {LongResult}", "read_section");
        var loop = new AgentLoop(client, new AgentOptions { Tools = [readSection] }, contextManager: manager);

        await loop.RunAsync("Read every section and take notes.", TestContext.Current.CancellationToken);

        Assert.Same(manager, client.GetService<ToolRoundContextChatClient>()!.ContextManager);
        var results = model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result!.ToString()!).ToList();
        Assert.All(results.Take(3), r => Assert.StartsWith("[Masked:", r));
        Assert.All(results.Skip(3), r => Assert.StartsWith("section", r));
    }

    [Fact]
    public async Task An_Unbound_Client_Passes_Requests_Through()
    {
        var model = new ReadingModel(sections: 1);
        var client = new ChatClientBuilder(model).UseToolRoundContext().Build();
        var history = OneTurnWithRounds(3);

        await client.GetResponseAsync(history, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(history, model.Requests[^1]);
    }

    [Fact]
    public void A_Pipeline_Bound_To_One_Loop_Refuses_A_Second_Loops_Manager()
    {
        var client = new ChatClientBuilder(new ReadingModel(sections: 1)).UseFunctionInvocation().UseToolRoundContext().Build();
        _ = new AgentLoop(client, contextManager: ContextManager.ForModel("gpt-4o"));

        Assert.Throws<InvalidOperationException>(() => new AgentLoop(client, contextManager: ContextManager.ForModel("gpt-4o")));
    }

    /// <summary>A manager whose budget holds two section results.</summary>
    private static ContextManager TwoResultBudgetManager()
    {
        var counter = new ContextTokenCounter("gpt-4o");
        var perResult = counter.CountTokens($"section 1: {LongResult}");
        return ContextManager.FromConfig(counter, new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedTokens = perResult * 2 + perResult / 2,
            EnableToolResultCompaction = false,
        });
    }

    /// <summary>Calls read_section once per round until it has read them all, then answers.</summary>
    private sealed class ReadingModel(int sections) : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Requests.Add(list);
            var read = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            var reply = read < sections
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{read + 1}", "read_section",
                    new Dictionary<string, object?> { ["n"] = read + 1 })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CharTokenCounter : IContextTokenCounter
    {
        public string ModelName => "chars";
        public int MaxContextTokens => 1_000_000;
        public int CountTokens(ChatMessage message) => message.Text?.Length ?? 0;
        public int CountTokens(IEnumerable<ChatMessage> messages) => messages.Sum(CountTokens);
        public int CountTokens(string text) => text.Length;
    }
}
