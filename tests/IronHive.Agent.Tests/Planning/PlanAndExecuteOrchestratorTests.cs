using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;

using NSubstitute;

namespace IronHive.Agent.Tests.Planning;

public class PlanAndExecuteOrchestratorTests
{
    private readonly ITaskPlanner _planner = Substitute.For<ITaskPlanner>();
    private readonly IPlanExecutor _executor = Substitute.For<IPlanExecutor>();
    private readonly IPlanEvaluator _evaluator = Substitute.For<IPlanEvaluator>();

    #region Helpers

    private static async IAsyncEnumerable<PlanExecutionEvent> ToAsyncEnumerable(
        params PlanExecutionEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
        }

        await Task.CompletedTask;
    }

    private static TaskPlan CreatePlan(string goal, params PlanStep[] steps)
    {
        return new TaskPlan
        {
            Goal = goal,
            Steps = steps.ToList(),
        };
    }

    private static PlanStep CreateStep(int index, string description, int[]? dependsOn = null)
    {
        return new PlanStep
        {
            Index = index,
            Description = description,
            Instruction = $"Do {description}",
            DependsOn = dependsOn ?? [],
        };
    }

    private PlanAndExecuteOrchestrator CreateOrchestrator(PlanAndExecuteOptions? options = null)
    {
        return new PlanAndExecuteOrchestrator(_planner, _executor, _evaluator, options);
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
    public async Task ExecuteAsync_HappyPath_CompletesAllSteps()
    {
        // Arrange
        var step0 = CreateStep(0, "First step");
        var step1 = CreateStep(1, "Second step");
        var plan = CreatePlan("Test goal", step0, step1);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Test goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result0 = new StepResult { Success = true, Output = "Step 0 done" };
        var result1 = new StepResult { Success = true, Output = "Step 1 done" };

        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result0)));

        _executor.ExecuteStepAsync(plan, step1, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(1, result1)));

        _evaluator.EvaluateAsync(plan, step0, result0, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });
        _evaluator.EvaluateAsync(plan, step1, result1, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Test goal", context));

        // Assert — PlanCreated + 2x(StepStarted + StepCompleted) + PlanCompleted = 6
        events.Should().HaveCount(6);
        events[0].Should().BeOfType<PlanCreatedEvent>();
        events[1].Should().BeOfType<StepStartedEvent>().Which.StepIndex.Should().Be(0);
        events[2].Should().BeOfType<StepCompletedEvent>().Which.StepIndex.Should().Be(0);
        events[3].Should().BeOfType<StepStartedEvent>().Which.StepIndex.Should().Be(1);
        events[4].Should().BeOfType<StepCompletedEvent>().Which.StepIndex.Should().Be(1);
        events[5].Should().BeOfType<PlanCompletedEvent>();

        // PlanCompletedEvent summary should mention both steps
        var completed = (PlanCompletedEvent)events[5];
        completed.Summary.Should().Contain("2/2");
    }

    [Fact]
    public async Task ExecuteAsync_StepFails_TriggersReplan()
    {
        // Arrange — first plan has one step that fails, evaluator says Replan,
        //   then new plan has one step that succeeds.
        var failStep = CreateStep(0, "Failing step");
        var failPlan = CreatePlan("Test goal", failStep);

        var successStep = CreateStep(0, "Fixed step");
        var successPlan = CreatePlan("Test goal", successStep);

        var context = new PlanningContext();

        _planner.CreatePlanAsync("Test goal", context, Arg.Any<CancellationToken>())
            .Returns(failPlan);

        var failResult = new StepResult { Success = false, Output = "Error occurred" };
        _executor.ExecuteStepAsync(failPlan, failStep, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        _evaluator.EvaluateAsync(failPlan, failStep, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Replan, Reason = "Step failed" });

        _planner.ReplanAsync(failPlan, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(successPlan);

        var successResult = new StepResult { Success = true, Output = "Fixed" };
        _executor.ExecuteStepAsync(successPlan, successStep, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, successResult)));

        _evaluator.EvaluateAsync(successPlan, successStep, successResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Test goal", context));

        // Assert
        events.Should().ContainSingle(e => e is PlanReplanEvent);
        events.Should().ContainSingle(e => e is PlanCompletedEvent);
        events.Should().NotContain(e => e is PlanAbortedEvent);
    }

    [Fact]
    public async Task ExecuteAsync_ExceedsMaxReplans_Aborts()
    {
        // Arrange — always fails, evaluator always says Replan, maxReplans = 2
        var options = new PlanAndExecuteOptions { MaxReplans = 2 };
        var context = new PlanningContext();

        var failResult = new StepResult { Success = false, Output = "Still failing" };

        // Create plans for initial + 2 replans + 1 more (which should trigger abort)
        var plans = Enumerable.Range(0, 4).Select(i =>
        {
            var step = CreateStep(0, $"Step attempt {i}");
            return CreatePlan("Test goal", step);
        }).ToList();

        _planner.CreatePlanAsync("Test goal", context, Arg.Any<CancellationToken>())
            .Returns(plans[0]);

        // Set up replan returns for each successive attempt
        _planner.ReplanAsync(Arg.Any<TaskPlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(plans[1], plans[2], plans[3]);

        // All executions fail
        _executor.ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        // Evaluator always says Replan
        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Replan, Reason = "Failed" });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Test goal", context));

        // Assert — should have 2 replans then abort
        events.OfType<PlanReplanEvent>().Should().HaveCount(2);
        events.Last().Should().BeOfType<PlanAbortedEvent>();
        var aborted = (PlanAbortedEvent)events.Last();
        aborted.Reason.Should().Contain("maximum replan");
    }

    [Fact]
    public async Task ExecuteAsync_EvaluatorAborts_StopsImmediately()
    {
        // Arrange — 3-step plan, evaluator aborts on first step
        var step0 = CreateStep(0, "Step 0");
        var step1 = CreateStep(1, "Step 1");
        var step2 = CreateStep(2, "Step 2");
        var plan = CreatePlan("Test goal", step0, step1, step2);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Test goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result0 = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result0)));

        _evaluator.EvaluateAsync(plan, step0, result0, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Abort, Reason = "Critical failure" });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Test goal", context));

        // Assert
        events.OfType<StepStartedEvent>().Should().HaveCount(1);
        events.Last().Should().BeOfType<PlanAbortedEvent>();
        var aborted = (PlanAbortedEvent)events.Last();
        aborted.Reason.Should().Be("Critical failure");

        // Step 1 and 2 should never be executed
        _executor.DidNotReceive().ExecuteStepAsync(plan, step1, Arg.Any<CancellationToken>());
        _executor.DidNotReceive().ExecuteStepAsync(plan, step2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SkipsDependentStepWhenDependencyFailed()
    {
        // Arrange — step 0 fails (but evaluator says Continue), step 1 depends on step 0
        var step0 = CreateStep(0, "Prerequisite step");
        var step1 = CreateStep(1, "Dependent step", dependsOn: [0]);
        var plan = CreatePlan("Test goal", step0, step1);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Test goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var failResult = new StepResult { Success = false, Output = "Prerequisite failed" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        _evaluator.EvaluateAsync(plan, step0, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Test goal", context));

        // Assert
        // Step 1 should be skipped — no StepStartedEvent for index 1
        events.OfType<StepStartedEvent>().Should().HaveCount(1);
        events.OfType<StepStartedEvent>().Single().StepIndex.Should().Be(0);

        // Step 1 status should be Skipped
        step1.Status.Should().Be(StepStatus.Skipped);

        // Step 1 should never be executed
        _executor.DidNotReceive().ExecuteStepAsync(plan, step1, Arg.Any<CancellationToken>());

        // Plan should still complete since evaluator said Continue for step 0
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }
}
