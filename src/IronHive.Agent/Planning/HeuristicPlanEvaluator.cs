using IronHive.Abstractions.Agent.Planning;

namespace IronHive.Agent.Planning;

/// <summary>
/// A domain-agnostic <see cref="IPlanEvaluator"/> that applies heuristic rules:
/// <list type="bullet">
///   <item>Successful steps continue execution.</item>
///   <item>Steps with critical error patterns abort the plan.</item>
///   <item>Exceeding the consecutive failure limit aborts the plan.</item>
///   <item>Other failures trigger replanning.</item>
/// </list>
/// Subclass and override <see cref="EvaluateAsync"/> to add domain-specific logic.
/// </summary>
public class HeuristicPlanEvaluator : IPlanEvaluator
{
    private readonly HeuristicPlanEvaluatorOptions _options;

    /// <summary>
    /// Initializes a new instance with default options.
    /// </summary>
    public HeuristicPlanEvaluator()
        : this(new HeuristicPlanEvaluatorOptions())
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified options.
    /// </summary>
    public HeuristicPlanEvaluator(HeuristicPlanEvaluatorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public virtual Task<EvaluationResult> EvaluateAsync(
        TaskPlan plan,
        PlanStep completedStep,
        StepResult result,
        CancellationToken cancellationToken = default)
    {
        // Successful step → continue execution
        if (result.Success)
        {
            return Task.FromResult(new EvaluationResult
            {
                Action = EvaluationAction.Continue,
            });
        }

        // Check for critical (unrecoverable) error patterns
        if (result.Error is not null &&
            _options.CriticalErrorPatterns.Any(
                pattern => result.Error.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new EvaluationResult
            {
                Action = EvaluationAction.Abort,
                Reason = $"Critical error: {result.Error}",
            });
        }

        // Check consecutive failure limit
        if (_options.MaxConsecutiveFailures > 0)
        {
            var consecutiveFailures = CountConsecutiveFailures(plan, completedStep);
            if (consecutiveFailures >= _options.MaxConsecutiveFailures)
            {
                return Task.FromResult(new EvaluationResult
                {
                    Action = EvaluationAction.Abort,
                    Reason = $"Exceeded maximum consecutive failures ({_options.MaxConsecutiveFailures})",
                });
            }
        }

        // Non-critical failure → replan
        return Task.FromResult(new EvaluationResult
        {
            Action = EvaluationAction.Replan,
            Reason = result.Error ?? "Step failed without error details",
        });
    }

    /// <summary>
    /// Counts consecutive failures ending at (and including) the current step.
    /// The current step is counted as a failure (since this method is only
    /// called when the current step has failed). Previous steps are examined
    /// in reverse index order; the streak ends at the first non-failed step.
    /// </summary>
    private static int CountConsecutiveFailures(TaskPlan plan, PlanStep completedStep)
    {
        var consecutiveFailures = 1; // current step is a failure

        var precedingSteps = plan.Steps
            .Where(s => s.Index < completedStep.Index)
            .OrderByDescending(s => s.Index);

        foreach (var step in precedingSteps)
        {
            if (step.Status == StepStatus.Failed)
            {
                consecutiveFailures++;
            }
            else
            {
                break;
            }
        }

        return consecutiveFailures;
    }
}
