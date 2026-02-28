using System.Runtime.CompilerServices;

using IronHive.Abstractions.Agent.Planning;

namespace IronHive.Agent.Planning;

/// <summary>
/// Orchestrates plan-and-execute loops: plan creation, step execution,
/// evaluation, and replanning with configurable retry limits.
/// </summary>
public sealed class PlanAndExecuteOrchestrator
{
    private readonly ITaskPlanner _planner;
    private readonly IPlanExecutor _executor;
    private readonly IPlanEvaluator _evaluator;
    private readonly PlanAndExecuteOptions _options;

    public PlanAndExecuteOrchestrator(
        ITaskPlanner planner,
        IPlanExecutor executor,
        IPlanEvaluator evaluator,
        PlanAndExecuteOptions? options = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _options = options ?? new PlanAndExecuteOptions();
    }

    /// <summary>
    /// Executes the plan-and-execute loop for the given goal.
    /// </summary>
    public async IAsyncEnumerable<PlanExecutionEvent> ExecuteAsync(
        string goal,
        PlanningContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // PLAN
        var plan = await _planner.CreatePlanAsync(goal, context, cancellationToken);
        plan.Status = PlanStatus.InProgress;
        var replanCount = 0;

        yield return new PlanCreatedEvent(plan);

        while (true)
        {
            var needsReplan = false;

            // EXECUTE LOOP (capped at MaxSteps)
            var stepLimit = Math.Min(plan.Steps.Count, _options.MaxSteps);
            for (var i = 0; i < stepLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = plan.Steps[i];

                // Check dependencies — skip if any dependency failed or was skipped
                if (HasFailedDependency(plan, step))
                {
                    step.Status = StepStatus.Skipped;
                    continue;
                }

                // Start step
                plan.CurrentStepIndex = i;
                step.Status = StepStatus.InProgress;
                yield return new StepStartedEvent(step.Index, step.Description);

                // Execute step — forward all events and capture StepCompletedEvent
                StepResult? result = null;
                await foreach (var evt in _executor.ExecuteStepAsync(plan, step, cancellationToken))
                {
                    if (evt is StepCompletedEvent completed)
                    {
                        result = completed.Result;
                    }

                    yield return evt;
                }

                // If executor did not produce a StepCompletedEvent, treat as failure
                result ??= new StepResult { Success = false, Output = "Step execution produced no result." };

                // Update step status based on result
                step.Status = result.Success ? StepStatus.Completed : StepStatus.Failed;
                step.Result = result.Output;

                // Evaluate
                var evaluation = await _evaluator.EvaluateAsync(plan, step, result, cancellationToken);

                switch (evaluation.Action)
                {
                    case EvaluationAction.Continue:
                        continue;

                    case EvaluationAction.Replan:
                        needsReplan = true;
                        break;

                    case EvaluationAction.Abort:
                        plan.Status = PlanStatus.Failed;
                        yield return new PlanAbortedEvent(plan, evaluation.Reason ?? "Evaluator aborted.");
                        yield break;
                }

                if (needsReplan)
                {
                    break;
                }
            }

            if (!needsReplan)
            {
                // All steps completed
                break;
            }

            // REPLAN
            replanCount++;
            if (replanCount > _options.MaxReplans)
            {
                plan.Status = PlanStatus.Failed;
                yield return new PlanAbortedEvent(plan, $"Exceeded maximum replan attempts ({_options.MaxReplans}).");
                yield break;
            }

            plan.Status = PlanStatus.Replanning;
            var reason = $"Replan attempt {replanCount}: step failures detected.";
            var newPlan = await _planner.ReplanAsync(plan, reason, cancellationToken);
            newPlan.Status = PlanStatus.InProgress;
            newPlan.ReplanCount = replanCount;

            yield return new PlanReplanEvent(reason, newPlan);

            plan = newPlan;
        }

        // COMPLETE
        plan.Status = PlanStatus.Completed;
        var summary = BuildSummary(plan);
        yield return new PlanCompletedEvent(plan, summary);
    }

    private static bool HasFailedDependency(TaskPlan plan, PlanStep step)
    {
        foreach (var depIndex in step.DependsOn)
        {
            if (depIndex >= 0 && depIndex < plan.Steps.Count)
            {
                var dep = plan.Steps[depIndex];
                if (dep.Status is StepStatus.Failed or StepStatus.Skipped)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildSummary(TaskPlan plan)
    {
        var completed = plan.Steps.Count(s => s.Status == StepStatus.Completed);
        var total = plan.Steps.Count;
        return $"Plan completed: {completed}/{total} steps succeeded.";
    }
}
