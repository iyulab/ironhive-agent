using IndexThinking.Reduction;
using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// <see cref="CompactedResultTool"/>: with the tool registered, a masked or shortened tool result is read back by id
/// instead of by repeating the call; without it, nothing is kept and the placeholders are unchanged.
/// </summary>
public class CompactedResultToolTests
{
    private static readonly string LongResult = new('x', 2_000);

    private static List<ChatMessage> OneTurnWithRounds(int rounds, string toolName = "send_report")
    {
        var history = new List<ChatMessage> { new(ChatRole.User, "Send every section and note what came back.") };
        for (var r = 1; r <= rounds; r++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{r}", toolName,
                new Dictionary<string, object?> { ["section"] = r })]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{r}", $"section {r}: {LongResult}")]));
        }

        return history;
    }

    private static string ResultOf(IReadOnlyList<ChatMessage> history, string callId)
        => history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == callId).Result!.ToString()!;

    /// <summary>Masks every result but the newest two inside one user turn (a budget of ~two results).</summary>
    private static ContextManager MaskingManager(ToolResultCompactor? compactor = null) => new(
        new CharTokenCounter(),
        toolResultCompactor: compactor,
        observationMasker: new ToolResultMasker(protectedTurns: 2, minimumResultLength: 200, protectedTokens: 4_100, tokenCounter: new ContextTokenCounterAdapter(new CharTokenCounter())));

    private static async Task<string> RetrieveAsync(AIFunction tool, string id, int? offset = null)
    {
        var arguments = new AIFunctionArguments { ["id"] = id };
        if (offset is { } o)
        {
            arguments["offset"] = o;
        }

        return (await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken))!.ToString()!;
    }

    [Fact]
    public async Task A_Result_Masked_Inside_The_Turn_Is_Read_Back_Without_Repeating_A_Side_Effect_Call()
    {
        var manager = MaskingManager();
        var tool = CompactedResultTool.Create(manager);

        // Per-request reduction, as ToolRoundContextChatClient runs it between the rounds of one turn — before any of
        // these results reach the loop's history.
        var reduced = manager.ReduceToolResults(OneTurnWithRounds(5));

        var placeholder = ResultOf(reduced, "c1");
        Assert.StartsWith("[Masked: send_report", placeholder);
        Assert.Contains("call retrieve_tool_result with id \"c1\"", placeholder);
        Assert.DoesNotContain("again with the same arguments", placeholder);
        Assert.Equal($"section 1: {LongResult}", await RetrieveAsync(tool, "c1"));
    }

    [Fact]
    public void Without_The_Tool_The_Placeholder_Still_Asks_For_The_Same_Call()
    {
        // Positive control: the same manager and history, no tool — the placeholder text is the one before this feature.
        var reduced = MaskingManager().ReduceToolResults(OneTurnWithRounds(5));

        var placeholder = ResultOf(reduced, "c1");
        Assert.Contains("call send_report again with the same arguments", placeholder);
        Assert.DoesNotContain(CompactedResultTool.ToolName, placeholder);
    }

    [Fact]
    public async Task A_Shortened_Result_Is_Read_Whole_In_Parts()
    {
        var original = string.Concat(Enumerable.Range(0, 2_500).Select(i => (char)('a' + (i % 26))));
        var manager = new ContextManager(new CharTokenCounter(), toolResultCompactor: new ToolResultCompactor(maxResultChars: 1_000));
        var tool = CompactedResultTool.Create(manager);
        List<ChatMessage> history =
        [
            new(ChatRole.User, "Fetch it."),
            new(ChatRole.Assistant, [new FunctionCallContent("f1", "fetch")]),
            new(ChatRole.Tool, [new FunctionResultContent("f1", original)]),
        ];

        var shortened = ResultOf(manager.ReduceToolResults(history), "f1");
        Assert.Contains("[Shortened. Call retrieve_tool_result with id \"f1\" to read the whole result.]", shortened);

        var first = await RetrieveAsync(tool, "f1");
        Assert.StartsWith(original[..1_000], first);
        Assert.Contains("offset 1000 for the rest", first);
        var second = await RetrieveAsync(tool, "f1", 1_000);
        Assert.StartsWith(original[1_000..2_000], second);
        var last = await RetrieveAsync(tool, "f1", 2_000);
        Assert.StartsWith(original[2_000..], last);
        Assert.Contains("the end of the result", last);
    }

    [Fact]
    public async Task A_Result_Shortened_And_Then_Masked_Keeps_The_Whole_Original()
    {
        var manager = MaskingManager(new ToolResultCompactor(maxResultChars: 1_000));
        var tool = CompactedResultTool.Create(manager);

        var reduced = manager.ReduceToolResults(OneTurnWithRounds(5));

        Assert.Contains("id \"c1\"", ResultOf(reduced, "c1"));
        var read = await RetrieveAsync(tool, "c1");
        Assert.StartsWith("section 1: ", read);
        Assert.Contains("of 2,011", read);
    }

    [Fact]
    public void A_Result_Without_A_Call_Id_Gets_The_Same_Id_On_Every_Request()
    {
        var manager = MaskingManager();
        CompactedResultTool.Create(manager);
        var history = OneTurnWithRounds(5);
        history[2] = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(string.Empty, $"section 1: {LongResult}")]);

        string FirstResult(IReadOnlyList<ChatMessage> h) => h[2].Contents.OfType<FunctionResultContent>().Single().Result!.ToString()!;

        var first = FirstResult(manager.ReduceToolResults(history));
        var second = FirstResult(manager.ReduceToolResults(history));
        Assert.Contains("with id \"r-", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task An_Unknown_Id_Is_Answered_With_What_The_Ids_Are()
    {
        var tool = CompactedResultTool.Create(MaskingManager());

        var answer = await RetrieveAsync(tool, "nope");

        Assert.Contains("No kept result has the id \"nope\"", answer);
    }

    [Fact]
    public async Task Clearing_The_Loop_History_Forgets_The_Kept_Results()
    {
        var manager = MaskingManager();
        var tool = CompactedResultTool.Create(manager);
        var loop = new AgentLoop(new MockChatClient(), contextManager: manager);
        manager.ReduceToolResults(OneTurnWithRounds(5));
        Assert.StartsWith("section 1:", await RetrieveAsync(tool, "c1"));

        loop.ClearHistory();

        Assert.Contains("No kept result", await RetrieveAsync(tool, "c1"));
    }

    [Fact]
    public void The_Tool_Is_Read_Only_For_Planning_Mode_And_Approval_Rules()
    {
        Assert.True(new PermissionEvaluator(new PermissionConfig()).IsReadOnlyTool(CompactedResultTool.ToolName));
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
