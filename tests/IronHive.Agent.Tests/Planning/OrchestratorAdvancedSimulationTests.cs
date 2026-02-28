using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;

using NSubstitute;

namespace IronHive.Agent.Tests.Planning;

/// <summary>
/// Advanced orchestrator scenarios: complex dependency graphs, partial completion,
/// multi-failure, replan with different plan sizes, summary accuracy.
/// </summary>
[Trait("Category", "Simulation")]
public class OrchestratorAdvancedSimulationTests
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

    private void SetupStepExecution(TaskPlan plan, PlanStep step, StepResult result)
    {
        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new StepCompletedEvent(step.Index, result)));
    }

    private void SetupEvaluation(TaskPlan plan, PlanStep step, StepResult result, EvaluationAction action, string? reason = null)
    {
        _evaluator.EvaluateAsync(plan, step, result, Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult { Action = action, Reason = reason });
    }

    #endregion

    // ── Diamond Dependency Pattern ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DiamondDependency_AllSucceed_CompletesAllFour()
    {
        // Diamond: step0 → step1, step0 → step2, step1+step2 → step3
        var step0 = CreateStep(0, "Root");
        var step1 = CreateStep(1, "Left", dependsOn: [0]);
        var step2 = CreateStep(2, "Right", dependsOn: [0]);
        int[] deps = [1, 2];
        var step3 = CreateStep(3, "Join", dependsOn: deps);
        var plan = CreatePlan("Diamond goal", step0, step1, step2, step3);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Diamond goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var success = new StepResult { Success = true, Output = "OK" };
        foreach (var step in plan.Steps)
        {
            SetupStepExecution(plan, step, success);
            SetupEvaluation(plan, step, success, EvaluationAction.Continue);
        }

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Diamond goal", context));

        // Assert — all 4 steps started and completed
        events.OfType<StepStartedEvent>().Should().HaveCount(4);
        events.OfType<StepCompletedEvent>().Should().HaveCount(4);
        events.Last().Should().BeOfType<PlanCompletedEvent>();

        var completed = (PlanCompletedEvent)events.Last();
        completed.Summary.Should().Contain("4/4");
    }

    [Fact]
    public async Task ExecuteAsync_DiamondDependency_LeftFails_JoinSkipped()
    {
        // Diamond: step0 → step1(fail), step0 → step2(ok), step1+step2 → step3(skip)
        var step0 = CreateStep(0, "Root");
        var step1 = CreateStep(1, "Left (fails)", dependsOn: [0]);
        var step2 = CreateStep(2, "Right", dependsOn: [0]);
        int[] deps = [1, 2];
        var step3 = CreateStep(3, "Join", dependsOn: deps);
        var plan = CreatePlan("Diamond goal", step0, step1, step2, step3);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Diamond goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var success = new StepResult { Success = true, Output = "OK" };
        var failure = new StepResult { Success = false, Output = "Error" };

        SetupStepExecution(plan, step0, success);
        SetupEvaluation(plan, step0, success, EvaluationAction.Continue);

        SetupStepExecution(plan, step1, failure);
        SetupEvaluation(plan, step1, failure, EvaluationAction.Continue);

        SetupStepExecution(plan, step2, success);
        SetupEvaluation(plan, step2, success, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Diamond goal", context));

        // Assert — step3 skipped because step1 failed
        step3.Status.Should().Be(StepStatus.Skipped);
        events.OfType<StepStartedEvent>().Should().HaveCount(3); // step0, step1, step2
        _ = _executor.DidNotReceive()
            .ExecuteStepAsync(plan, step3, Arg.Any<CancellationToken>());
    }

    // ── Partial Completion with Replan ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PartialCompletion_SecondStepFails_ReplansWithDifferentSize()
    {
        // 3-step plan: step0 ok, step1 fails → replan → new 2-step plan succeeds
        var step0 = CreateStep(0, "Prep");
        var step1 = CreateStep(1, "Process");
        var step2 = CreateStep(2, "Cleanup");
        var plan1 = CreatePlan("Goal", step0, step1, step2);

        var newStep0 = CreateStep(0, "New process");
        var newStep1 = CreateStep(1, "New cleanup");
        var plan2 = CreatePlan("Goal", newStep0, newStep1);

        var context = new PlanningContext();
        var options = new PlanAndExecuteOptions { MaxReplans = 2 };

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan1);
        _planner.ReplanAsync(plan1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(plan2);

        var success = new StepResult { Success = true, Output = "OK" };
        var failure = new StepResult { Success = false, Output = "Error" };

        SetupStepExecution(plan1, step0, success);
        SetupEvaluation(plan1, step0, success, EvaluationAction.Continue);
        SetupStepExecution(plan1, step1, failure);
        SetupEvaluation(plan1, step1, failure, EvaluationAction.Replan, "Step 1 failed");

        SetupStepExecution(plan2, newStep0, success);
        SetupEvaluation(plan2, newStep0, success, EvaluationAction.Continue);
        SetupStepExecution(plan2, newStep1, success);
        SetupEvaluation(plan2, newStep1, success, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator(options);

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — 1 replan, then plan2 completed
        events.OfType<PlanReplanEvent>().Should().HaveCount(1);
        events.Last().Should().BeOfType<PlanCompletedEvent>();

        var completed = (PlanCompletedEvent)events.Last();
        completed.Summary.Should().Contain("2/2");
    }

    // ── Multiple Consecutive Failures ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MultipleStepsFail_EvaluatorContinues_AllExecuted()
    {
        // 3 steps, all fail, evaluator says Continue for each → plan completes (with 0 success)
        var steps = Enumerable.Range(0, 3)
            .Select(i => CreateStep(i, $"Step {i}"))
            .ToArray();
        var plan = CreatePlan("Goal", steps);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var failure = new StepResult { Success = false, Output = "Error" };

        foreach (var step in steps)
        {
            SetupStepExecution(plan, step, failure);
            SetupEvaluation(plan, step, failure, EvaluationAction.Continue);
        }

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — all 3 steps attempted, plan completes
        events.OfType<StepStartedEvent>().Should().HaveCount(3);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
        var completed = (PlanCompletedEvent)events.Last();
        completed.Summary.Should().Contain("0/3");
    }

    // ── Last Step Replan ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LastStepTriggersReplan_ReplansCorrectly()
    {
        // 2 steps: step0 ok, step1 fails → replan → new single step succeeds
        var step0 = CreateStep(0, "First");
        var step1 = CreateStep(1, "Last (fails)");
        var plan1 = CreatePlan("Goal", step0, step1);

        var newStep = CreateStep(0, "Fixed step");
        var plan2 = CreatePlan("Goal", newStep);

        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan1);
        _planner.ReplanAsync(plan1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(plan2);

        var success = new StepResult { Success = true, Output = "OK" };
        var failure = new StepResult { Success = false, Output = "Last step error" };

        SetupStepExecution(plan1, step0, success);
        SetupEvaluation(plan1, step0, success, EvaluationAction.Continue);
        SetupStepExecution(plan1, step1, failure);
        SetupEvaluation(plan1, step1, failure, EvaluationAction.Replan);

        SetupStepExecution(plan2, newStep, success);
        SetupEvaluation(plan2, newStep, success, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        events.OfType<PlanReplanEvent>().Should().HaveCount(1);
        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    // ── Large Progress Stream ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ManyProgressEvents_AllForwarded()
    {
        // Executor emits 50 progress events before completion
        var step = CreateStep(0, "Chatty step");
        var plan = CreatePlan("Goal", step);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var progressEvents = Enumerable.Range(0, 50)
            .Select(i => (PlanExecutionEvent)new StepProgressEvent(0, $"chunk {i}"))
            .ToList();
        var result = new StepResult { Success = true, Output = "Done" };
        progressEvents.Add(new StepCompletedEvent(0, result));

        _executor.ExecuteStepAsync(plan, step, Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(progressEvents.ToArray()));

        SetupEvaluation(plan, step, result, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — all 50 progress events forwarded
        events.OfType<StepProgressEvent>().Should().HaveCount(50);
    }

    // ── Summary Accuracy ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MixedOutcomes_SummaryReflectsCompletedOnly()
    {
        // 4 steps: step0 ok, step1 fail, step2 ok (no deps), step3 depends on step1 → skipped
        var step0 = CreateStep(0, "A");
        var step1 = CreateStep(1, "B");
        var step2 = CreateStep(2, "C");
        var step3 = CreateStep(3, "D", dependsOn: [1]);
        var plan = CreatePlan("Goal", step0, step1, step2, step3);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var success = new StepResult { Success = true, Output = "OK" };
        var failure = new StepResult { Success = false, Output = "Error" };

        SetupStepExecution(plan, step0, success);
        SetupEvaluation(plan, step0, success, EvaluationAction.Continue);
        SetupStepExecution(plan, step1, failure);
        SetupEvaluation(plan, step1, failure, EvaluationAction.Continue);
        SetupStepExecution(plan, step2, success);
        SetupEvaluation(plan, step2, success, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — 2 completed, 1 failed, 1 skipped
        var completed = (PlanCompletedEvent)events.Last();
        completed.Summary.Should().Contain("2/4");

        step0.Status.Should().Be(StepStatus.Completed);
        step1.Status.Should().Be(StepStatus.Failed);
        step2.Status.Should().Be(StepStatus.Completed);
        step3.Status.Should().Be(StepStatus.Skipped);
    }

    // ── Event Ordering ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EventOrdering_FollowsStrictSequence()
    {
        // Verify: PlanCreated → (StepStarted → ... → StepCompleted)* → PlanCompleted
        var steps = Enumerable.Range(0, 3)
            .Select(i => CreateStep(i, $"Step {i}"))
            .ToArray();
        var plan = CreatePlan("Goal", steps);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var success = new StepResult { Success = true, Output = "OK" };
        foreach (var step in steps)
        {
            SetupStepExecution(plan, step, success);
            SetupEvaluation(plan, step, success, EvaluationAction.Continue);
        }

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — strict ordering
        events[0].Should().BeOfType<PlanCreatedEvent>();

        // Each step: Started then Completed, in index order
        var stepEvents = events.Skip(1).SkipLast(1).ToList();
        for (var i = 0; i < 3; i++)
        {
            var started = stepEvents[i * 2];
            var completed = stepEvents[i * 2 + 1];
            started.Should().BeOfType<StepStartedEvent>().Which.StepIndex.Should().Be(i);
            completed.Should().BeOfType<StepCompletedEvent>().Which.StepIndex.Should().Be(i);
        }

        events.Last().Should().BeOfType<PlanCompletedEvent>();
    }

    // ── Replan Event Contains New Plan ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReplanEvent_CarriesNewPlanReference()
    {
        var failStep = CreateStep(0, "Fail");
        var failPlan = CreatePlan("Goal", failStep);
        var successStep = CreateStep(0, "Success");
        var successPlan = CreatePlan("Goal", successStep);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(failPlan);
        _planner.ReplanAsync(failPlan, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(successPlan);

        var failResult = new StepResult { Success = false, Output = "Error" };
        var successResult = new StepResult { Success = true, Output = "OK" };

        SetupStepExecution(failPlan, failStep, failResult);
        SetupEvaluation(failPlan, failStep, failResult, EvaluationAction.Replan);
        SetupStepExecution(successPlan, successStep, successResult);
        SetupEvaluation(successPlan, successStep, successResult, EvaluationAction.Continue);

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert — PlanReplanEvent carries the new plan
        var replan = events.OfType<PlanReplanEvent>().Single();
        replan.NewPlan.Should().BeSameAs(successPlan);
        replan.Reason.Should().Contain("Replan attempt 1");
    }

    // ── Abort Mid-Plan Preserves Earlier Step States ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_AbortAfterSecondStep_PreservesFirstStepState()
    {
        // 3 steps: step0 ok, step1 ok + abort, step2 never reached
        var step0 = CreateStep(0, "First");
        var step1 = CreateStep(1, "Second");
        var step2 = CreateStep(2, "Third");
        var plan = CreatePlan("Goal", step0, step1, step2);
        var context = new PlanningContext();

        _planner.CreatePlanAsync("Goal", context, Arg.Any<CancellationToken>())
            .Returns(plan);

        var success = new StepResult { Success = true, Output = "OK" };

        SetupStepExecution(plan, step0, success);
        SetupEvaluation(plan, step0, success, EvaluationAction.Continue);
        SetupStepExecution(plan, step1, success);
        SetupEvaluation(plan, step1, success, EvaluationAction.Abort, "Abort after step 1");

        var orchestrator = CreateOrchestrator();

        // Act
        var events = await CollectEventsAsync(orchestrator.ExecuteAsync("Goal", context));

        // Assert
        step0.Status.Should().Be(StepStatus.Completed);
        step1.Status.Should().Be(StepStatus.Completed);
        step2.Status.Should().Be(StepStatus.Pending); // never touched
        events.Last().Should().BeOfType<PlanAbortedEvent>();
    }

    // ── Constructor Validation ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullPlanner_Throws()
    {
        var act = () => new PlanAndExecuteOrchestrator(null!, _executor, _evaluator);
        act.Should().Throw<ArgumentNullException>().WithParameterName("planner");
    }

    [Fact]
    public void Constructor_NullExecutor_Throws()
    {
        var act = () => new PlanAndExecuteOrchestrator(_planner, null!, _evaluator);
        act.Should().Throw<ArgumentNullException>().WithParameterName("executor");
    }

    [Fact]
    public void Constructor_NullEvaluator_Throws()
    {
        var act = () => new PlanAndExecuteOrchestrator(_planner, _executor, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("evaluator");
    }

    [Fact]
    public void Constructor_NullOptions_UsesDefaults()
    {
        // Should not throw — null options means use defaults
        var orchestrator = new PlanAndExecuteOrchestrator(_planner, _executor, _evaluator, null);
        orchestrator.Should().NotBeNull();
    }
}
