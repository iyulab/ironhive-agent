using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Context;
using IronHive.Agent.Planning;

namespace IronHive.Agent.Tests.Planning;

/// <summary>
/// Edge cases for ScratchpadPlanTracker: null handling, empty plans,
/// multiple replans, observation accumulation, truncation boundary.
/// </summary>
[Trait("Category", "Simulation")]
public class ScratchpadPlanTrackerSimulationTests
{
    private readonly Scratchpad _scratchpad = new();
    private readonly ScratchpadPlanTracker _tracker;

    public ScratchpadPlanTrackerSimulationTests()
    {
        _tracker = new ScratchpadPlanTracker(_scratchpad);
    }

    private static TaskPlan CreateTestPlan(string goal = "Test goal", params string[] stepDescriptions)
    {
        var steps = stepDescriptions.Select((desc, i) => new PlanStep
        {
            Index = i,
            Description = desc,
            Instruction = $"Do {desc}",
        }).ToList();

        return new TaskPlan
        {
            Goal = goal,
            Steps = steps,
        };
    }

    // ── Null Handling ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullScratchpad_Throws()
    {
        var act = () => new ScratchpadPlanTracker(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("scratchpad");
    }

    [Fact]
    public void OnPlanCreated_NullPlan_Throws()
    {
        var act = () => _tracker.OnPlanCreated(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("plan");
    }

    [Fact]
    public void OnStepCompleted_NullResult_Throws()
    {
        var act = () => _tracker.OnStepCompleted(0, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("result");
    }

    [Fact]
    public void OnReplan_NullPlan_Throws()
    {
        var act = () => _tracker.OnReplan("reason", null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("newPlan");
    }

    // ── Empty Plan ──────────────────────────────────────────────────────────

    [Fact]
    public void OnPlanCreated_EmptySteps_SetsGoalOnly()
    {
        var plan = CreateTestPlan("Empty plan");

        _tracker.OnPlanCreated(plan);

        _scratchpad.CurrentPlan.Should().Contain("Empty plan");
        _scratchpad.CurrentStep.Should().Be(0);
    }

    // ── Truncation Boundary ─────────────────────────────────────────────────

    [Fact]
    public void OnStepCompleted_ExactlyMaxLength_NoTruncation()
    {
        var output = new string('x', 200); // exactly MaxOutputLength
        var result = new StepResult { Success = true, Output = output };

        _tracker.OnStepCompleted(0, result);

        _scratchpad.Observations.Should().HaveCount(1);
        _scratchpad.Observations[0].Should().NotContain("...");
    }

    [Fact]
    public void OnStepCompleted_OnePastMaxLength_Truncated()
    {
        var output = new string('x', 201); // one past MaxOutputLength
        var result = new StepResult { Success = true, Output = output };

        _tracker.OnStepCompleted(0, result);

        _scratchpad.Observations.Should().HaveCount(1);
        _scratchpad.Observations[0].Should().Contain("...");
    }

    [Fact]
    public void OnStepCompleted_EmptyOutput_NoTruncation()
    {
        var result = new StepResult { Success = true, Output = string.Empty };

        _tracker.OnStepCompleted(0, result);

        _scratchpad.Observations.Should().HaveCount(1);
        _scratchpad.Observations[0].Should().Contain("OK");
    }

    // ── Multiple Steps Accumulation ─────────────────────────────────────────

    [Fact]
    public void OnStepCompleted_FiveSteps_AllObservationsAccumulated()
    {
        for (var i = 0; i < 5; i++)
        {
            _tracker.OnStepCompleted(i, new StepResult { Success = true, Output = $"Result {i}" });
        }

        _scratchpad.Observations.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            _scratchpad.Observations[i].Should().Contain($"Step {i}");
            _scratchpad.Observations[i].Should().Contain($"Result {i}");
        }
    }

    // ── Multiple Replans ────────────────────────────────────────────────────

    [Fact]
    public void OnReplan_ThreeTimes_AccumulatesAllReplanObservations()
    {
        var plan1 = CreateTestPlan("Goal", "Step A");
        _tracker.OnPlanCreated(plan1);
        _tracker.OnStepCompleted(0, new StepResult { Success = false, Output = "Fail 1" });

        var plan2 = CreateTestPlan("Goal", "Step B");
        _tracker.OnReplan("Reason 1", plan2);
        _tracker.OnStepCompleted(0, new StepResult { Success = false, Output = "Fail 2" });

        var plan3 = CreateTestPlan("Goal", "Step C");
        _tracker.OnReplan("Reason 2", plan3);
        _tracker.OnStepCompleted(0, new StepResult { Success = false, Output = "Fail 3" });

        var plan4 = CreateTestPlan("Goal", "Step D");
        _tracker.OnReplan("Reason 3", plan4);

        // 3 step completions + 3 replan observations = 6
        _scratchpad.Observations.Should().HaveCount(6);
        _scratchpad.Observations.Where(o => o.Contains("Replan:")).Should().HaveCount(3);

        // Final plan should be plan4
        _scratchpad.CurrentPlan.Should().Contain("Step D");
    }

    // ── Replan Resets CurrentStep ────────────────────────────────────────────

    [Fact]
    public void OnReplan_AfterStepProgress_ResetsCurrentStepToZero()
    {
        var plan = CreateTestPlan("Goal", "A", "B", "C");
        _tracker.OnPlanCreated(plan);
        _tracker.OnStepStarted(2, "C");

        _scratchpad.CurrentStep.Should().Be(2);

        var newPlan = CreateTestPlan("Goal", "X", "Y");
        _tracker.OnReplan("failed", newPlan);

        _scratchpad.CurrentStep.Should().Be(0);
    }

    // ── FormatPlanSummary ───────────────────────────────────────────────────

    [Fact]
    public void OnPlanCreated_MultipleSteps_FormatsWithIndexedDescriptions()
    {
        var plan = CreateTestPlan("Organize", "Scan", "Categorize", "Move");

        _tracker.OnPlanCreated(plan);

        _scratchpad.CurrentPlan.Should().Contain("Goal: Organize");
        _scratchpad.CurrentPlan.Should().Contain("0. Scan");
        _scratchpad.CurrentPlan.Should().Contain("1. Categorize");
        _scratchpad.CurrentPlan.Should().Contain("2. Move");
    }

    // ── Step Status Formatting ──────────────────────────────────────────────

    [Fact]
    public void OnStepCompleted_Failed_ContainsFailedLabel()
    {
        var result = new StepResult { Success = false, Output = "Error occurred" };
        _tracker.OnStepCompleted(3, result);

        _scratchpad.Observations[0].Should().Contain("Step 3");
        _scratchpad.Observations[0].Should().Contain("FAILED");
        _scratchpad.Observations[0].Should().Contain("Error occurred");
    }

    [Fact]
    public void OnStepCompleted_Success_ContainsOkLabel()
    {
        var result = new StepResult { Success = true, Output = "Files moved" };
        _tracker.OnStepCompleted(0, result);

        _scratchpad.Observations[0].Should().Contain("OK");
        _scratchpad.Observations[0].Should().Contain("Files moved");
    }
}
