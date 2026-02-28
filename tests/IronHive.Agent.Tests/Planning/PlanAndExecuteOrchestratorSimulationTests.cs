using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;

using NSubstitute;

namespace IronHive.Agent.Tests.Planning;

[Trait("Category", "Simulation")]
public class PlanAndExecuteOrchestratorSimulationTests
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

    // ── Empty Plan ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyPlan_CompletesImmediately()
    {
        // Arrange — planner returns a plan with zero steps
        var plan = CreatePlan("Empty goal");
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Empty goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Empty goal", context));

        // Assert — PlanCreated + PlanCompleted only
        events.Should().HaveCount(2);
        events[0].Should().BeOfType<PlanCreatedEvent>();
        events[1].Should().BeOfType<PlanCompletedEvent>();

        var completed = (PlanCompletedEvent)events[1];
        completed.Summary.Should().Contain("0/0");

        // No steps executed
        _ = _executor.DidNotReceive()
            .ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>());
        await _evaluator.DidNotReceive()
            .EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<StepResult>(), Arg.Any<CancellationToken>());
    }

    // ── Single Step ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SingleStep_ProducesMinimalEventStream()
    {
        // Arrange — plan with exactly 1 step
        var step = CreateStep(0, "Solo step");
        var plan = CreatePlan("Single goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Single goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Single goal", context));

        // Assert — PlanCreated + StepStarted + StepCompleted + PlanCompleted = 4
        events.Should().HaveCount(4);
        events[0].Should().BeOfType<PlanCreatedEvent>();
        events[1].Should().BeOfType<StepStartedEvent>();
        events[2].Should().BeOfType<StepCompletedEvent>();
        events[3].Should().BeOfType<PlanCompletedEvent>();

        var completed = (PlanCompletedEvent)events[3];
        completed.Summary.Should().Contain("1/1");
    }

    // ── MaxSteps Capping ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MaxStepsCapping_OnlyExecutesAllowedSteps()
    {
        // Arrange — plan has 5 steps, but MaxSteps = 3
        var steps = Enumerable.Range(0, 5)
            .Select(i => CreateStep(i, $"Step {i}"))
            .ToArray();
        var plan = CreatePlan("Big goal", steps);
        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxSteps = 3 };

        _planner.CreatePlanAsync("Big goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var successResult = new StepResult { Success = true, Output = "OK" };

        _executor.ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(
                new StepCompletedEvent(callInfo.ArgAt<PlanStep>(1).Index, successResult)));

        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), successResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Big goal", context));

        // Assert — only 3 steps should be started
        events.OfType<StepStartedEvent>().Should().HaveCount(3);
        events.OfType<StepStartedEvent>()
            .Select(e => e.StepIndex)
            .Should().BeEquivalentTo([0, 1, 2]);

        // Steps 3 and 4 never executed
        _executor.DidNotReceive()
            .ExecuteStepAsync(plan, steps[3], Arg.Any<CancellationToken>());
        _executor.DidNotReceive()
            .ExecuteStepAsync(plan, steps[4], Arg.Any<CancellationToken>());

        // Plan should still complete
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    [Fact]
    public async Task ExecuteAsync_MaxStepsEqualsOne_ExecutesOnlyFirstStep()
    {
        // Arrange — MaxSteps=1 with multi-step plan
        var step0 = CreateStep(0, "First");
        var step1 = CreateStep(1, "Second");
        var plan = CreatePlan("Goal", step0, step1);
        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxSteps = 1 };

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step0, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        events.OfType<StepStartedEvent>().Should().HaveCount(1);
        _executor.DidNotReceive()
            .ExecuteStepAsync(plan, step1, Arg.Any<CancellationToken>());
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    // ── Missing StepCompletedEvent ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExecutorProducesNoCompletedEvent_TreatedAsFailure()
    {
        // Arrange — executor returns events but no StepCompletedEvent
        var step = CreateStep(0, "Incomplete step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        // Executor returns only a progress event, no StepCompletedEvent
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepProgressEvent(0, "partial output...")));

        // Evaluator should receive a synthetic failure result
        _evaluator.EvaluateAsync(plan, step, Arg.Any<StepResult>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — step status should be Failed
        step.Status.Should().Be(StepStatus.Failed);
        step.Result.Should().Contain("no result");

        // Evaluator was called with a synthetic failure
        await _evaluator.Received(1)
            .EvaluateAsync(plan, step,
                Arg.Is<StepResult>(r => !r.Success),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ExecutorProducesEmptyStream_TreatedAsFailure()
    {
        // Arrange — executor returns completely empty stream
        var step = CreateStep(0, "Empty step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        // Empty stream
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        _evaluator.EvaluateAsync(plan, step, Arg.Any<StepResult>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        step.Status.Should().Be(StepStatus.Failed);
    }

    // ── Progress Event Forwarding ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProgressEvents_ForwardedToConsumer()
    {
        // Arrange — executor emits progress + tool call events before completed
        var step = CreateStep(0, "Verbose step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new StepProgressEvent(0, "Working..."),
                new StepToolCallEvent(0, "file_search", true),
                new StepProgressEvent(0, "Almost done..."),
                new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — all intermediate events are present in order
        events.OfType<StepProgressEvent>().Should().HaveCount(2);
        events.OfType<StepToolCallEvent>().Should().HaveCount(1);

        // Verify ordering: StepStarted → Progress → ToolCall → Progress → Completed
        var stepEvents = events.Where(e =>
            e is StepStartedEvent or StepProgressEvent or StepToolCallEvent or StepCompletedEvent).ToList();

        stepEvents[0].Should().BeOfType<StepStartedEvent>();
        stepEvents[1].Should().BeOfType<StepProgressEvent>();
        stepEvents[2].Should().BeOfType<StepToolCallEvent>();
        stepEvents[3].Should().BeOfType<StepProgressEvent>();
        stepEvents[4].Should().BeOfType<StepCompletedEvent>();
    }

    // ── Multi-Dependency Chain ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultipleDependencies_SkipsWhenAnyDependencyFailed()
    {
        // Arrange — step 2 depends on both step 0 and step 1
        //   step 0 succeeds, step 1 fails, step 2 should be skipped
        var step0 = CreateStep(0, "Base A");
        var step1 = CreateStep(1, "Base B");
        int[] deps = [0, 1];
        var step2 = CreateStep(2, "Depends on A and B", dependsOn: deps);
        var plan = CreatePlan("Goal", step0, step1, step2);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var successResult = new StepResult { Success = true, Output = "OK" };
        var failResult = new StepResult { Success = false, Output = "Fail" };

        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, successResult)));
        _executor.ExecuteStepAsync(plan, step1, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(1, failResult)));

        _evaluator.EvaluateAsync(plan, step0, successResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });
        _evaluator.EvaluateAsync(plan, step1, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        step2.Status.Should().Be(StepStatus.Skipped);
        _executor.DidNotReceive()
            .ExecuteStepAsync(plan, step2, Arg.Any<CancellationToken>());
        events.OfType<StepStartedEvent>().Should().HaveCount(2);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    [Fact]
    public async Task ExecuteAsync_TransitiveDependencySkip_PropagatesThroughChain()
    {
        // Arrange — step 0 fails, step 1 depends on 0 (skipped),
        //   step 2 depends on 1 (should also be skipped)
        var step0 = CreateStep(0, "Root");
        var step1 = CreateStep(1, "Mid", dependsOn: [0]);
        var step2 = CreateStep(2, "Leaf", dependsOn: [1]);
        var plan = CreatePlan("Goal", step0, step1, step2);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var failResult = new StepResult { Success = false, Output = "Root failed" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        _evaluator.EvaluateAsync(plan, step0, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — step 1 skipped because dep 0 failed, step 2 skipped because dep 1 skipped
        step1.Status.Should().Be(StepStatus.Skipped);
        step2.Status.Should().Be(StepStatus.Skipped);
        events.OfType<StepStartedEvent>().Should().HaveCount(1);
    }

    // ── All Steps Skipped ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllStepsSkipped_CompletesWithZeroSuccessful()
    {
        // Arrange — step 0 fails, all others depend on step 0
        var step0 = CreateStep(0, "Prerequisite");
        var step1 = CreateStep(1, "Dep A", dependsOn: [0]);
        var step2 = CreateStep(2, "Dep B", dependsOn: [0]);
        var plan = CreatePlan("Goal", step0, step1, step2);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var failResult = new StepResult { Success = false, Output = "Failed" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        _evaluator.EvaluateAsync(plan, step0, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        step1.Status.Should().Be(StepStatus.Skipped);
        step2.Status.Should().Be(StepStatus.Skipped);
        events.Last().Should().BeOfType<PlanCompletedEvent>();

        var completed = (PlanCompletedEvent)events.Last();
        completed.Summary.Should().Contain("0/3");
    }

    // ── MaxReplans Boundary ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MaxReplansZero_FirstReplanCausesAbort()
    {
        // Arrange — MaxReplans=0, first failure with replan evaluation → immediate abort
        var step = CreateStep(0, "Failing step");
        var plan = CreatePlan("Goal", step);
        var replan = CreatePlan("Goal", CreateStep(0, "Replan step"));
        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxReplans = 0 };

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);
        _planner.ReplanAsync(Arg.Any<TaskPlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(replan);

        var failResult = new StepResult { Success = false, Output = "Error" };
        _executor.ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));

        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Replan });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — abort with no replan events
        events.OfType<PlanReplanEvent>().Should().BeEmpty();
        events.Last().Should().BeOfType<PlanAbortedEvent>();
        var aborted = (PlanAbortedEvent)events.Last();
        aborted.Reason.Should().Contain("maximum replan");
    }

    [Fact]
    public async Task ExecuteAsync_ExactlyAtMaxReplans_LastReplanSucceeds()
    {
        // Arrange — MaxReplans=1, first plan fails → replan succeeds
        var failStep = CreateStep(0, "Fail");
        var failPlan = CreatePlan("Goal", failStep);
        var successStep = CreateStep(0, "Success");
        var successPlan = CreatePlan("Goal", successStep);
        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxReplans = 1 };

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(failPlan);
        _planner.ReplanAsync(failPlan, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(successPlan);

        var failResult = new StepResult { Success = false, Output = "Error" };
        var successResult = new StepResult { Success = true, Output = "OK" };

        _executor.ExecuteStepAsync(failPlan, failStep, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));
        _executor.ExecuteStepAsync(successPlan, successStep, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, successResult)));

        _evaluator.EvaluateAsync(failPlan, failStep, failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Replan });
        _evaluator.EvaluateAsync(successPlan, successStep, successResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — exactly 1 replan, then completed
        events.OfType<PlanReplanEvent>().Should().HaveCount(1);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        // Arrange — cancel token is triggered before step execution
        var step0 = CreateStep(0, "Step 0");
        var step1 = CreateStep(1, "Step 1");
        var plan = CreatePlan("Goal", step0, step1);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result0 = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step0, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result0)));
        _evaluator.EvaluateAsync(plan, step0, result0, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        using var cts = new CancellationTokenSource();
        var orchestrator = CreateOrchestrator();

        // Act — cancel after first step completes
        var events = new List<PlanExecutionEvent>();
        var act = async () =>
        {
            await foreach (var evt in orchestrator.ExecuteAsync("Goal", context, cts.Token))
            {
                events.Add(evt);
                // Cancel after first step's StepCompleted
                if (evt is StepCompletedEvent { StepIndex: 0 })
                {
                    cts.Cancel();
                }
            }
        };

        // Assert — OperationCanceledException thrown
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Step 0 was executed, step 1 was not
        events.OfType<StepStartedEvent>().Should().HaveCount(1);
    }

    // ── Plan Status Tracking ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PlanStatusTransitions_AreCorrect()
    {
        // Arrange
        var step = CreateStep(0, "Only step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — final status should be Completed
        plan.Status.Should().Be(PlanStatus.Completed);
        step.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_AbortedPlan_StatusIsFailed()
    {
        // Arrange
        var step = CreateStep(0, "Doomed step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "Done" };
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Abort, Reason = "Critical" });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        plan.Status.Should().Be(PlanStatus.Failed);
        events.Last().Should().BeOfType<PlanAbortedEvent>();
    }

    // ── DependsOn with Invalid Index ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DependsOnOutOfRange_IgnoresInvalidDependency()
    {
        // Arrange — step depends on index 99 which doesn't exist
        var step0 = CreateStep(0, "Normal step");
        var step1 = CreateStep(1, "Step with invalid dep", dependsOn: [99]);
        var plan = CreatePlan("Goal", step0, step1);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "OK" };
        _executor.ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(
                new StepCompletedEvent(callInfo.ArgAt<PlanStep>(1).Index, result)));

        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — step 1 should NOT be skipped (invalid dep index is safely ignored)
        step1.Status.Should().Be(StepStatus.Completed);
        events.OfType<StepStartedEvent>().Should().HaveCount(2);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    [Fact]
    public async Task ExecuteAsync_DependsOnNegativeIndex_IgnoresInvalidDependency()
    {
        // Arrange — step depends on negative index
        var step = CreateStep(0, "Step with neg dep", dependsOn: [-1]);
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var result = new StepResult { Success = true, Output = "OK" };
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(0, result)));

        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — step should execute normally
        step.Status.Should().Be(StepStatus.Completed);
    }

    // ── Replan With Updated Plan ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReplanUpdatesReplanCount_Correctly()
    {
        // Arrange — 2 replans then success
        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxReplans = 3 };

        var plans = Enumerable.Range(0, 3).Select(i =>
        {
            var step = CreateStep(0, $"Attempt {i}");
            return CreatePlan("Goal", step);
        }).ToList();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plans[0]);
        _planner.ReplanAsync(Arg.Any<TaskPlan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(plans[1], plans[2]);

        var failResult = new StepResult { Success = false, Output = "Error" };
        var successResult = new StepResult { Success = true, Output = "OK" };

        // First two plans fail, third succeeds
        _executor.ExecuteStepAsync(plans[0], Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));
        _executor.ExecuteStepAsync(plans[1], Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(new StepCompletedEvent(0, failResult)));
        _executor.ExecuteStepAsync(plans[2], Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(new StepCompletedEvent(0, successResult)));

        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), failResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Replan });
        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), successResult, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — 2 replan events, ReplanCount should be tracked
        events.OfType<PlanReplanEvent>().Should().HaveCount(2);
        plans[1].ReplanCount.Should().Be(1);
        plans[2].ReplanCount.Should().Be(2);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    // ── CurrentStepIndex Tracking ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CurrentStepIndex_UpdatedDuringExecution()
    {
        // Arrange — 3-step plan, track CurrentStepIndex progression
        var steps = Enumerable.Range(0, 3)
            .Select(i => CreateStep(i, $"Step {i}"))
            .ToArray();
        var plan = CreatePlan("Goal", steps);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var stepIndices = new List<int>();

        _executor.ExecuteStepAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                stepIndices.Add(plan.CurrentStepIndex);
                var idx = callInfo.ArgAt<PlanStep>(1).Index;
                return ToAsyncEnumerable(
                    new StepCompletedEvent(idx, new StepResult { Success = true, Output = "OK" }));
            });

        _evaluator.EvaluateAsync(Arg.Any<TaskPlan>(), Arg.Any<PlanStep>(), Arg.Any<StepResult>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = EvaluationAction.Continue });

        var orchestrator = CreateOrchestrator();

        // Act
        await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — CurrentStepIndex was set to 0, 1, 2 during execution
        stepIndices.Should().Equal(0, 1, 2);
    }
}
