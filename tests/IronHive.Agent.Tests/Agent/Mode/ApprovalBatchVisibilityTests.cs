using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// What a step of the tool invocation pipeline can see of the model's turn when it judges one call: the assistant message
/// that holds the call, with every call the model made in the same batch. Anything that ties a call to another call of its
/// batch (a proposal the model made for it) rests on this, so it is pinned on the real function-invoking client.
/// </summary>
public class ApprovalBatchVisibilityTests
{
    private sealed record Seen(string Tool, int Index, int Count, IReadOnlyList<string> BatchTools);

    private sealed class Capture : IToolInvocationMiddleware
    {
        public List<Seen> Calls { get; } = [];

        public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
        {
            var callId = context.CallContent?.CallId;
            var holder = context.Messages.LastOrDefault(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == callId));
            var batch = holder?.Contents.OfType<FunctionCallContent>().Select(c => c.Name).ToList() ?? [];
            Calls.Add(new Seen(context.Function.Name, context.FunctionCallIndex, context.FunctionCount, batch));
            return next(context, cancellationToken);
        }
    }

    private static async Task<Capture> RunBatchAsync(params (string Tool, string Arguments)[] batch)
    {
        var capture = new Capture();
        var mock = new MockChatClient().EnqueueToolCallsResponse(batch).EnqueueResponse("done");
        var client = mock.AsBuilder().UseToolInvocationPipeline(new ToolInvocationPipeline([capture])).Build();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create((string query) => $"found {query}", "Lookup"),
            AIFunctionFactory.Create((string path) => $"wrote {path}", "WriteFile"),
        };

        await new AgentLoop(client, new AgentOptions { Tools = tools }).RunAsync("go", TestContext.Current.CancellationToken);
        return capture;
    }

    [Theory]
    [InlineData("Lookup", "WriteFile")]
    [InlineData("WriteFile", "Lookup")]
    public async Task A_call_sees_every_call_of_its_batch_in_the_conversation_whichever_order_the_model_made_them(string first, string second)
    {
        string Args(string tool) => tool == "Lookup" ? """{"query":"q"}""" : """{"path":"a.txt"}""";

        var capture = await RunBatchAsync((first, Args(first)), (second, Args(second)));

        capture.Calls.Should().HaveCount(2);
        capture.Calls.Select(c => c.Tool).Should().Equal(first, second);
        capture.Calls.Should().OnlyContain(c => c.Count == 2);
        // Both calls read the same batch - the later one too, and the earlier one before its sibling has run.
        capture.Calls.Should().OnlyContain(c => c.BatchTools.SequenceEqual(new[] { first, second }));
    }
}
