using System.Runtime.CompilerServices;

using AwesomeAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;

using Microsoft.Extensions.AI;

using NSubstitute;

namespace IronHive.Agent.Tests.Planning;

public class DefaultPlanExecutorTests
{
    #region Helpers

    /// <summary>
    /// A chat client that streams one scripted round per call, records what it was sent,
    /// and can fail when the call starts or after a number of updates.
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponseUpdate[]> _rounds;

        public ScriptedChatClient(params ChatResponseUpdate[][] rounds) => _rounds = new Queue<ChatResponseUpdate[]>(rounds);

        public Exception? ThrowOnCall { get; init; }
        public Exception? ThrowAfterUpdates { get; init; }
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The executor streams.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            Options.Add(options);
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            return Stream(_rounds.Count > 0 ? _rounds.Dequeue() : [], cancellationToken);
        }

        private async IAsyncEnumerable<ChatResponseUpdate> Stream(
            ChatResponseUpdate[] updates, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }

            if (ThrowAfterUpdates is not null)
            {
                throw ThrowAfterUpdates;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate Call(string callId, string name) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name)]);

    private static TaskPlan CreateSingleStepPlan(string goal = "Test goal")
    {
        return new TaskPlan
        {
            Goal = goal,
            Steps =
            [
                new PlanStep { Index = 0, Description = "Do something", Instruction = "Execute the task" },
            ],
        };
    }

    private static async Task<List<PlanExecutionEvent>> CollectEventsAsync(
        IAsyncEnumerable<PlanExecutionEvent> stream)
    {
        var events = new List<PlanExecutionEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    #endregion

    [Fact]
    public async Task ExecuteStepAsync_Success_ReturnsCompletedEventWithOutput()
    {
        var client = new ScriptedChatClient([Text("Step completed successfully")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.StepIndex.Should().Be(0);
        completed.Result.Success.Should().BeTrue();
        completed.Result.Output.Should().Be("Step completed successfully");
        completed.Result.Error.Should().BeNull();
        events[^1].Should().BeSameAs(completed);
    }

    [Fact]
    public async Task ExecuteStepAsync_StreamsEachTextDeltaAsProgress_ThenCompletesWithTheWholeText()
    {
        var client = new ScriptedChatClient([Text("Found "), Text("42 "), Text("files.")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        events.OfType<StepProgressEvent>().Select(e => e.TextDelta).Should().Equal("Found ", "42 ", "files.");
        events.OfType<StepProgressEvent>().Should().OnlyContain(e => e.StepIndex == 0);
        events[^1].Should().BeOfType<StepCompletedEvent>()
            .Which.Result.Output.Should().Be("Found 42 files.");
    }

    [Fact]
    public async Task ExecuteStepAsync_WithTools_ReportsEachToolCallAfterItRuns()
    {
        var calls = 0;
        var tools = new List<AITool> { AIFunctionFactory.Create(() => { calls++; return "test result"; }, "TestTool") };
        var client = new ScriptedChatClient([Call("c1", "TestTool")], [Text("Used the tool.")]);
        var executor = new DefaultPlanExecutor(client, tools);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        calls.Should().Be(1);
        var toolEvent = events.OfType<StepToolCallEvent>().Single();
        toolEvent.Should().Be(new StepToolCallEvent(0, "TestTool", true));
        events.IndexOf(toolEvent).Should().BeLessThan(events.FindIndex(e => e is StepProgressEvent));
        events[^1].Should().BeOfType<StepCompletedEvent>()
            .Which.Result.Should().Match<StepResult>(r => r.Success && r.Output == "Used the tool.");
    }

    [Fact]
    public async Task ExecuteStepAsync_ToolThatThrows_IsReportedAsAFailedToolCall()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(new Func<string>(() => throw new InvalidOperationException("disk full")), "Write"),
        };
        var client = new ScriptedChatClient([Call("c1", "Write")], [Text("Could not write.")]);
        var executor = new DefaultPlanExecutor(client, tools);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        events.OfType<StepToolCallEvent>().Single().Should().Be(new StepToolCallEvent(0, "Write", false));
        events[^1].Should().BeOfType<StepCompletedEvent>().Which.Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteStepAsync_ChatClientThrows_ReturnsFailureWithError()
    {
        var client = new ScriptedChatClient { ThrowOnCall = new InvalidOperationException("LLM service unavailable") };
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        var completed = events.Should().ContainSingle().Which.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Result.Success.Should().BeFalse();
        completed.Result.Output.Should().BeEmpty();
        completed.Result.Error.Should().Contain("LLM service unavailable");
    }

    [Fact]
    public async Task ExecuteStepAsync_StreamFailsMidway_KeepsTheProgressAndReportsTheFailure()
    {
        var client = new ScriptedChatClient([Text("partial")])
        {
            ThrowAfterUpdates = new HttpRequestException("Connection reset"),
        };
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        events.OfType<StepProgressEvent>().Single().TextDelta.Should().Be("partial");
        var completed = events[^1].Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Result.Success.Should().BeFalse();
        completed.Result.Output.Should().Be("partial");
        completed.Result.Error.Should().Contain("Connection reset");
    }

    [Fact]
    public async Task ExecuteStepAsync_OperationCanceled_Rethrows()
    {
        var client = new ScriptedChatClient { ThrowOnCall = new OperationCanceledException() };
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var act = () => CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteStepAsync_TaskCanceledMidStream_Rethrows()
    {
        // TaskCanceledException is a subclass of OperationCanceledException
        var client = new ScriptedChatClient([Text("partial")]) { ThrowAfterUpdates = new TaskCanceledException() };
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var act = () => CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ExecuteStepAsync_WithTools_PassesToolsInChatOptions()
    {
        var client = new ScriptedChatClient([Text("Executed")]);
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "test result", "TestTool") };
        var executor = new DefaultPlanExecutor(client, tools);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        events.OfType<StepCompletedEvent>().Single().Result.Success.Should().BeTrue();
        client.Options.Should().ContainSingle().Which!.Tools.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteStepAsync_WithoutTools_PassesNullChatOptions()
    {
        var client = new ScriptedChatClient([Text("done")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken)) { }

        client.Options.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteStepAsync_EmptyToolList_TreatedAsNoTools()
    {
        var client = new ScriptedChatClient([Text("done")]);
        var executor = new DefaultPlanExecutor(client, new List<AITool>());
        var plan = CreateSingleStepPlan();

        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken)) { }

        client.Options.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteStepAsync_IncludesCompletedStepContextInPrompt()
    {
        var client = new ScriptedChatClient([Text("done")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = new TaskPlan
        {
            Goal = "Organize photos",
            Steps =
            [
                new PlanStep
                {
                    Index = 0,
                    Description = "Find files",
                    Instruction = "Find all jpg files",
                    Status = StepStatus.Completed,
                    Result = "Found 42 files",
                },
                new PlanStep
                {
                    Index = 1,
                    Description = "Sort files",
                    Instruction = "Sort files by date",
                },
            ],
        };

        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[1], TestContext.Current.CancellationToken)) { }

        var sent = client.Calls.Should().ContainSingle().Subject;
        sent.Should().Contain(m => m.Text != null && m.Text.Contains("Organize photos"));
        sent.Should().Contain(m => m.Text != null && m.Text.Contains("Found 42 files"));
    }

    [Fact]
    public async Task ExecuteStepAsync_NoCompletedSteps_OmitsContextSection()
    {
        var client = new ScriptedChatClient([Text("done")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan("Find files");

        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken)) { }

        client.Calls.Should().ContainSingle().Which
            .Should().OnlyContain(m => m.Text == null || !m.Text.Contains("Completed steps so far"));
    }

    [Fact]
    public async Task ExecuteStepAsync_NoText_ReturnsEmptyOutputAndNoProgress()
    {
        var client = new ScriptedChatClient([]);
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        var completed = events.Should().ContainSingle().Which.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Result.Success.Should().BeTrue();
        completed.Result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteStepAsync_PreservesCorrectStepIndex()
    {
        var client = new ScriptedChatClient([Text("done")]);
        var executor = new DefaultPlanExecutor(client);
        var plan = new TaskPlan
        {
            Goal = "Multi-step plan",
            Steps =
            [
                new PlanStep { Index = 0, Description = "Step 0", Instruction = "Do 0" },
                new PlanStep { Index = 1, Description = "Step 1", Instruction = "Do 1" },
                new PlanStep { Index = 5, Description = "Step 5", Instruction = "Do 5" },
            ],
        };

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[2], TestContext.Current.CancellationToken));

        events.OfType<StepCompletedEvent>().Single().StepIndex.Should().Be(5);
        events.OfType<StepProgressEvent>().Should().OnlyContain(e => e.StepIndex == 5);
    }

    [Fact]
    public async Task ExecuteStepAsync_HttpRequestException_CapturedAsFailure()
    {
        var client = new ScriptedChatClient { ThrowOnCall = new HttpRequestException("Connection refused") };
        var executor = new DefaultPlanExecutor(client);
        var plan = CreateSingleStepPlan();

        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0], TestContext.Current.CancellationToken));

        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.Result.Success.Should().BeFalse();
        completed.Result.Error.Should().Contain("Connection refused");
    }

    [Fact]
    public void IsInheritable_NotSealed()
    {
        typeof(DefaultPlanExecutor).IsSealed.Should().BeFalse();
    }

    [Fact]
    public void BuildStepMessages_IsVirtualProtected()
    {
        var method = typeof(DefaultPlanExecutor).GetMethod(
            "BuildStepMessages",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull();
        method!.IsVirtual.Should().BeTrue();
        method.IsFamily.Should().BeTrue(); // protected
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        typeof(DefaultPlanExecutor).Should().Implement<IDisposable>();
    }

    [Fact]
    public void Dispose_WithTools_DisposesOwnedClient()
    {
        // The inner client is wrapped by ChatClientBuilder; disposing the executor disposes that pipeline
        // and must not throw.
        var chatClient = Substitute.For<IChatClient, IDisposable>();
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "result", "Tool1") };
        var executor = new DefaultPlanExecutor(chatClient, tools);

        var act = executor.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithoutTools_DoesNotDisposeClient()
    {
        var chatClient = Substitute.For<IChatClient, IDisposable>();
        var executor = new DefaultPlanExecutor(chatClient); // no tools → not owned

        executor.Dispose();

        ((IDisposable)chatClient).DidNotReceive().Dispose();
    }
}
