using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// An approver that judges one act at a time (a classifier or judge model answering for a person) says its yes covers this
/// call only (<see cref="ApprovalResult.ThisCallOnly"/>), so a policy wrapped around the approval service does not widen
/// it to the calls that follow. The property rides the result; the gate itself never remembers an approval.
/// </summary>
public class ApprovalResultScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AnApprovalCoversWhatTheApproversPolicySays_UntilItNamesThisCallOnly()
    {
        Assert.False(ApprovalResult.Approve().ThisCallOnly);
        Assert.False(ApprovalResult.Approve(alwaysApprove: true).ThisCallOnly);
        Assert.False(ApprovalResult.Reject("no").ThisCallOnly);
        Assert.False(ApprovalResult.Defer("later").ThisCallOnly);
        Assert.False(new ApprovalResult { Approved = true }.ThisCallOnly);
    }

    [Fact]
    public void ApproveThisCallOnly_IsApprovedAndNothingMore()
    {
        var result = ApprovalResult.ApproveThisCallOnly();

        Assert.True(result.Approved);
        Assert.True(result.ThisCallOnly);
        Assert.False(result.AlwaysApprove);
        Assert.False(result.Deferred);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public async Task TheGateAsksAgainForTheNextCall_WhateverTheFirstAnswerCovered()
    {
        var policy = Substitute.For<IToolCallPolicy>();
        policy.Evaluate(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>())
            .Returns(new RiskAssessment { IsRisky = true, Verdict = PermissionAction.Ask, Reason = "writes a file" });
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalResult.ApproveThisCallOnly());
        var gate = new ApprovalGate(policy, approval);

        await gate.DecideAsync("write_file", null, Ct);
        await gate.DecideAsync("write_file", null, Ct);

        await approval.Received(2).RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>());
    }
}
