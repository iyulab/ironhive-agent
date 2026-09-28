using System.Runtime.CompilerServices;
using System.Text.Json;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// A host runs some tools itself: it declares them to the loop without an implementation, the loop stops at the
/// call, the host runs it and hands the result back, and the loop continues from there. The chat client here is the
/// real <see cref="FunctionInvokingChatClient"/> over a scripted model, so the stop at the declaration is the
/// middleware's own behavior, not a test double's.
/// </summary>
public class HostToolRoundTripTests
{
    private static readonly JsonElement TabSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"tab":{"type":"string"}},"required":["tab"]}""").RootElement.Clone();

    private static AgentOptions Options() => new()
    {
        SystemPrompt = "Answer questions about open tabs.",
        Tools = [AIFunctionFactory.CreateDeclaration("read_page", "Reads one open tab's text.", TabSchema)],
    };

    [Fact]
    public async Task Declared_Tool_Stops_Then_Host_Result_Continues_To_The_Answer()
    {
        var model = new ScriptedModel();
        var client = new FunctionInvokingChatClient(model);

        // First half: the loop stops at the host tool.
        var first = new AgentLoop(client, Options());
        var stopped = await first.RunAsync("What is today's lunch on tab web-1?", TestContext.Current.CancellationToken);
        Assert.Equal("read_page", Assert.Single(stopped.ToolCalls).ToolName);

        // The host runs it and sends the conversation back to a fresh (stateless) loop.
        var history = first.History.ToList();
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(ScriptedModel.CallId, "Lunch: bibimbap")]));
        var next = new AgentLoop(client, Options());
        next.InitializeHistory(history);

        var answer = await next.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Today's lunch is bibimbap.", answer.Content);
        // The model saw the host's result, and no invented user message after it.
        var sent = model.Requests[^1];
        Assert.Equal(ChatRole.Tool, sent[^1].Role);
        Assert.Single(sent, m => m.Role == ChatRole.User);
        Assert.Equal(ChatRole.Assistant, next.History[^1].Role);
    }

    [Fact]
    public async Task Streaming_Continue_Yields_The_Answer_And_A_Turn_Record()
    {
        var model = new ScriptedModel();
        var loop = new AgentLoop(new FunctionInvokingChatClient(model), Options());
        await loop.RunAsync("What is today's lunch on tab web-1?", TestContext.Current.CancellationToken);
        var history = loop.History.ToList();
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(ScriptedModel.CallId, "Lunch: bibimbap")]));
        loop.InitializeHistory(history);

        var text = new List<string>();
        TurnRecord? turn = null;
        await foreach (var chunk in loop.ContinueStreamingAsync(TestContext.Current.CancellationToken))
        {
            if (chunk.TextDelta is { } delta)
            {
                text.Add(delta);
            }
            turn ??= chunk.Turn;
        }

        Assert.Equal("Today's lunch is bibimbap.", string.Concat(text));
        Assert.Equal("Today's lunch is bibimbap.", turn!.Content);
    }

    [Fact]
    public async Task Continue_Without_The_Pending_Result_Is_Refused_Before_Calling_The_Model()
    {
        var model = new ScriptedModel();
        var loop = new AgentLoop(new FunctionInvokingChatClient(model), Options());
        await loop.RunAsync("What is today's lunch on tab web-1?", TestContext.Current.CancellationToken);
        var callsBefore = model.Requests.Count;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.ContinueAsync(TestContext.Current.CancellationToken));

        Assert.Contains("assistant", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(callsBefore, model.Requests.Count);
    }

    [Fact]
    public async Task Continue_With_Only_Some_Results_Names_The_Missing_Call()
    {
        var loop = new AgentLoop(new FunctionInvokingChatClient(new ScriptedModel()), Options());
        loop.InitializeHistory([
            new ChatMessage(ChatRole.User, "Compare tabs 1 and 2."),
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("a", "read_page", new Dictionary<string, object?> { ["tab"] = "1" }),
                new FunctionCallContent("b", "read_page", new Dictionary<string, object?> { ["tab"] = "2" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "one")]),
        ]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loop.ContinueAsync(TestContext.Current.CancellationToken));

        Assert.Contains("b", refused.Message);
    }

    [Fact]
    public async Task Continue_After_A_Restored_User_Message_Runs_The_Turn()
    {
        var model = new ScriptedModel();
        var loop = new AgentLoop(new FunctionInvokingChatClient(model), Options());
        loop.InitializeHistory([new ChatMessage(ChatRole.User, "What is today's lunch on tab web-1?")]);

        var response = await loop.ContinueAsync(TestContext.Current.CancellationToken);

        Assert.Equal("read_page", Assert.Single(response.ToolCalls).ToolName);
    }

    [Fact]
    public async Task Continue_On_An_Empty_History_Is_Refused()
    {
        var loop = new AgentLoop(new ScriptedModel());

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.ContinueAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Calls read_page until the conversation holds its result, then answers from it.
    /// </summary>
    private sealed class ScriptedModel : IChatClient
    {
        public const string CallId = "call-1";
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Next(messages)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = Next(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, message.Contents);
        }

        private ChatMessage Next(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            Requests.Add(list);
            var result = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().FirstOrDefault(r => r.CallId == CallId);
            return result is null
                ? new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent(CallId, "read_page", new Dictionary<string, object?> { ["tab"] = "web-1" })])
                : new ChatMessage(ChatRole.Assistant, "Today's lunch is bibimbap.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
