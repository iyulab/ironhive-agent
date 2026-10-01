using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// The tracing contract (GenAI semantic conventions): a turn is an <c>invoke_agent</c> span, and the model calls and
/// tool runs inside it are its descendants when the chat client carries <c>UseOpenTelemetry()</c>.
/// </summary>
[Collection(nameof(AgentTelemetryTests))] // ActivityListener is process-wide
public sealed class AgentTelemetryTests : IDisposable
{
    private const string ChatSource = "IronHive.Agent.Tests.Chat";
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;
    private readonly string _testRoot;

    public AgentTelemetryTests()
    {
        // Each test runs under its own root activity so spans from other tests in the process are filtered out.
        _testRoot = Guid.NewGuid().ToString("N");
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is AgentTelemetry.SourceName or ChatSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.GetBaggageItem("test") == _testRoot)
                {
                    _spans.Enqueue(a);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Activity Root() => new Activity("test-root").AddBaggage("test", _testRoot).Start();

    private static AgentLoop Loop(MockChatClient mock, string? name = "librarian")
    {
        var client = mock.AsBuilder()
            .UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault())
            .UseOpenTelemetry(sourceName: ChatSource)
            .Build();
        var tools = new List<AITool> { AIFunctionFactory.Create((string query) => $"found {query}", "Lookup") };
        return new AgentLoop(client, new AgentOptions { Name = name, ModelId = "test-model", Tools = tools });
    }

    [Fact]
    public async Task A_turn_is_an_invoke_agent_span_with_the_model_and_tool_spans_beneath_it()
    {
        var mock = new MockChatClient().EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done");
        var loop = Loop(mock);

        using (Root())
        {
            await loop.RunAsync("look it up", Ct);
        }

        var agent = _spans.Should().ContainSingle(s => IsTurn(s)).Subject;
        agent.DisplayName.Should().Be("invoke_agent librarian");
        agent.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        agent.GetTagItem("gen_ai.agent.name").Should().Be("librarian");
        agent.GetTagItem("gen_ai.request.model").Should().Be("test-model");
        agent.GetTagItem("ironhive.agent.tool_calls").Should().Be(1);
        agent.Status.Should().NotBe(ActivityStatusCode.Error);

        // Microsoft.Extensions.AI starts execute_tool on the current activity's source, so it arrives on ours.
        var inner = _spans.Where(s => !IsTurn(s)).ToList();
        inner.Where(s => s.DisplayName.StartsWith("chat", StringComparison.Ordinal)).Should().HaveCount(2);
        inner.Should().ContainSingle(s => s.DisplayName == "execute_tool Lookup");
        inner.Should().OnlyContain(s => DescendsFrom(s, agent));
    }

    [Fact]
    public async Task A_streamed_turn_is_one_invoke_agent_span_too()
    {
        var mock = new MockChatClient().EnqueueResponse("streamed answer");
        var loop = Loop(mock, name: null);

        using (Root())
        {
            await foreach (var _ in loop.RunStreamingAsync("hi", Ct))
            {
            }
        }

        var agent = _spans.Should().ContainSingle(s => IsTurn(s)).Subject;
        agent.DisplayName.Should().Be("invoke_agent");
        agent.GetTagItem("gen_ai.agent.name").Should().BeNull();
        agent.GetTagItem("ironhive.agent.tool_calls").Should().Be(0);
    }

    [Fact]
    public async Task A_turn_that_throws_is_an_error_span_naming_the_exception()
    {
        var failing = new ThrowingChatClient(new InvalidOperationException("model unavailable"));
        var loop = new AgentLoop(failing, new AgentOptions { Name = "librarian" });

        using (Root())
        {
            var act = () => loop.RunAsync("hi", Ct);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        var agent = _spans.Should().ContainSingle(s => IsTurn(s)).Subject;
        agent.Status.Should().Be(ActivityStatusCode.Error);
        agent.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
    }

    private static bool IsTurn(Activity span) => span.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal);

    private static bool DescendsFrom(Activity span, Activity ancestor)
    {
        for (var parent = span.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.SpanId == ancestor.SpanId)
            {
                return true;
            }
        }

        return span.ParentSpanId == ancestor.SpanId;
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
