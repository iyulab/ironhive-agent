using IronHive.Agent.Mode;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// Tests for ModeManager state machine transitions.
/// </summary>
public class ModeManagerTests
{
    [Fact]
    public void InitialState_IsIdle()
    {
        var manager = new ModeManager();
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Fire_StartPlanning_TransitionsToPlanning()
    {
        var manager = new ModeManager();

        var result = manager.Fire(ModeTrigger.StartPlanning);

        Assert.True(result);
        Assert.Equal(AgentMode.Planning, manager.CurrentMode);
    }

    [Fact]
    public void Fire_StartWorking_TransitionsToWorking()
    {
        var manager = new ModeManager();

        var result = manager.Fire(ModeTrigger.StartWorking);

        Assert.True(result);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);
    }

    [Fact]
    public void Fire_FinishPlanning_TransitionsPlanningToWorking()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartPlanning);

        var result = manager.Fire(ModeTrigger.FinishPlanning);

        Assert.True(result);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);
    }

    [Fact]
    public void Fire_RiskyOperation_TransitionsWorkingToHITL()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var result = manager.Fire(ModeTrigger.RiskyOperationDetected);

        Assert.True(result);
        Assert.Equal(AgentMode.HumanInTheLoop, manager.CurrentMode);
    }

    [Fact]
    public void Fire_UserApproved_TransitionsHITLToWorking()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);
        manager.Fire(ModeTrigger.RiskyOperationDetected);

        var result = manager.Fire(ModeTrigger.UserApproved);

        Assert.True(result);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);
    }

    [Fact]
    public void Fire_UserRejected_TransitionsHITLToIdle()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);
        manager.Fire(ModeTrigger.RiskyOperationDetected);

        var result = manager.Fire(ModeTrigger.UserRejected);

        Assert.True(result);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Fire_Complete_TransitionsWorkingToIdle()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var result = manager.Fire(ModeTrigger.Complete);

        Assert.True(result);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Fire_Reset_TransitionsAnyModeToIdle()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var result = manager.Fire(ModeTrigger.Reset);

        Assert.True(result);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Fire_InvalidTransition_ReturnsFalse()
    {
        var manager = new ModeManager();

        // Can't complete from Idle
        var result = manager.Fire(ModeTrigger.Complete);

        Assert.False(result);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void CanFire_ValidTransition_ReturnsTrue()
    {
        var manager = new ModeManager();

        Assert.True(manager.CanFire(ModeTrigger.StartPlanning));
        Assert.True(manager.CanFire(ModeTrigger.StartWorking));
    }

    [Fact]
    public void CanFire_InvalidTransition_ReturnsFalse()
    {
        var manager = new ModeManager();

        Assert.False(manager.CanFire(ModeTrigger.Complete));
        Assert.False(manager.CanFire(ModeTrigger.UserApproved));
    }

    [Fact]
    public void GetPermittedTriggers_FromIdle_ReturnsCorrectTriggers()
    {
        var manager = new ModeManager();

        var triggers = manager.GetPermittedTriggers();

        Assert.Contains(ModeTrigger.StartPlanning, triggers);
        Assert.Contains(ModeTrigger.StartWorking, triggers);
        Assert.Equal(2, triggers.Count);
    }

    [Fact]
    public void GetPermittedTriggers_FromWorking_ReturnsCorrectTriggers()
    {
        var manager = new ModeManager();
        manager.Fire(ModeTrigger.StartWorking);

        var triggers = manager.GetPermittedTriggers();

        Assert.Contains(ModeTrigger.RiskyOperationDetected, triggers);
        Assert.Contains(ModeTrigger.Complete, triggers);
        Assert.Contains(ModeTrigger.Reset, triggers);
        Assert.Contains(ModeTrigger.ReplanRequested, triggers);
        Assert.Equal(4, triggers.Count);
    }

    [Fact]
    public void ModeChanged_EventFiredOnTransition()
    {
        var manager = new ModeManager();
        ModeChangedEventArgs? capturedArgs = null;
        manager.ModeChanged += (_, args) => capturedArgs = args;

        manager.Fire(ModeTrigger.StartPlanning);

        Assert.NotNull(capturedArgs);
        Assert.Equal(AgentMode.Idle, capturedArgs.PreviousMode);
        Assert.Equal(AgentMode.Planning, capturedArgs.NewMode);
        Assert.Equal(ModeTrigger.StartPlanning, capturedArgs.Trigger);
    }

    [Fact]
    public void ModeChanged_NotFiredOnInvalidTransition()
    {
        var manager = new ModeManager();
        var eventFired = false;
        manager.ModeChanged += (_, _) => eventFired = true;

        manager.Fire(ModeTrigger.Complete); // Invalid from Idle

        Assert.False(eventFired);
    }

    [Fact]
    public void FullWorkflow_PlanToWorkToComplete()
    {
        var manager = new ModeManager();

        Assert.Equal(AgentMode.Idle, manager.CurrentMode);

        manager.Fire(ModeTrigger.StartPlanning);
        Assert.Equal(AgentMode.Planning, manager.CurrentMode);

        manager.Fire(ModeTrigger.FinishPlanning);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);

        manager.Fire(ModeTrigger.Complete);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void FullWorkflow_WorkWithHITLApproval()
    {
        var manager = new ModeManager();

        manager.Fire(ModeTrigger.StartWorking);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);

        manager.Fire(ModeTrigger.RiskyOperationDetected);
        Assert.Equal(AgentMode.HumanInTheLoop, manager.CurrentMode);

        manager.Fire(ModeTrigger.UserApproved);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);

        manager.Fire(ModeTrigger.Complete);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }
}
