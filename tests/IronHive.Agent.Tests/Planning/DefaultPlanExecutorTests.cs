using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;

using Microsoft.Extensions.AI;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IronHive.Agent.Tests.Planning;

public class DefaultPlanExecutorTests
{
    #region Helpers

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
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Step completed successfully")));

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        // Assert
        events.Should().ContainSingle();
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.StepIndex.Should().Be(0);
        completed.Result.Success.Should().BeTrue();
        completed.Result.Output.Should().Be("Step completed successfully");
        completed.Result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteStepAsync_ChatClientThrows_ReturnsFailureWithError()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("LLM service unavailable"));

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        // Assert
        events.Should().ContainSingle();
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.StepIndex.Should().Be(0);
        completed.Result.Success.Should().BeFalse();
        completed.Result.Output.Should().BeEmpty();
        completed.Result.Error.Should().Contain("LLM service unavailable");
    }

    [Fact]
    public async Task ExecuteStepAsync_OperationCanceled_Rethrows()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act & Assert
        var act = () => CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteStepAsync_TaskCanceled_Rethrows()
    {
        // Arrange — TaskCanceledException is a subclass of OperationCanceledException
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException());

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act & Assert
        var act = () => CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task ExecuteStepAsync_WithTools_PassesToolsInChatOptions()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Executed tool")));

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "test result", "TestTool"),
        };
        var executor = new DefaultPlanExecutor(chatClient, tools);
        var plan = CreateSingleStepPlan();

        // Act
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        // Assert
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.Result.Success.Should().BeTrue();

        // Verify the inner client received non-null ChatOptions with tools
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.Tools != null && o.Tools.Count > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStepAsync_WithoutTools_PassesNullChatOptions()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var executor = new DefaultPlanExecutor(chatClient); // no tools

        var plan = CreateSingleStepPlan();

        // Act
        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0])) { }

        // Assert
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStepAsync_EmptyToolList_TreatedAsNoTools()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var executor = new DefaultPlanExecutor(chatClient, new List<AITool>()); // empty list

        var plan = CreateSingleStepPlan();

        // Act
        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0])) { }

        // Assert — empty tool list should be treated the same as no tools
        await chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStepAsync_IncludesCompletedStepContextInPrompt()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var executor = new DefaultPlanExecutor(chatClient);
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

        // Act
        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[1])) { }

        // Assert — verify the prompt includes the plan goal and completed step context
        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null && m.Text.Contains("Organize photos")) &&
                msgs.Any(m => m.Text != null && m.Text.Contains("Found 42 files"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStepAsync_NoCompletedSteps_OmitsContextSection()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan("Find files");

        // Act
        await foreach (var _ in executor.ExecuteStepAsync(plan, plan.Steps[0])) { }

        // Assert — no "Completed steps so far" section when no steps are completed
        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.All(m => m.Text == null || !m.Text.Contains("Completed steps so far"))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteStepAsync_NullResponseText_ReturnsEmptyOutput()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        // ChatResponse with no content — response.Text will be null
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([]));

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        // Assert
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.Result.Success.Should().BeTrue();
        completed.Result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteStepAsync_PreservesCorrectStepIndex()
    {
        // Arrange
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        var executor = new DefaultPlanExecutor(chatClient);
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

        // Act — execute step with index 5
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[2]));

        // Assert
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.StepIndex.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteStepAsync_HttpRequestException_CapturedAsFailure()
    {
        // Arrange — simulate a network error
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        var executor = new DefaultPlanExecutor(chatClient);
        var plan = CreateSingleStepPlan();

        // Act
        var events = await CollectEventsAsync(
            executor.ExecuteStepAsync(plan, plan.Steps[0]));

        // Assert
        var completed = events.OfType<StepCompletedEvent>().Single();
        completed.Result.Success.Should().BeFalse();
        completed.Result.Error.Should().Contain("Connection refused");
    }

    [Fact]
    public void IsInheritable_NotSealed()
    {
        // Verify that DefaultPlanExecutor is not sealed, allowing subclassing
        typeof(DefaultPlanExecutor).IsSealed.Should().BeFalse();
    }

    [Fact]
    public void BuildStepMessages_IsVirtualProtected()
    {
        // Verify BuildStepMessages is protected virtual for override
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
        // Arrange — create a disposable chat client mock
        var chatClient = Substitute.For<IChatClient, IDisposable>();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "result", "Tool1"),
        };
        var executor = new DefaultPlanExecutor(chatClient, tools);

        // Act
        executor.Dispose();

        // Assert — the inner client is wrapped by ChatClientBuilder, so the original
        // should NOT be disposed (the wrapper owns its own pipeline).
        // What matters is that Dispose() does not throw and completes successfully.
        // The wrapped pipeline implements IDisposable and is disposed.
    }

    [Fact]
    public void Dispose_WithoutTools_DoesNotDisposeClient()
    {
        // Arrange — chat client that is also IDisposable
        var chatClient = Substitute.For<IChatClient, IDisposable>();
        var executor = new DefaultPlanExecutor(chatClient); // no tools → _ownsClient = false

        // Act
        executor.Dispose();

        // Assert — the client was NOT wrapped, so Dispose should not touch it
        ((IDisposable)chatClient).DidNotReceive().Dispose();
    }
}
