using IronHive.Agent.Delegation;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// A delegated run calls its tools through the session's approval path; the request the person sees names the
/// delegated agent that asked (<see cref="ApprovalRequest.DelegatedBy"/>), and nothing for the session's own calls.
/// </summary>
public class ApprovalRequestDelegatedByTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ApprovalGate Gate, List<ApprovalRequest> Seen) Build()
    {
        var policy = Substitute.For<IToolCallPolicy>();
        policy.Evaluate(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>())
            .Returns(new RiskAssessment { IsRisky = true, Verdict = PermissionAction.Ask, Reason = "writes a file" });
        var seen = new List<ApprovalRequest>();
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Do<ApprovalRequest>(seen.Add), Arg.Any<CancellationToken>())
            .Returns(new ApprovalResult { Approved = true });
        return (new ApprovalGate(policy, approval), seen);
    }

    [Fact]
    public async Task OutsideADelegatedRun_TheRequestNamesNoAgent()
    {
        var (gate, seen) = Build();

        await gate.DecideAsync("write_file", null, Ct);

        Assert.Null(Assert.Single(seen).DelegatedBy);
    }

    [Fact]
    public async Task InsideADelegatedRun_TheRequestNamesTheInnermostAgent_AndLeavingRestoresTheOuter()
    {
        var (gate, seen) = Build();

        using (DelegationScope.Enter("researcher"))
        {
            await gate.DecideAsync("write_file", null, Ct);
            using (DelegationScope.Enter("summarizer"))
            {
                await gate.DecideAsync("write_file", null, Ct);
            }

            await gate.DecideAsync("write_file", null, Ct);
        }

        await gate.DecideAsync("write_file", null, Ct);

        Assert.Equal(["researcher", "summarizer", "researcher", null], seen.Select(r => r.DelegatedBy));
    }

    [Fact]
    public async Task TheScopeFlowsAcrossAwaits_AndDoesNotLeakIntoAParallelFlow()
    {
        var (gate, seen) = Build();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var delegated = Task.Run(async () =>
        {
            using var scope = DelegationScope.Enter("researcher");
            entered.SetResult();
            await release.Task;
            await gate.DecideAsync("write_file", null, Ct);
        }, Ct);

        await entered.Task;
        Assert.Null(DelegationScope.AgentName);
        release.SetResult();
        await delegated;

        Assert.Equal("researcher", Assert.Single(seen).DelegatedBy);
    }
}
