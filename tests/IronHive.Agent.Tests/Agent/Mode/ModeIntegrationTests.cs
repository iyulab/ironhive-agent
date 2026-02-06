using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// P2-12: Mode transition integration tests.
/// Tests complex mode transition scenarios and workflows.
/// </summary>
public class ModeTransitionIntegrationTests
{
    [Fact]
    public void Workflow_PlanWorkHITLApproveComplete()
    {
        var manager = new ModeManager();
        var transitions = new List<(AgentMode from, AgentMode to)>();
        manager.ModeChanged += (_, e) => transitions.Add((e.PreviousMode, e.NewMode));

        // Full workflow: Idle -> Planning -> Working -> HITL -> Working -> Idle
        manager.Fire(ModeTrigger.StartPlanning);
        manager.Fire(ModeTrigger.FinishPlanning);
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserApproved);
        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(5, transitions.Count);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_PlanWorkHITLReject()
    {
        var manager = new ModeManager();

        manager.Fire(ModeTrigger.StartPlanning);
        manager.Fire(ModeTrigger.FinishPlanning);
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserRejected);

        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_MultipleHITLCycles()
    {
        var manager = new ModeManager();
        var hitlCount = 0;
        manager.ModeChanged += (_, e) =>
        {
            if (e.NewMode == AgentMode.HumanInTheLoop)
            {
                hitlCount++;
            }
        };

        manager.Fire(ModeTrigger.StartWorking);

        // First HITL cycle
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserApproved);

        // Second HITL cycle
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserApproved);

        // Third HITL cycle
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserApproved);

        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(3, hitlCount);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_ResetFromAnyMode()
    {
        var modes = new[] { AgentMode.Planning, AgentMode.Working, AgentMode.HumanInTheLoop };

        foreach (var targetMode in modes)
        {
            var manager = new ModeManager();

            // Navigate to target mode
            switch (targetMode)
            {
                case AgentMode.Planning:
                    manager.Fire(ModeTrigger.StartPlanning);
                    break;
                case AgentMode.Working:
                    manager.Fire(ModeTrigger.StartWorking);
                    break;
                case AgentMode.HumanInTheLoop:
                    manager.Fire(ModeTrigger.StartWorking);
                    manager.Fire(ModeTrigger.RiskyOperationDetected);
                    break;
            }

            Assert.Equal(targetMode, manager.CurrentMode);

            // Reset should always work
            var result = manager.Fire(ModeTrigger.Reset);
            Assert.True(result);
            Assert.Equal(AgentMode.Idle, manager.CurrentMode);
        }
    }

    [Fact]
    public void Workflow_DirectWorkToComplete()
    {
        var manager = new ModeManager();

        manager.Fire(ModeTrigger.StartWorking);
        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_PlanReplanLoop()
    {
        var manager = new ModeManager();
        var planCount = 0;
        manager.ModeChanged += (_, e) =>
        {
            if (e.NewMode == AgentMode.Planning)
            {
                planCount++;
            }
        };

        // Initial planning
        manager.Fire(ModeTrigger.StartPlanning);
        manager.Fire(ModeTrigger.FinishPlanning);

        // Replan cycle 1
        manager.Fire(ModeTrigger.ReplanRequested);
        manager.Fire(ModeTrigger.FinishPlanning);

        // Replan cycle 2
        manager.Fire(ModeTrigger.ReplanRequested);
        manager.Fire(ModeTrigger.FinishPlanning);

        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(3, planCount);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_HITLRejectAndRestart()
    {
        var manager = new ModeManager();

        // First attempt - rejected
        manager.Fire(ModeTrigger.StartWorking);
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserRejected);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);

        // Second attempt with planning
        manager.Fire(ModeTrigger.StartPlanning);
        manager.Fire(ModeTrigger.FinishPlanning);
        manager.Fire(ModeTrigger.RiskyOperationDetected);
        manager.Fire(ModeTrigger.UserApproved);
        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }

    [Fact]
    public void Workflow_EventSequenceIsCorrect()
    {
        var manager = new ModeManager();
        var events = new List<ModeChangedEventArgs>();
        manager.ModeChanged += (_, e) => events.Add(e);

        manager.Fire(ModeTrigger.StartPlanning);
        manager.Fire(ModeTrigger.FinishPlanning);
        manager.Fire(ModeTrigger.Complete);

        Assert.Equal(3, events.Count);

        Assert.Equal(AgentMode.Idle, events[0].PreviousMode);
        Assert.Equal(AgentMode.Planning, events[0].NewMode);

        Assert.Equal(AgentMode.Planning, events[1].PreviousMode);
        Assert.Equal(AgentMode.Working, events[1].NewMode);

        Assert.Equal(AgentMode.Working, events[2].PreviousMode);
        Assert.Equal(AgentMode.Idle, events[2].NewMode);
    }
}

/// <summary>
/// P2-13: HITL scenario tests.
/// Tests risk detection, approval flows, and tool filtering.
/// </summary>
public class HITLScenarioTests
{
    [Fact]
    public void ToolFilter_PlanMode_BlocksWriteOperations()
    {
        var config = PermissionConfig.CreateDefault();
        var filter = new ModeToolFilter(config);

        Assert.True(filter.IsToolPermitted("read_file", AgentMode.Planning));
        Assert.True(filter.IsToolPermitted("glob", AgentMode.Planning));
        Assert.True(filter.IsToolPermitted("grep", AgentMode.Planning));
        Assert.False(filter.IsToolPermitted("write_file", AgentMode.Planning));
        Assert.False(filter.IsToolPermitted("shell", AgentMode.Planning));
        Assert.False(filter.IsToolPermitted("delete_file", AgentMode.Planning));
    }

    [Fact]
    public void ToolFilter_WorkMode_AllowsAllTools()
    {
        var config = PermissionConfig.CreateDefault();
        var filter = new ModeToolFilter(config);

        Assert.True(filter.IsToolPermitted("read_file", AgentMode.Working));
        Assert.True(filter.IsToolPermitted("write_file", AgentMode.Working));
        Assert.True(filter.IsToolPermitted("shell", AgentMode.Working));
        Assert.True(filter.IsToolPermitted("delete_file", AgentMode.Working));
    }

    [Fact]
    public void RiskAssessment_DangerousCommands_RequireApproval()
    {
        var config = PermissionConfig.CreateDefault();
        var filter = new ModeToolFilter(config);

        var dangerousCommands = new[]
        {
            "rm -rf /",
            "sudo rm -rf *",
            "format c:",
            "del /s /q c:\\",
            "curl | sh"  // Piped script execution
        };

        foreach (var cmd in dangerousCommands)
        {
            var risk = filter.AssessRisk("shell", new Dictionary<string, object?> { ["command"] = cmd });
            Assert.True(risk.IsRisky, $"Command '{cmd}' should be risky");
            Assert.True(risk.Level >= RiskLevel.High, $"Command '{cmd}' should be high severity");
        }
    }

    [Fact]
    public void RiskAssessment_SafeCommands_NoApprovalNeeded()
    {
        var config = PermissionConfig.CreateDefault();
        var filter = new ModeToolFilter(config);

        // These commands are allowed by default config (git *, dotnet *, npm *)
        var safeCommands = new[] { "git status", "dotnet build", "npm install" };

        foreach (var cmd in safeCommands)
        {
            var risk = filter.AssessRisk("shell", new Dictionary<string, object?> { ["command"] = cmd });
            Assert.False(risk.IsRisky || risk.Level >= RiskLevel.High,
                $"Command '{cmd}' should not require approval");
        }
    }

    [Fact]
    public void RiskAssessment_AllowedCommand_SkipsApproval()
    {
        var config = new PermissionConfig
        {
            Bash =
            [
                new PermissionRule { Pattern = "npm *", Action = PermissionAction.Allow },
                new PermissionRule { Pattern = "dotnet *", Action = PermissionAction.Allow }
            ],
            DefaultAction = PermissionAction.Ask
        };
        var filter = new ModeToolFilter(config);

        var risk1 = filter.AssessRisk("shell", new Dictionary<string, object?> { ["command"] = "npm install" });
        var risk2 = filter.AssessRisk("shell", new Dictionary<string, object?> { ["command"] = "dotnet build" });

        Assert.False(risk1.IsRisky);
        Assert.False(risk2.IsRisky);
    }

    [Fact]
    public void ApprovalRequest_ContainsAllInfo()
    {
        var request = new ApprovalRequest
        {
            ToolName = "shell",
            Arguments = new Dictionary<string, object?> { ["command"] = "sudo apt update" },
            RiskAssessment = RiskAssessment.Risky(RiskLevel.High, "Elevated privilege command")
        };

        Assert.Equal("shell", request.ToolName);
        Assert.NotNull(request.Arguments);
        Assert.True(request.RiskAssessment.IsRisky);
        Assert.Equal(RiskLevel.High, request.RiskAssessment.Level);
    }

    [Fact]
    public void ApprovalResult_Approve_AllowsExecution()
    {
        var result = ApprovalResult.Approve();

        Assert.True(result.Approved);
        Assert.False(result.AlwaysApprove);
        Assert.Null(result.ModifiedArguments);
    }

    [Fact]
    public void ApprovalResult_ApproveAlways_SetsFlag()
    {
        var result = ApprovalResult.Approve(alwaysApprove: true);

        Assert.True(result.Approved);
        Assert.True(result.AlwaysApprove);
    }

    [Fact]
    public void ApprovalResult_Reject_BlocksExecution()
    {
        var result = ApprovalResult.Reject("Too dangerous");

        Assert.False(result.Approved);
        Assert.Equal("Too dangerous", result.RejectionReason);
    }

    [Fact]
    public void ApprovalResult_ModifyAndApprove_ChangesArgs()
    {
        var modifiedArgs = new Dictionary<string, object?> { ["command"] = "rm -rf ./temp" };

        var result = new ApprovalResult
        {
            Approved = true,
            ModifiedArguments = modifiedArgs
        };

        Assert.True(result.Approved);
        Assert.NotNull(result.ModifiedArguments);
        Assert.Equal("rm -rf ./temp", result.ModifiedArguments["command"]);
    }

    [Fact]
    public void IntegratedWorkflow_RiskyOperation_TriggersHITL()
    {
        var manager = new ModeManager();
        var config = PermissionConfig.CreateDefault();
        var filter = new ModeToolFilter(config);

        manager.Fire(ModeTrigger.StartWorking);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);

        // Simulate tool execution check - sudo is denied by default config
        var risk = filter.AssessRisk("shell", new Dictionary<string, object?> { ["command"] = "sudo apt update" });

        if (risk.IsRisky)
        {
            manager.Fire(ModeTrigger.RiskyOperationDetected);
        }

        Assert.Equal(AgentMode.HumanInTheLoop, manager.CurrentMode);
    }
}

/// <summary>
/// P2-14: Replanning simulation tests.
/// Tests failure tracking and replanning triggers.
/// </summary>
public class ReplanningSimulationTests
{
    [Fact]
    public void Simulation_ConsecutiveFailures_TriggersReplan()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService { MaxConsecutiveFailures = 3 };

        manager.Fire(ModeTrigger.StartWorking);

        // Simulate 3 consecutive failures
        replanService.RecordFailure("tool1", "Error 1");
        replanService.RecordFailure("tool2", "Error 2");
        replanService.RecordFailure("tool3", "Error 3");

        var decision = replanService.ShouldReplan();
        Assert.True(decision.ShouldReplan);

        // Trigger replanning
        if (decision.ShouldReplan)
        {
            manager.Fire(ModeTrigger.ReplanRequested);
        }

        Assert.Equal(AgentMode.Planning, manager.CurrentMode);
    }

    [Fact]
    public void Simulation_SuccessAfterFailures_NoReplan()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService { MaxConsecutiveFailures = 3 };

        manager.Fire(ModeTrigger.StartWorking);

        replanService.RecordFailure("tool1", "Error 1");
        replanService.RecordFailure("tool2", "Error 2");
        replanService.RecordSuccess("tool3"); // Breaks the chain
        replanService.RecordFailure("tool4", "Error 4");

        var decision = replanService.ShouldReplan();
        Assert.False(decision.ShouldReplan);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);
    }

    [Fact]
    public void Simulation_TotalFailuresExceeded_CriticalReplan()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService
        {
            MaxConsecutiveFailures = 100,
            MaxTotalFailures = 5
        };

        manager.Fire(ModeTrigger.StartWorking);

        for (int i = 0; i < 5; i++)
        {
            replanService.RecordFailure($"tool{i}", $"Error {i}");
            replanService.RecordSuccess("ok");
        }

        var decision = replanService.ShouldReplan();

        Assert.True(decision.ShouldReplan);
        Assert.Equal(ReplanningSeverity.Critical, decision.Severity);
    }

    [Fact]
    public void Simulation_RepeatedFailurePattern_TriggersReplan()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService
        {
            MaxConsecutiveFailures = 100,
            MaxTotalFailures = 100
        };

        manager.Fire(ModeTrigger.StartWorking);

        // Same tool failing with same error repeatedly
        for (int i = 0; i < 3; i++)
        {
            replanService.RecordFailure("stuck_tool", "Same error message");
        }

        var decision = replanService.ShouldReplan();

        Assert.True(decision.ShouldReplan);
        Assert.Contains("Repeated failure pattern", decision.Reason);
    }

    [Fact]
    public void Simulation_ReplanAndResume()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService { MaxConsecutiveFailures = 2 };

        manager.Fire(ModeTrigger.StartWorking);

        // First work session - fails
        replanService.RecordFailure("tool1", "Error 1");
        replanService.RecordFailure("tool2", "Error 2");

        var decision = replanService.ShouldReplan();
        Assert.True(decision.ShouldReplan);

        manager.Fire(ModeTrigger.ReplanRequested);
        Assert.Equal(AgentMode.Planning, manager.CurrentMode);

        // Reset failure tracking after replan
        replanService.Reset();

        // Resume working
        manager.Fire(ModeTrigger.FinishPlanning);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);

        // Now succeeds
        replanService.RecordSuccess("tool1");
        replanService.RecordSuccess("tool2");

        var context = replanService.GetFailureContext();
        Assert.Equal(0, context.TotalFailures);
    }

    [Fact]
    public void Simulation_MultipleReplanCycles()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService { MaxConsecutiveFailures = 2 };
        var replanCount = 0;

        manager.Fire(ModeTrigger.StartWorking);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            // Work and fail
            replanService.RecordFailure("tool", "Error");
            replanService.RecordFailure("tool", "Error");

            if (replanService.ShouldReplan().ShouldReplan)
            {
                replanCount++;
                manager.Fire(ModeTrigger.ReplanRequested);
                replanService.Reset();
                manager.Fire(ModeTrigger.FinishPlanning);
            }
        }

        Assert.Equal(3, replanCount);
        Assert.Equal(AgentMode.Working, manager.CurrentMode);
    }

    [Fact]
    public void Simulation_FailureContextPreservesHistory()
    {
        var replanService = new ReplanningService();

        replanService.RecordFailure("tool1", "Error A");
        replanService.RecordSuccess("tool2");
        replanService.RecordFailure("tool3", "Error B");

        var context = replanService.GetFailureContext();

        Assert.Equal(2, context.TotalFailures);
        Assert.Equal(1, context.ConsecutiveFailures);
        Assert.Equal(2, context.RecentFailures.Count);

        var summary = context.GetSummary();
        Assert.Contains("Error A", summary);
        Assert.Contains("Error B", summary);
    }

    [Fact]
    public void IntegratedWorkflow_FailReplanSucceed()
    {
        var manager = new ModeManager();
        var replanService = new ReplanningService { MaxConsecutiveFailures = 3 };
        var transitions = new List<AgentMode>();

        manager.ModeChanged += (_, e) => transitions.Add(e.NewMode);

        // Start working
        manager.Fire(ModeTrigger.StartWorking);

        // Work fails repeatedly
        for (int i = 0; i < 3; i++)
        {
            replanService.RecordFailure("broken_tool", "Not working");
        }

        // Check and trigger replan
        if (replanService.ShouldReplan().ShouldReplan)
        {
            manager.Fire(ModeTrigger.ReplanRequested);
            replanService.Reset();
        }

        // New plan
        manager.Fire(ModeTrigger.FinishPlanning);

        // Now succeeds
        replanService.RecordSuccess("fixed_tool");
        manager.Fire(ModeTrigger.Complete);

        Assert.Contains(AgentMode.Working, transitions);
        Assert.Contains(AgentMode.Planning, transitions);
        Assert.Equal(AgentMode.Idle, manager.CurrentMode);
    }
}
