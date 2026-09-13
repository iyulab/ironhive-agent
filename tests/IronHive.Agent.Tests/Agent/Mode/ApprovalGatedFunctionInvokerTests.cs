using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// The library declares the approval abstraction; these facts pin that something actually consults it.
/// Every fact drives a real <c>FunctionInvokingChatClient</c> (the middleware a consumer wraps its
/// client with) through an <see cref="AgentLoop"/>, so "the tool did not run" is observed where a
/// consumer would observe it — on the function result the model receives — not on a mock's call count.
/// </summary>
public class ApprovalGatedFunctionInvokerTests
{
    private sealed class Probe
    {
        public int Invocations;
        public string? LastPath;

        [System.ComponentModel.Description("Writes a file")]
        public string WriteFile(string path, string content)
        {
            Invocations++;
            LastPath = path;
            return $"wrote {path}";
        }

        [System.ComponentModel.Description("Looks something up")]
        public string Lookup(string query)
        {
            Invocations++;
            return $"found {query}";
        }
    }

    private static (AgentLoop Loop, Probe Probe, MockChatClient Mock) Build(
        PermissionConfig config,
        IHumanApprovalService? approval,
        string toolName,
        string argumentsJson)
    {
        var probe = new Probe();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(probe.WriteFile, "WriteFile"),
            AIFunctionFactory.Create(probe.Lookup, "Lookup")
        };

        var mock = new MockChatClient()
            .EnqueueToolCallResponse(toolName, argumentsJson)
            .EnqueueResponse("done");

        var filter = new ModeToolFilter(config);
        var client = mock.AsBuilder()
            .UseFunctionInvocation(configure: c =>
                c.FunctionInvoker = ApprovalGatedFunctionInvoker.Create(filter, approval))
            .Build();

        var loop = new AgentLoop(client, new AgentOptions { Tools = tools });
        return (loop, probe, mock);
    }

    private static PermissionConfig ConfigWith(Action<PermissionConfig> mutate)
    {
        var config = PermissionConfig.CreateDefault();
        mutate(config);
        return config;
    }

    [Fact]
    public async Task AllowVerdict_InvokesTheTool()
    {
        var config = ConfigWith(c => c.Edit.Add(new PermissionRule { Pattern = "notes/**", Action = PermissionAction.Allow, Priority = 50 }));
        var (loop, probe, _) = Build(config, approval: null, "WriteFile", """{"path":"notes/a.txt","content":"x"}""");

        var response = await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Invocations);
        Assert.True(response.ToolCalls[0].Success);
        Assert.Equal("wrote notes/a.txt", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task DenyVerdict_DoesNotInvoke_AndReturnsTheReasonAsTheResult()
    {
        var config = ConfigWith(c => c.Edit.Add(new PermissionRule { Pattern = "**/secrets/**", Action = PermissionAction.Deny, Priority = 100, Reason = "Protected directory" }));
        var approval = Substitute.For<IHumanApprovalService>();
        var (loop, probe, _) = Build(config, approval, "WriteFile", """{"path":"vault/secrets/k.txt","content":"x"}""");

        var response = await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal(0, probe.Invocations);
        Assert.Contains("Permission denied", response.ToolCalls[0].Result);
        Assert.Contains("Protected directory", response.ToolCalls[0].Result);
        await approval.DidNotReceive().RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskVerdict_ConsultsTheApprovalService_AndInvokesOnApproval()
    {
        // *.json is an Ask rule in the default config.
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalResult.Approve());
        var (loop, probe, _) = Build(PermissionConfig.CreateDefault(), approval, "WriteFile", """{"path":"app.json","content":"{}"}""");

        var response = await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Invocations);
        Assert.Equal("wrote app.json", response.ToolCalls[0].Result);
        await approval.Received(1).RequestApprovalAsync(
            Arg.Is<ApprovalRequest>(r => r.ToolName == "WriteFile" && r.RiskAssessment.RequiresApproval && r.Arguments!["path"]!.ToString() == "app.json"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskVerdict_Rejected_DoesNotInvoke_AndReturnsTheRejectionReason()
    {
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalResult.Reject("not today"));
        var (loop, probe, _) = Build(PermissionConfig.CreateDefault(), approval, "WriteFile", """{"path":"app.json","content":"{}"}""");

        var response = await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal(0, probe.Invocations);
        Assert.Contains("Approval rejected", response.ToolCalls[0].Result);
        Assert.Contains("not today", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task AskVerdict_WithNoApprovalService_IsRefused_NotPassedThrough()
    {
        var (loop, probe, _) = Build(PermissionConfig.CreateDefault(), approval: null, "WriteFile", """{"path":"app.json","content":"{}"}""");

        var response = await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal(0, probe.Invocations);
        Assert.Contains("no approval service is configured", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task ModifiedArguments_FromTheApprover_ReachTheTool()
    {
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalResult
            {
                Approved = true,
                ModifiedArguments = new Dictionary<string, object?> { ["path"] = "app.local.json" }
            });
        var (loop, probe, _) = Build(PermissionConfig.CreateDefault(), approval, "WriteFile", """{"path":"app.json","content":"{}"}""");

        await loop.RunAsync("write", TestContext.Current.CancellationToken);

        Assert.Equal("app.local.json", probe.LastPath);
    }

    [Fact]
    public async Task UnknownToolName_FallsToTheToolsRules_ThenToDefaultAction()
    {
        // "Lookup" matches no category and no Tools rule: the default Ask applies, so it is asked about.
        var approval = Substitute.For<IHumanApprovalService>();
        approval.RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>())
            .Returns(ApprovalResult.Approve());
        var (loop, probe, _) = Build(PermissionConfig.CreateDefault(), approval, "Lookup", """{"query":"q"}""");

        await loop.RunAsync("look", TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Invocations);
        await approval.Received(1).RequestApprovalAsync(Arg.Is<ApprovalRequest>(r => r.ToolName == "Lookup"), Arg.Any<CancellationToken>());

        // ...and a Tools rule that allows it removes the question.
        var allowed = ConfigWith(c => c.Tools.Add(new PermissionRule { Pattern = "Lookup", Action = PermissionAction.Allow, Priority = 10 }));
        var approval2 = Substitute.For<IHumanApprovalService>();
        var (loop2, probe2, _) = Build(allowed, approval2, "Lookup", """{"query":"q"}""");

        await loop2.RunAsync("look", TestContext.Current.CancellationToken);

        Assert.Equal(1, probe2.Invocations);
        await approval2.DidNotReceive().RequestApprovalAsync(Arg.Any<ApprovalRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InnerInvoker_RunsApprovedCalls()
    {
        var inner = 0;
        var filter = new ModeToolFilter(ConfigWith(c => c.Tools.Add(new PermissionRule { Pattern = "Lookup", Action = PermissionAction.Allow })));
        var invoker = ApprovalGatedFunctionInvoker.Create(filter, approvalService: null, inner: (ctx, ct) =>
        {
            inner++;
            return new ValueTask<object?>("from inner");
        });
        var probe = new Probe();
        var mock = new MockChatClient().EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done");
        var client = mock.AsBuilder().UseFunctionInvocation(configure: c => c.FunctionInvoker = invoker).Build();
        var loop = new AgentLoop(client, new AgentOptions { Tools = [AIFunctionFactory.Create(probe.Lookup, "Lookup")] });

        var response = await loop.RunAsync("look", TestContext.Current.CancellationToken);

        Assert.Equal(1, inner);
        Assert.Equal(0, probe.Invocations);
        Assert.Equal("from inner", response.ToolCalls[0].Result);
    }
}
