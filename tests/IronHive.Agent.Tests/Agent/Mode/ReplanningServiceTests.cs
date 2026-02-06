using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// Tests for ReplanningService failure tracking and replanning decisions.
/// </summary>
public class ReplanningServiceTests
{
    private readonly ReplanningService _service = new();

    [Fact]
    public void ShouldReplan_NoFailures_ReturnsFalse()
    {
        var decision = _service.ShouldReplan();

        Assert.False(decision.ShouldReplan);
    }

    [Fact]
    public void ShouldReplan_SingleFailure_ReturnsFalse()
    {
        _service.RecordFailure("test_tool", "Error message");

        var decision = _service.ShouldReplan();

        Assert.False(decision.ShouldReplan);
    }

    [Fact]
    public void ShouldReplan_ConsecutiveFailures_ReturnsTrue()
    {
        _service.MaxConsecutiveFailures = 3;

        _service.RecordFailure("tool1", "Error 1");
        _service.RecordFailure("tool2", "Error 2");
        _service.RecordFailure("tool3", "Error 3");

        var decision = _service.ShouldReplan();

        Assert.True(decision.ShouldReplan);
        Assert.Contains("consecutive", decision.Reason);
        Assert.Equal(ReplanningSeverity.High, decision.Severity);
    }

    [Fact]
    public void ShouldReplan_SuccessResetsConsecutive()
    {
        _service.MaxConsecutiveFailures = 3;

        _service.RecordFailure("tool1", "Error 1");
        _service.RecordFailure("tool2", "Error 2");
        _service.RecordSuccess("tool3"); // This should reset consecutive count
        _service.RecordFailure("tool4", "Error 4");

        var decision = _service.ShouldReplan();

        Assert.False(decision.ShouldReplan);
    }

    [Fact]
    public void ShouldReplan_TotalFailuresExceeded_ReturnsTrue()
    {
        _service.MaxTotalFailures = 5;
        _service.MaxConsecutiveFailures = 100; // Disable consecutive check

        for (int i = 0; i < 5; i++)
        {
            _service.RecordFailure($"tool{i}", $"Error {i}");
            _service.RecordSuccess("ok"); // Reset consecutive but not total
        }

        var decision = _service.ShouldReplan();

        Assert.True(decision.ShouldReplan);
        Assert.Contains("total", decision.Reason);
        Assert.Equal(ReplanningSeverity.Critical, decision.Severity);
    }

    [Fact]
    public void ShouldReplan_RepeatedPattern_ReturnsTrue()
    {
        _service.MaxConsecutiveFailures = 100; // Disable consecutive check
        _service.MaxTotalFailures = 100; // Disable total check

        // Same tool, same error 3 times
        _service.RecordFailure("same_tool", "Same error");
        _service.RecordFailure("same_tool", "Same error");
        _service.RecordFailure("same_tool", "Same error");

        var decision = _service.ShouldReplan();

        Assert.True(decision.ShouldReplan);
        Assert.Contains("Repeated failure pattern", decision.Reason);
    }

    [Fact]
    public void ShouldReplan_DifferentErrors_NoPattern()
    {
        _service.MaxConsecutiveFailures = 100;
        _service.MaxTotalFailures = 100;

        _service.RecordFailure("same_tool", "Error 1");
        _service.RecordFailure("same_tool", "Error 2");
        _service.RecordFailure("same_tool", "Error 3");

        var decision = _service.ShouldReplan();

        Assert.False(decision.ShouldReplan);
    }

    [Fact]
    public void GetFailureContext_ReturnsCorrectInfo()
    {
        _service.RecordFailure("tool1", "Error 1");
        _service.RecordFailure("tool2", "Error 2");

        var context = _service.GetFailureContext();

        Assert.Equal(2, context.TotalFailures);
        Assert.Equal(2, context.ConsecutiveFailures);
        Assert.Equal(2, context.RecentFailures.Count);
        Assert.False(context.RepeatedFailureDetected);
    }

    [Fact]
    public void GetFailureContext_Summary_ContainsInfo()
    {
        _service.RecordFailure("test_tool", "Test error");

        var context = _service.GetFailureContext();
        var summary = context.GetSummary();

        Assert.Contains("test_tool", summary);
        Assert.Contains("Test error", summary);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        _service.RecordFailure("tool1", "Error 1");
        _service.RecordFailure("tool2", "Error 2");
        _service.RecordFailure("tool3", "Error 3");

        _service.Reset();

        var context = _service.GetFailureContext();
        Assert.Equal(0, context.TotalFailures);
        Assert.Equal(0, context.ConsecutiveFailures);
        Assert.Empty(context.RecentFailures);
    }

    [Fact]
    public void FailureContext_EmptySummary()
    {
        var context = _service.GetFailureContext();
        var summary = context.GetSummary();

        Assert.Contains("No recent failures", summary);
    }

    [Fact]
    public void ReplanningDecision_NoFactory()
    {
        var decision = ReplanningDecision.No;

        Assert.False(decision.ShouldReplan);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void ReplanningDecision_YesFactory()
    {
        var decision = ReplanningDecision.Yes("Test reason", ReplanningSeverity.High);

        Assert.True(decision.ShouldReplan);
        Assert.Equal("Test reason", decision.Reason);
        Assert.Equal(ReplanningSeverity.High, decision.Severity);
    }
}

/// <summary>
/// Tests for ModeManager replan trigger.
/// </summary>
public class ModeManagerReplanTests
{
    [Fact]
    public void Fire_ReplanRequested_FromWorking_TransitionsToPlanning()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var result = manager.Fire(ModeTrigger.ReplanRequested);

        Assert.True(result);
        Assert.Equal(AgentMode.Planning, manager.CurrentMode);
    }

    [Fact]
    public void Fire_ReplanRequested_FromIdle_Fails()
    {
        var manager = new ModeManager();

        var result = manager.Fire(ModeTrigger.ReplanRequested);

        Assert.False(result);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Fire_ReplanRequested_FromPlanning_Fails()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartPlanning);

        var result = manager.Fire(ModeTrigger.ReplanRequested);

        Assert.False(result);
        Assert.Equal(AgentMode.Planning, manager.CurrentMode);
    }

    [Fact]
    public void GetPermittedTriggers_WorkingMode_IncludesReplanRequested()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var triggers = manager.GetPermittedTriggers();

        Assert.Contains(ModeTrigger.ReplanRequested, triggers);
    }
}
