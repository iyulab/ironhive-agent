using IronHive.Agent.Context;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// The goal reminder is composed for one preparation: at most one in any prepared history, pointing at the request the
/// current turn serves, and never read as the user's request. Before, one was appended per model call and kept, it named
/// the session's first question, and tool retrieval queried it instead of the user's message.
/// </summary>
public class GoalReminderAccumulationTests
{
    private static List<ChatMessage> ThreeTurnChat() =>
    [
        new(ChatRole.System, "You are a file assistant."),
        new(ChatRole.User, "What is the project codename in notes.txt?"),
        new(ChatRole.Assistant, "It is Aurora."),
        new(ChatRole.User, "Read server.pem"),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "ReadFile")]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "-----BEGIN CERTIFICATE-----")]),
        new(ChatRole.Assistant, "That is a certificate."),
        new(ChatRole.User, "Use GrepFiles to search for PROBE_TOKEN"),
    ];

    private static ContextManager Manager(GoalReminderOptions? reminder = null) => ContextManager.ForModel("gpt-4o", new CompactionConfig
    {
        UseTokenBasedCompaction = false,
        EnableObservationMasking = false,
        EnableToolResultCompaction = false,
        GoalReminder = reminder,
    });

    private static IEnumerable<ChatMessage> Reminders(IEnumerable<ChatMessage> history)
        => history.Where(m => m.Text?.StartsWith("[REMINDER]", StringComparison.Ordinal) == true);

    [Fact]
    public async Task Preparing_Its_Own_Output_Again_Keeps_One_Reminder_For_The_Latest_Request()
    {
        var manager = Manager();
        var history = ThreeTurnChat();
        manager.SetGoalFromHistory(history);

        IReadOnlyList<ChatMessage> prepared = history;
        for (var call = 0; call < 3; call++)
        {
            prepared = await manager.PrepareHistoryAsync(prepared, TestContext.Current.CancellationToken);
        }

        var reminder = Assert.Single(Reminders(prepared));
        Assert.Equal("[REMINDER] Current goal: Use GrepFiles to search for PROBE_TOKEN", reminder.Text);
        Assert.True(ContextManager.IsInjected(reminder));
    }

    [Fact]
    public async Task The_Reminder_Is_Never_Read_As_The_Request()
    {
        var manager = Manager();
        var history = ThreeTurnChat();
        manager.SetGoalFromHistory(history);
        var prepared = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        // Setting the goal again from the prepared history (as the next turn does) still finds the user's request.
        manager.SetGoalFromHistory(prepared);
        var again = await manager.PrepareHistoryAsync(prepared, TestContext.Current.CancellationToken);

        Assert.Equal("[REMINDER] Current goal: Use GrepFiles to search for PROBE_TOKEN", Assert.Single(Reminders(again)).Text);
    }

    [Fact]
    public async Task Tool_Retrieval_Queries_The_Users_Request_Not_The_Reminder()
    {
        var retriever = new RecordingRetriever();
        var manager = Manager();
        var loop = new AgentLoop(new EchoClient(),
            new AgentOptions { Tools = [AIFunctionFactory.Create(() => "ok", "GrepFiles")] },
            contextManager: manager, toolRetriever: retriever);
        var history = ThreeTurnChat();
        loop.InitializeHistory(history.Take(history.Count - 1));

        await loop.RunAsync("Use GrepFiles to search for PROBE_TOKEN", TestContext.Current.CancellationToken);
        await loop.RunAsync("And in docs/ too", TestContext.Current.CancellationToken);

        Assert.Equal(["Use GrepFiles to search for PROBE_TOKEN", "And in docs/ too"], retriever.Queries);
        Assert.Single(Reminders(loop.History));
    }

    [Fact]
    public async Task Config_Can_Turn_The_Reminder_Off()
    {
        var manager = Manager(new GoalReminderOptions { Enabled = false });
        var history = ThreeTurnChat();
        manager.SetGoalFromHistory(history);

        var prepared = await manager.PrepareHistoryAsync(history, TestContext.Current.CancellationToken);

        Assert.Empty(Reminders(prepared));
    }

    private sealed class RecordingRetriever : IToolRetriever
    {
        public List<string> Queries { get; } = [];

        public Task<ToolRetrievalResult> RetrieveAsync(
            string query, IList<AITool> availableTools, ToolRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new ToolRetrievalResult { SelectedTools = availableTools });
        }
    }

    private sealed class EchoClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
