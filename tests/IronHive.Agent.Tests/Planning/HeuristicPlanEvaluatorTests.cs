using FluentAssertions;
using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Planning;
using Xunit;

namespace IronHive.Agent.Tests.Planning;

public sealed class HeuristicPlanEvaluatorTests
{
    private static TaskPlan CreatePlan(params PlanStep[] steps) => new()
    {
        Goal = "test goal",
        Steps = steps.ToList(),
    };

    private static PlanStep CreateStep(int index, StepStatus status = StepStatus.Pending)
        => new()
        {
            Index = index,
            Description = $"Step {index}",
            Instruction = $"Do step {index}",
            Status = status,
        };

    #region Success → Continue

    [Fact]
    public async Task EvaluateAsync_SuccessfulStep_ReturnsContinue()
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = true, Output = "done" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Continue);
        evaluation.Reason.Should().BeNull();
    }

    #endregion

    #region Critical errors → Abort

    [Theory]
    [InlineData("out of memory")]
    [InlineData("stack overflow")]
    [InlineData("disk full")]
    [InlineData("no space left on device")]
    [InlineData("access denied")]
    [InlineData("permission denied")]
    public async Task EvaluateAsync_CriticalError_ReturnsAbort(string errorPattern)
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = $"Failed: {errorPattern} encountered" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("Critical error");
    }

    [Fact]
    public async Task EvaluateAsync_CriticalError_CaseInsensitive()
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = "OUT OF MEMORY" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("OUT OF MEMORY");
    }

    [Fact]
    public async Task EvaluateAsync_CriticalError_DiskFull_ReturnsAbort()
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = "Disk Full" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("Disk Full");
    }

    #endregion

    #region Non-critical failure → Replan

    [Fact]
    public async Task EvaluateAsync_NonCriticalFailure_ReturnsReplan()
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = "File not found" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Replan);
        evaluation.Reason.Should().Contain("File not found");
    }

    [Fact]
    public async Task EvaluateAsync_NullError_ReturnsReplanWithDefaultMessage()
    {
        var evaluator = new HeuristicPlanEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Replan);
        evaluation.Reason.Should().Be("Step failed without error details");
    }

    #endregion

    #region Consecutive failures → Abort

    [Fact]
    public async Task EvaluateAsync_ConsecutiveFailuresExceedMax_ReturnsAbort()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 3,
        });
        var steps = new[]
        {
            CreateStep(0, StepStatus.Failed),
            CreateStep(1, StepStatus.Failed),
            CreateStep(2), // current step (will fail)
        };
        var plan = CreatePlan(steps);
        var result = new StepResult { Success = false, Output = "", Error = "timeout" };

        var evaluation = await evaluator.EvaluateAsync(plan, steps[2], result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("consecutive failures");
    }

    [Fact]
    public async Task EvaluateAsync_ConsecutiveFailuresBelowMax_ReturnsReplan()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 3,
        });
        var steps = new[]
        {
            CreateStep(0, StepStatus.Failed),
            CreateStep(1), // current step (will fail)
        };
        var plan = CreatePlan(steps);
        var result = new StepResult { Success = false, Output = "", Error = "timeout" };

        var evaluation = await evaluator.EvaluateAsync(plan, steps[1], result);

        evaluation.Action.Should().Be(EvaluationAction.Replan);
    }

    [Fact]
    public async Task EvaluateAsync_SuccessBetweenFailures_ResetsConsecutiveCount()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 2,
        });
        var steps = new[]
        {
            CreateStep(0, StepStatus.Failed),
            CreateStep(1, StepStatus.Completed), // success breaks the streak
            CreateStep(2, StepStatus.Failed),
            CreateStep(3), // current step (will fail) → only 2 consecutive, equals max → abort
        };
        var plan = CreatePlan(steps);
        var result = new StepResult { Success = false, Output = "", Error = "timeout" };

        var evaluation = await evaluator.EvaluateAsync(plan, steps[3], result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
    }

    [Fact]
    public async Task EvaluateAsync_SuccessBreaksStreak_OnlyOneFailure_Replans()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 3,
        });
        var steps = new[]
        {
            CreateStep(0, StepStatus.Failed),
            CreateStep(1, StepStatus.Completed), // success breaks the streak
            CreateStep(2), // current step (will fail) → only 1 consecutive
        };
        var plan = CreatePlan(steps);
        var result = new StepResult { Success = false, Output = "", Error = "timeout" };

        var evaluation = await evaluator.EvaluateAsync(plan, steps[2], result);

        evaluation.Action.Should().Be(EvaluationAction.Replan);
    }

    [Fact]
    public async Task EvaluateAsync_ConsecutiveFailuresDisabled_NeverAborts()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 0, // disabled
        });
        var steps = new[]
        {
            CreateStep(0, StepStatus.Failed),
            CreateStep(1, StepStatus.Failed),
            CreateStep(2, StepStatus.Failed),
            CreateStep(3, StepStatus.Failed),
            CreateStep(4), // current step
        };
        var plan = CreatePlan(steps);
        var result = new StepResult { Success = false, Output = "", Error = "timeout" };

        var evaluation = await evaluator.EvaluateAsync(plan, steps[4], result);

        evaluation.Action.Should().Be(EvaluationAction.Replan);
    }

    #endregion

    #region Custom options

    [Fact]
    public async Task EvaluateAsync_CustomCriticalPatterns_UsesProvidedPatterns()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            CriticalErrorPatterns = new(StringComparer.OrdinalIgnoreCase) { "fatal" },
        });
        var step = CreateStep(0);
        var plan = CreatePlan(step);

        // "fatal" should be critical
        var fatalResult = new StepResult { Success = false, Output = "", Error = "Fatal error occurred" };
        var fatalEval = await evaluator.EvaluateAsync(plan, step, fatalResult);
        fatalEval.Action.Should().Be(EvaluationAction.Abort);

        // "out of memory" should NOT be critical with custom patterns (default cleared)
        var oomResult = new StepResult { Success = false, Output = "", Error = "out of memory" };
        var oomEval = await evaluator.EvaluateAsync(plan, step, oomResult);
        oomEval.Action.Should().Be(EvaluationAction.Replan);
    }

    [Fact]
    public async Task EvaluateAsync_CustomMaxConsecutiveFailures_UsesProvidedValue()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 1, // abort on first failure
        });
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = "some error" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("consecutive failures");
    }

    #endregion

    #region Default options

    [Fact]
    public async Task DefaultOptions_ContainsExpectedCriticalPatterns()
    {
        var options = new HeuristicPlanEvaluatorOptions();

        options.CriticalErrorPatterns.Should().Contain("out of memory");
        options.CriticalErrorPatterns.Should().Contain("stack overflow");
        options.CriticalErrorPatterns.Should().Contain("disk full");
        options.CriticalErrorPatterns.Should().Contain("no space left on device");
        options.CriticalErrorPatterns.Should().Contain("access denied");
        options.CriticalErrorPatterns.Should().Contain("permission denied");
        options.MaxConsecutiveFailures.Should().Be(3);
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new HeuristicPlanEvaluator(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region CriticalError takes precedence over ConsecutiveFailures

    [Fact]
    public async Task EvaluateAsync_CriticalErrorWithConsecutiveFailures_AbortsByCriticalError()
    {
        var evaluator = new HeuristicPlanEvaluator(new HeuristicPlanEvaluatorOptions
        {
            MaxConsecutiveFailures = 10, // high threshold
        });
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = false, Output = "", Error = "out of memory" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
        evaluation.Reason.Should().Contain("Critical error"); // not "consecutive failures"
    }

    #endregion

    #region Virtual override

    [Fact]
    public async Task EvaluateAsync_IsVirtual_CanBeOverridden()
    {
        var evaluator = new AlwaysAbortEvaluator();
        var step = CreateStep(0);
        var plan = CreatePlan(step);
        var result = new StepResult { Success = true, Output = "done" };

        var evaluation = await evaluator.EvaluateAsync(plan, step, result);

        evaluation.Action.Should().Be(EvaluationAction.Abort);
    }

    private sealed class AlwaysAbortEvaluator : HeuristicPlanEvaluator
    {
        public override Task<EvaluationResult> EvaluateAsync(
            TaskPlan plan,
            PlanStep completedStep,
            StepResult result,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EvaluationResult
            {
                Action = EvaluationAction.Abort,
                Reason = "Always abort",
            });
        }
    }

    #endregion
}
