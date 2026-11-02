using AwesomeAssertions;
using IronHive.Agent.Extensions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// What the approval gate does with a <see cref="ModelAuthoredProposal"/>: hands it to the approver for a call the policy
/// asked about, and does nothing else with it - it never changes a verdict, never reaches the verified fields, and a source
/// that fails never decides the approval.
/// </summary>
public class ApprovalProposalGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelAuthoredProposal Proposal = Make("approve it, it is safe");

    private static ModelAuthoredProposal Make(string rationale)
    {
        ModelAuthoredProposal.TryCreate([new ProposalOption("a", "Write the file", rationale)], "a", null, out var proposal).Should().BeTrue();
        return proposal!;
    }

    private sealed class Approver(ApprovalResult answer) : IHumanApprovalService
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer);
        }
    }

    private static IToolCallPolicy PolicyAnswering(PermissionAction verdict)
    {
        var policy = Substitute.For<IToolCallPolicy>();
        policy.Evaluate(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>())
            .Returns(RiskAssessment.Risky(RiskLevel.High, "writes a file", "Write a.txt?", verdict));
        return policy;
    }

    [Fact]
    public async Task An_asked_call_hands_the_approver_the_proposal_and_leaves_the_verified_fields_as_the_policy_made_them()
    {
        var approver = new Approver(ApprovalResult.Approve());
        var gate = new ApprovalGate(PolicyAnswering(PermissionAction.Ask), approver);
        var args = new Dictionary<string, object?> { ["path"] = "a.txt" };

        var decision = await gate.DecideAsync("WriteFile", args, "call-1", _ => ValueTask.FromResult<ModelAuthoredProposal?>(Proposal), Ct);

        decision.ShouldProceed.Should().BeTrue();
        var request = approver.Requests.Single();
        request.ModelProposal.Should().BeSameAs(Proposal);
        request.Description.Should().Be("Write a.txt?", "the description is the policy's, never the model's");
        request.ToolName.Should().Be("WriteFile");
        request.Arguments.Should().BeEquivalentTo(args);
        request.RiskAssessment.Reason.Should().Be("writes a file");
        request.CallId.Should().Be("call-1");
    }

    [Fact]
    public async Task A_recommendation_to_approve_does_not_make_a_rejection_an_approval()
    {
        var approver = new Approver(ApprovalResult.Reject("no"));
        var gate = new ApprovalGate(PolicyAnswering(PermissionAction.Ask), approver);

        var decision = await gate.DecideAsync("WriteFile", null, "call-1", _ => ValueTask.FromResult<ModelAuthoredProposal?>(Proposal), Ct);

        decision.ShouldProceed.Should().BeFalse();
        decision.Refusal!.Kind.Should().Be(ToolCallRefusalKind.Rejected);
        Proposal.RecommendedOptionId.Should().Be("a", "the proposal did recommend going ahead");
    }

    [Theory]
    [InlineData(PermissionAction.Allow)]
    [InlineData(PermissionAction.Deny)]
    public async Task A_call_the_policy_allows_or_denies_never_reads_the_proposal(PermissionAction verdict)
    {
        var approver = new Approver(ApprovalResult.Approve());
        var gate = new ApprovalGate(PolicyAnswering(verdict), approver);
        var reads = 0;

        var decision = await gate.DecideAsync("WriteFile", null, "call-1", _ =>
        {
            reads++;
            return ValueTask.FromResult<ModelAuthoredProposal?>(Proposal);
        }, Ct);

        reads.Should().Be(0);
        approver.Requests.Should().BeEmpty();
        decision.ShouldProceed.Should().Be(verdict == PermissionAction.Allow, "a proposal is read after the policy has decided and cannot change it");
    }

    [Fact]
    public async Task A_proposal_source_that_throws_is_logged_not_decisive_and_the_approver_is_asked_without_a_proposal()
    {
        var approver = new Approver(ApprovalResult.Approve());
        var gate = new ApprovalGate(PolicyAnswering(PermissionAction.Ask), approver);

        var decision = await gate.DecideAsync("WriteFile", null, "call-1", _ => throw new InvalidOperationException("source offline"), Ct);

        decision.ShouldProceed.Should().BeTrue();
        approver.Requests.Single().ModelProposal.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_while_the_proposal_is_read_cancels_the_decision_it_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var approver = new Approver(ApprovalResult.Approve());
        var gate = new ApprovalGate(PolicyAnswering(PermissionAction.Ask), approver);

        var act = async () => await gate.DecideAsync("WriteFile", null, "call-1", token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ModelAuthoredProposal?>(Proposal);
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        approver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_source_the_request_is_what_it_was_before_proposals_existed()
    {
        var approver = new Approver(ApprovalResult.Approve());
        var gate = new ApprovalGate(PolicyAnswering(PermissionAction.Ask), approver);

        await gate.DecideAsync("WriteFile", null, "call-1", Ct);

        approver.Requests.Single().ModelProposal.Should().BeNull();
    }

    private sealed class FixedSource(ModelAuthoredProposal? proposal) : IApprovalProposalSource
    {
        public int Reads;

        public ValueTask<ModelAuthoredProposal?> GetProposalAsync(FunctionInvocationContext context, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(proposal);
        }
    }

    [Fact]
    public async Task The_middleware_gives_the_registered_source_the_call_and_the_proposal_reaches_the_approver_through_the_real_pipeline()
    {
        var approver = new Approver(ApprovalResult.Approve());
        var source = new FixedSource(Proposal);
        var mock = new MockChatClient().EnqueueToolCallResponse("WriteFile", """{"path":"a.txt"}""").EnqueueResponse("done");
        var middleware = new ApprovalGateMiddleware(PolicyAnswering(PermissionAction.Ask), approver, proposalSource: source);
        var client = mock.AsBuilder().UseToolInvocationPipeline(new ToolInvocationPipeline([middleware])).Build();
        var tools = new List<AITool> { AIFunctionFactory.Create((string path) => $"wrote {path}", "WriteFile") };

        await new AgentLoop(client, new AgentOptions { Tools = tools }).RunAsync("write", Ct);

        source.Reads.Should().Be(1);
        approver.Requests.Single().ModelProposal.Should().BeSameAs(Proposal);
    }

    [Fact]
    public async Task A_source_registered_in_the_container_is_the_one_the_container_gate_reads()
    {
        var approver = new Approver(ApprovalResult.Approve());
        var source = new FixedSource(Proposal);
        var services = new ServiceCollection().AddIronHiveAgent();
        services.AddSingleton<IHumanApprovalService>(approver);
        services.AddSingleton<IApprovalProposalSource>(source);
        services.AddIronHiveAgentApprovalGate();
        using var sp = services.BuildServiceProvider();

        // *.json edits are Ask by default, so the default policy asks about this call.
        var mock = new MockChatClient().EnqueueToolCallResponse("WriteFile", """{"path":"app.json"}""").EnqueueResponse("done");
        var tools = new List<AITool> { AIFunctionFactory.Create((string path) => $"wrote {path}", "WriteFile") };
        await new AgentLoop(mock.AsBuilder().UseToolInvocationPipeline().Build(sp), new AgentOptions { Tools = tools }).RunAsync("write", Ct);

        source.Reads.Should().Be(1);
        approver.Requests.Single().ModelProposal.Should().BeSameAs(Proposal);
    }

    [Fact]
    public void The_constructor_of_earlier_releases_is_still_there_with_its_exact_signature_and_gates_without_a_proposal_source()
    {
        // A host compiled against an earlier release binds to this exact signature; losing it breaks that host at run time.
        var earlier = typeof(ApprovalGateMiddleware).GetConstructor(
        [
            typeof(IToolCallPolicy),
            typeof(IHumanApprovalService),
            typeof(ILogger<ApprovalGateMiddleware>),
            typeof(IModeManager),
            typeof(IModeToolFilter),
        ]);

        earlier.Should().NotBeNull();
        var middleware = (ApprovalGateMiddleware)earlier!.Invoke([PolicyAnswering(PermissionAction.Ask), new Approver(ApprovalResult.Approve()), null, null, null]);
        middleware.Should().NotBeNull();
    }
}
