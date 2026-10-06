using System.Runtime.CompilerServices;
using AwesomeAssertions;
using IronHive.Agent.Loop;
using IronHive.Extensions.AI;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// <see cref="AgentOptions.StreamToolArguments"/>: the loop asks the IronHive bridge for argument fragments and turns each
/// into an <c>IsComplete = false</c> chunk before the complete one; off, nothing is asked and nothing changes.
/// </summary>
public class ToolArgumentStreamingTests
{
    private static ChatResponseUpdate[] FragmentedCall() =>
    [
        new() { Contents = [new FunctionCallDeltaContent("call-1", "", "propose_app")] },
        new() { Contents = [new FunctionCallDeltaContent("call-1", "{\"html\":\"<h1>")] },
        new() { Contents = [new FunctionCallDeltaContent("call-1", "Hi</h1>\"}")] },
        new() { Contents = [new FunctionCallContent("call-1", "propose_app", new Dictionary<string, object?> { ["html"] = "<h1>Hi</h1>" })] },
    ];

    private static async Task<List<ToolCallChunk>> ToolChunks(AgentLoop loop)
    {
        var chunks = new List<ToolCallChunk>();
        await foreach (var chunk in loop.RunStreamingAsync("make an app", cancellationToken: TestContext.Current.CancellationToken))
        {
            if (chunk.ToolCallDelta is { } tool)
            {
                chunks.Add(tool);
            }
        }

        return chunks;
    }

    [Fact]
    public async Task On_AsksForFragments_AndStreamsThemBeforeTheCompleteCall()
    {
        var client = new RecordingStreamClient(FragmentedCall());
        var loop = new AgentLoop(client, new AgentOptions { StreamToolArguments = true });

        var chunks = await ToolChunks(loop);

        client.Options!.AdditionalProperties![ChatClientAdapter.StreamToolArgumentsKey].Should().Be(true);
        chunks.Select(c => c.IsComplete).Should().Equal(false, false, false, true);
        chunks.Should().AllSatisfy(c => c.Id.Should().Be("call-1"));
        chunks[0].NameDelta.Should().Be("propose_app");
        chunks[1].NameDelta.Should().BeNull();
        string.Concat(chunks.Where(c => !c.IsComplete).Select(c => c.ArgumentsDelta)).Should().Be("{\"html\":\"<h1>Hi</h1>\"}");
        chunks[3].NameDelta.Should().Be("propose_app");
    }

    [Fact]
    public async Task Off_DoesNotAsk()
    {
        var client = new RecordingStreamClient(FragmentedCall()[3]);
        var loop = new AgentLoop(client);

        var chunks = await ToolChunks(loop);

        (client.Options?.AdditionalProperties?.ContainsKey(ChatClientAdapter.StreamToolArgumentsKey) ?? false).Should().BeFalse();
        chunks.Should().ContainSingle().Which.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task APerCallOverride_KeepsTheSwitch()
    {
        var client = new RecordingStreamClient(FragmentedCall());
        var loop = new AgentLoop(client, new AgentOptions { StreamToolArguments = true });

        await foreach (var _ in loop.RunStreamingAsync("q", new ChatOptions { Temperature = 0.1f }, TestContext.Current.CancellationToken))
        {
        }

        client.Options!.Temperature.Should().Be(0.1f);
        client.Options.AdditionalProperties![ChatClientAdapter.StreamToolArgumentsKey].Should().Be(true);
    }

    private sealed class RecordingStreamClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            foreach (var u in updates)
            {
                yield return u;
                await Task.Yield();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, string.Empty)]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
