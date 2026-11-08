using System.Text.Json;
using IndexThinking.Agents;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using NSubstitute;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A call's reported outcome and the loop guards read a tool's result by the same rule: a failure carried as a value
/// (an MCP result with <c>isError: true</c>, or what <see cref="ToolInvocationOptions.FailureOf"/> recognises) is not a
/// success on the wire.
/// </summary>
public class ToolFailureOutcomeTests
{
    private const string RefusedByServer = """{"content":[{"type":"text","text":"the approver refused this call"}],"isError":true}""";
    private const string Fine = """{"content":[{"type":"text","text":"done"}],"isError":false}""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task RunAsync_ReportsAnMcpErrorResultAsAFailure()
    {
        var client = new MockChatClient().EnqueueResolvedToolCallResponseWithValue("srv_act", "{}", Json(RefusedByServer));

        var response = await new AgentLoop(client).RunAsync("act", TestContext.Current.CancellationToken);

        Assert.False(response.ToolCalls[0].Success);
        Assert.Null(response.ToolCalls[0].RefusalKind); // the tool ran and reported failure - nothing refused it here
        Assert.Contains("the approver refused this call", response.ToolCalls[0].Result);
    }

    [Fact]
    public async Task RunAsync_ReportsAnMcpSuccessResultAsASuccess()
    {
        var client = new MockChatClient().EnqueueResolvedToolCallResponseWithValue("srv_act", "{}", Json(Fine));

        var response = await new AgentLoop(client).RunAsync("act", TestContext.Current.CancellationToken);

        Assert.True(response.ToolCalls[0].Success);
    }

    [Fact]
    public async Task RunAsync_ReportsAProtocolErrorObjectAndAnMcpToolResultAsFailures()
    {
        var protocol = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "no" }] };
        var wrapped = new McpToolResult { IsError = true, Content = "no" };

        foreach (var value in new object[] { protocol, wrapped })
        {
            var client = new MockChatClient().EnqueueResolvedToolCallResponseWithValue("srv_act", "{}", value);

            var response = await new AgentLoop(client).RunAsync("act", TestContext.Current.CancellationToken);

            Assert.False(response.ToolCalls[0].Success);
        }
    }

    [Fact]
    public async Task RunStreamingAsync_ReportsAnMcpErrorResultAsAFailureInTheArrivedChunkAndTheTurnRecord()
    {
        var client = new MockChatClient().EnqueueResolvedToolCallResponseWithValue("srv_act", "{}", Json(RefusedByServer));

        var arrived = new List<ToolCallResult>();
        TurnRecord? turn = null;
        await foreach (var chunk in new AgentLoop(client).RunStreamingAsync("act", TestContext.Current.CancellationToken))
        {
            if (chunk.ToolResult is { } result)
            {
                arrived.Add(result);
            }

            turn = chunk.Turn ?? turn;
        }

        Assert.All(arrived, call => Assert.False(call.Success));
        Assert.False(Assert.Single(turn!.ToolCalls).Success);
    }

    [Fact]
    public async Task ThinkingAgentLoop_ReportsAnMcpErrorResultAsAFailure()
    {
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "srv_act", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", Json(RefusedByServer))]),
        ]);
        var manager = Substitute.For<IThinkingTurnManager>();
        manager.ProcessTurnAsync(Arg.Any<ThinkingContext>(), Arg.Any<Func<IList<ChatMessage>, CancellationToken, Task<ChatResponse>>>())
            .Returns(Task.FromResult(TurnResult.Success(response, TurnMetrics.Empty, null)));

        var result = await new ThinkingAgentLoop(new MockChatClient(), manager).RunAsync("act", TestContext.Current.CancellationToken);

        Assert.False(result.ToolCalls[0].Success);
    }

    [Fact]
    public void Extract_RecognisesAHostFailureShapeThroughTheOptionsAndOtherwiseLeavesItASuccess()
    {
        var response = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "run", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "ERROR: disk full")]),
        ]);
        var options = new ToolInvocationOptions { FailureOf = r => r is string s && s.StartsWith("ERROR:", StringComparison.Ordinal) ? s : null };

        Assert.False(Assert.Single(ToolCallResultFactory.Extract(response, options)).Success);
        Assert.True(Assert.Single(ToolCallResultFactory.Extract(response)).Success); // no host rule: a plain string is a value
    }

    [Fact]
    public void ForArrivedResults_AppliesTheSameRuleAsExtract()
    {
        var call = new FunctionCallContent("c1", "run", new Dictionary<string, object?>());
        var result = new FunctionResultContent("c1", Json(RefusedByServer));

        var arrived = Assert.Single(ToolCallResultFactory.ForArrivedResults([call], [result]));
        var extracted = Assert.Single(ToolCallResultFactory.Extract([call], [result]));

        Assert.False(arrived.Success);
        Assert.Equal(extracted.Success, arrived.Success);
    }

    [Fact]
    public async Task RunAsync_AppliesTheHostRuleOfThePipelineTheChatClientCarries()
    {
        var options = new ToolInvocationOptions { FailureOf = r => r is string s && s.StartsWith("ERROR:", StringComparison.Ordinal) ? s : null };
        var inner = new MockChatClient().EnqueueResolvedToolCallResponseWithValue("run", "{}", "ERROR: disk full");
        var client = new PipelineCarryingClient(inner, ToolInvocationPipeline.CreateDefault(options));

        var response = await new AgentLoop(client).RunAsync("run", TestContext.Current.CancellationToken);

        Assert.False(response.ToolCalls[0].Success);
    }

    private sealed class PipelineCarryingClient(IChatClient inner, ToolInvocationPipeline pipeline) : DelegatingChatClient(inner)
    {
        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ToolInvocationPipeline) && serviceKey is null ? pipeline : base.GetService(serviceType, serviceKey);
    }
}
