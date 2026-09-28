using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// One user message followed by many tool rounds (read a long document section by section) is a single user turn, so
/// protecting recent user turns protects every result of that turn. <see cref="CompactionConfig.ObservationMaskingProtectedRounds"/>
/// masks older rounds inside it, and <see cref="ToolRoundContextChatClient"/> applies that to each model call of the turn.
/// </summary>
public class ToolRoundMaskingTests
{
    private static readonly string LongResult = new('x', 2_000);

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

    [Fact]
    public void Protected_Rounds_Mask_Older_Rounds_Inside_One_Turn()
    {
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200, protectedRounds: 2);

        var masked = masker.MaskObservations(OneTurnWithRounds(5));

        Assert.StartsWith("[Masked: read_section result", ResultOf(masked, "c1"));
        Assert.StartsWith("[Masked: read_section result", ResultOf(masked, "c3"));
        Assert.StartsWith("section 4:", ResultOf(masked, "c4"));
        Assert.StartsWith("section 5:", ResultOf(masked, "c5"));
        // The calls and the user message stay: the model still knows what it read.
        Assert.Equal(5, masked.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(ChatRole.User, masked[0].Role);
    }

    [Fact]
    public void Without_Protected_Rounds_One_Turn_Is_Never_Masked()
    {
        // Positive control: the documented user-turn rule alone protects the whole turn.
        var masker = new ObservationMasker(protectedTurns: 2, minimumResultLength: 200);

        var history = OneTurnWithRounds(5);

        Assert.Same(history, masker.MaskObservations(history));
    }

    [Fact]
    public void Protected_Rounds_Do_Not_Unmask_What_The_Turn_Rule_Masks()
    {
        // Two turns; the older one is outside the protected turn, and stays masked regardless of rounds.
        var history = OneTurnWithRounds(1);
        history.Add(new ChatMessage(ChatRole.Assistant, "noted"));
        history.AddRange(OneTurnWithRounds(1).Select(m => m.Role == ChatRole.User ? new ChatMessage(ChatRole.User, "next") : m));
        var masker = new ObservationMasker(protectedTurns: 1, minimumResultLength: 200, protectedRounds: 5);

        var masked = masker.MaskObservations(history);

        Assert.StartsWith("[Masked:", ResultOf(masked.Take(4).ToList(), "c1"));
    }

    [Fact]
    public void Config_Carries_Protected_Rounds_Into_The_Context_Manager()
    {
        var manager = ContextManager.ForModel("gpt-4o", new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedRounds = 2,
            EnableToolResultCompaction = false,
        });

        var reduced = manager.ReduceToolResults(OneTurnWithRounds(4));

        Assert.StartsWith("[Masked:", ResultOf(reduced, "c1"));
        Assert.StartsWith("section 4:", ResultOf(reduced, "c4"));
    }

    [Fact]
    public async Task Each_Tool_Round_Of_A_Turn_Is_Sent_With_Older_Results_Masked()
    {
        var manager = ContextManager.ForModel("gpt-4o", new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedRounds = 2,
            EnableToolResultCompaction = false,
        });
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
        Assert.All(results.Take(3), r => Assert.StartsWith("[Masked:", r));
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
        var manager = ContextManager.ForModel("gpt-4o", new CompactionConfig
        {
            EnableObservationMasking = true,
            ObservationMaskingProtectedTurns = 2,
            ObservationMaskingProtectedRounds = 2,
            EnableToolResultCompaction = false,
        });
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
}
