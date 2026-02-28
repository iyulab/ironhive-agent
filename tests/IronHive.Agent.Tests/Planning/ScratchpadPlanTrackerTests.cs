using FluentAssertions;

using IronHive.Abstractions.Agent.Planning;
using IronHive.Agent.Context;
using IronHive.Agent.Planning;

namespace IronHive.Agent.Tests.Planning;

public class ScratchpadPlanTrackerTests
{
    private readonly Scratchpad _scratchpad = new();
    private readonly ScratchpadPlanTracker _tracker;

    public ScratchpadPlanTrackerTests()
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

    [Fact]
    public void OnPlanCreated_PopulatesScratchpad()
    {
        // Arrange
        var plan = CreateTestPlan("Organize files", "Scan directory", "Categorize items", "Move files");

        // Act
        _tracker.OnPlanCreated(plan);

        // Assert
        _scratchpad.CurrentPlan.Should().NotBeNull();
        _scratchpad.CurrentPlan.Should().Contain("Organize files");
        _scratchpad.CurrentPlan.Should().Contain("Scan directory");
        _scratchpad.CurrentPlan.Should().Contain("Categorize items");
        _scratchpad.CurrentPlan.Should().Contain("Move files");
        _scratchpad.CurrentStep.Should().Be(0);
    }

    [Fact]
    public void OnStepStarted_UpdatesCurrentStep()
    {
        // Arrange
        var plan = CreateTestPlan("Test goal", "Step A", "Step B");
        _tracker.OnPlanCreated(plan);

        // Act
        _tracker.OnStepStarted(1, "Step B");

        // Assert
        _scratchpad.CurrentStep.Should().Be(1);
    }

    [Fact]
    public void OnStepCompleted_AddsObservation()
    {
        // Arrange
        var plan = CreateTestPlan("Test goal", "Step A");
        _tracker.OnPlanCreated(plan);
        _tracker.OnStepStarted(0, "Step A");

        var result = new StepResult { Success = true, Output = "Files organized successfully" };

        // Act
        _tracker.OnStepCompleted(0, result);

        // Assert
        _scratchpad.Observations.Should().HaveCount(1);
        _scratchpad.Observations[0].Should().Contain("Step 0");
        _scratchpad.Observations[0].Should().Contain("OK");
        _scratchpad.Observations[0].Should().Contain("Files organized successfully");
    }

    [Fact]
    public void OnStepCompleted_TruncatesLongOutput()
    {
        // Arrange
        var longOutput = new string('x', 300);
        var result = new StepResult { Success = false, Output = longOutput };

        // Act
        _tracker.OnStepCompleted(0, result);

        // Assert
        _scratchpad.Observations.Should().HaveCount(1);
        _scratchpad.Observations[0].Should().Contain("FAILED");
        _scratchpad.Observations[0].Should().Contain("...");
        // Original output is 300 chars, truncated to 200 + "..."
        _scratchpad.Observations[0].Length.Should().BeLessThan(300);
    }

    [Fact]
    public void OnReplan_ClearsAndRepopulates()
    {
        // Arrange — initial plan
        var initialPlan = CreateTestPlan("Initial goal", "Step A", "Step B");
        _tracker.OnPlanCreated(initialPlan);
        _tracker.OnStepStarted(0, "Step A");
        _tracker.OnStepCompleted(0, new StepResult { Success = false, Output = "Failed" });

        var newPlan = CreateTestPlan("Revised goal", "Step X", "Step Y");

        // Act
        _tracker.OnReplan("Step A failed, trying different approach", newPlan);

        // Assert — should have replan observation and updated plan
        _scratchpad.Observations.Should().HaveCount(2); // step 0 completed + replan
        _scratchpad.Observations.Last().Should().Contain("Replan:");
        _scratchpad.Observations.Last().Should().Contain("different approach");

        // Plan should be updated to new plan
        _scratchpad.CurrentPlan.Should().Contain("Revised goal");
        _scratchpad.CurrentPlan.Should().Contain("Step X");
        _scratchpad.CurrentPlan.Should().Contain("Step Y");
        _scratchpad.CurrentStep.Should().Be(0);
    }
}
