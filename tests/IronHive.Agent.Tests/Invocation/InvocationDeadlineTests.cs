using AwesomeAssertions;
using IronHive.Agent.Extensions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// The time limit per tool call (<see cref="ToolInvocationOptions.MaxInvocationDuration"/>, a tool's own
/// <see cref="ToolInvocationHints.WithMaxDuration"/>): what the model reads when a call runs past it, and what is and is
/// not timed.
/// </summary>
public class InvocationDeadlineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private sealed class Tools
    {
        public int Runs;
        public bool SawCancellation;

        // Honours its token: stops the moment the deadline fires.
        public async Task<string> Walk(string path, CancellationToken cancellationToken)
        {
            Runs++;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            return "never";
        }

        // Ignores its token: only abandoning it ends the wait.
        public async Task<string> Stubborn(string path)
        {
            Runs++;
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return "late";
        }

        public async Task<string> Quick(string path, CancellationToken cancellationToken)
        {
            Runs++;
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            return $"listed {path}";
        }
    }

    private static (AgentLoop Loop, MockChatClient Mock, Tools Tools) Build(
        ToolInvocationPipeline pipeline,
        Action<MockChatClient> script,
        Func<AITool, AITool>? quick = null)
    {
        var tools = new Tools();
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder().UseToolInvocationPipeline(pipeline).Build();
        var quickTool = AIFunctionFactory.Create(tools.Quick, "Quick");
        var list = new List<AITool>
        {
            AIFunctionFactory.Create(tools.Walk, "Walk"),
            AIFunctionFactory.Create(tools.Stubborn, "Stubborn"),
            quick is null ? quickTool : quick(quickTool),
        };
        return (new AgentLoop(client, new AgentOptions { Tools = list }), mock, tools);
    }

    private static ToolInvocationOptions Limit(TimeSpan limit) => new()
    {
        MaxInvocationDuration = limit,
        AbandonGrace = TimeSpan.FromMilliseconds(200),
    };

    private static List<FunctionResultContent> ResultsTheModelRead(MockChatClient mock) =>
        mock.ReceivedMessages[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();

    [Fact]
    public async Task A_call_past_the_limit_is_cancelled_and_the_model_reads_which_tool_ran_out_of_time()
    {
        var (loop, mock, tools) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(Short)),
            m => m.EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""").EnqueueResponse("I'll narrow it"));

        var response = await loop.RunAsync("find it", Ct);

        tools.SawCancellation.Should().BeTrue("the tool's token is cancelled at the limit");
        var refusal = ResultsTheModelRead(mock).Single().Result.Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.TimedOut);
        refusal.Message.Should().Contain("'Walk'").And.Contain("0.2 s");
        response.Content.Should().Be("I'll narrow it");
        response.StopReason.Should().Be(TurnStopReason.Completed);
        response.ToolCalls[0].Success.Should().BeFalse();
        response.ToolCalls[0].RefusalKind.Should().Be(ToolCallRefusalKind.TimedOut);
    }

    [Fact]
    public async Task A_tool_that_ignores_its_token_is_abandoned_and_the_turn_goes_on()
    {
        var (loop, mock, _) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(Short)),
            m => m.EnqueueToolCallResponse("Stubborn", """{"path":"C:/big"}""").EnqueueResponse("moving on"));

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var response = await loop.RunAsync("find it", Ct);

        System.Diagnostics.Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(10), "the 30 s tool is not awaited");
        ResultsTheModelRead(mock).Single().Result.Should().BeOfType<ToolCallRefusal>()
            .Which.Kind.Should().Be(ToolCallRefusalKind.TimedOut);
        response.Content.Should().Be("moving on");
    }

    [Fact]
    public async Task The_same_slow_call_repeated_is_stopped_by_the_repeated_error_guard()
    {
        var limits = Limit(Short);
        limits.MaxRepeatedErrors = 2;
        var (loop, mock, tools) = Build(
            ToolInvocationPipeline.CreateDefault(limits),
            m => m.EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""")
                .EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""")
                .EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""")
                .EnqueueResponse("never requested"));

        var response = await loop.RunAsync("find it", Ct);

        tools.Runs.Should().Be(2);
        mock.ReceivedMessages.Should().HaveCount(2, "the second timeout in a row ends the request");
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        var last = loop.History[^1].Contents.OfType<FunctionResultContent>().Single();
        last.Result.Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
    }

    [Fact]
    public async Task A_tool_s_own_limit_wins_over_the_default_in_both_directions()
    {
        // Default 200 ms; Quick takes 400 ms but declares 5 s, so it finishes.
        var (loop, mock, _) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(Short)),
            m => m.EnqueueToolCallResponse("Quick", """{"path":"C:/docs"}""").EnqueueResponse("done"),
            quick: t => t.WithMaxDuration(TimeSpan.FromSeconds(5)));

        await loop.RunAsync("list", Ct);

        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Contain("listed C:/docs");

        // No default; Quick declares 100 ms, so it is stopped.
        var (loop2, mock2, _) = Build(
            ToolInvocationPipeline.CreateDefault(),
            m => m.EnqueueToolCallResponse("Quick", """{"path":"C:/docs"}""").EnqueueResponse("done"),
            quick: t => t.WithMaxDuration(TimeSpan.FromMilliseconds(100)));

        await loop2.RunAsync("list", Ct);

        ResultsTheModelRead(mock2).Single().Result.Should().BeOfType<ToolCallRefusal>()
            .Which.Kind.Should().Be(ToolCallRefusalKind.TimedOut);
    }

    [Fact]
    public async Task An_infinite_tool_limit_lifts_the_default()
    {
        var (loop, mock, _) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(Short)),
            m => m.EnqueueToolCallResponse("Quick", """{"path":"C:/docs"}""").EnqueueResponse("done"),
            quick: t => t.WithMaxDuration(Timeout.InfiniteTimeSpan));

        await loop.RunAsync("list", Ct);

        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Contain("listed C:/docs");
    }

    [Fact]
    public async Task Without_a_limit_a_slow_tool_finishes()
    {
        var (loop, mock, _) = Build(
            ToolInvocationPipeline.CreateDefault(),
            m => m.EnqueueToolCallResponse("Quick", """{"path":"C:/docs"}""").EnqueueResponse("done"));

        await loop.RunAsync("list", Ct);

        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Contain("listed C:/docs");
    }

    private sealed class SlowGate(TimeSpan wait) : IToolInvocationMiddleware
    {
        public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
        {
            await Task.Delay(wait, cancellationToken); // a person deciding
            return await next(context, cancellationToken);
        }
    }

    [Fact]
    public async Task Time_spent_in_the_steps_around_the_tool_such_as_an_approval_wait_is_not_counted()
    {
        // The gate (a person deciding) waits 500 ms and the tool takes ~400 ms; under a 600 ms limit the call only
        // finishes if the limit starts when the tool starts.
        var pipeline = new ToolInvocationPipeline(
            [new SlowGate(TimeSpan.FromMilliseconds(500))],
            resultMiddleware: null,
            Limit(TimeSpan.FromMilliseconds(600)));
        var (loop, mock, _) = Build(pipeline, m => m.EnqueueToolCallResponse("Quick", """{"path":"C:/docs"}""").EnqueueResponse("done"));

        await loop.RunAsync("list", Ct);

        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Contain("listed C:/docs",
            "500 ms in the gate + 400 ms in the tool exceeds 600 ms only if the gate is timed");
    }

    [Fact]
    public async Task Cancelling_the_turn_still_cancels_it_rather_than_reading_as_a_timeout()
    {
        var (loop, _, _) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(TimeSpan.FromSeconds(30))),
            m => m.EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""").EnqueueResponse("never"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => loop.RunAsync("find it", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_streamed_turn_reports_the_timeout_on_the_call_s_result_chunk()
    {
        var (loop, _, _) = Build(
            ToolInvocationPipeline.CreateDefault(Limit(Short)),
            m => m.EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""").EnqueueResponse("narrowing"));

        var results = new List<ToolCallResult>();
        await foreach (var chunk in loop.RunStreamingAsync("find it", Ct))
        {
            if (chunk.ToolResult is { } result)
            {
                results.Add(result);
            }
        }

        results.Should().ContainSingle().Which.RefusalKind.Should().Be(ToolCallRefusalKind.TimedOut);
    }

    [Fact]
    public async Task The_container_pipeline_takes_the_limit_from_AddIronHiveAgent()
    {
        using var provider = new ServiceCollection()
            .AddIronHiveAgent(o => o.ToolInvocation = new ToolInvocationOptions { MaxInvocationDuration = Short })
            .BuildServiceProvider();
        var (loop, mock, _) = Build(
            provider.GetRequiredService<ToolInvocationPipeline>(),
            m => m.EnqueueToolCallResponse("Walk", """{"path":"C:/big"}""").EnqueueResponse("narrowing"));

        await loop.RunAsync("find it", Ct);

        ResultsTheModelRead(mock).Single().Result.Should().BeOfType<ToolCallRefusal>()
            .Which.Kind.Should().Be(ToolCallRefusalKind.TimedOut);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("00:01:00", 60)]
    public void A_declared_limit_reads_from_seconds_or_a_timespan_string(string declared, int seconds)
    {
        var tool = AIFunctionFactory.Create(() => "x", "X");
        var withLimit = new AIFunctionFactoryOptions
        {
            Name = "X",
            AdditionalProperties = new Dictionary<string, object?> { [ToolInvocationHints.MaxDurationKey] = declared },
        };
        var declaredTool = AIFunctionFactory.Create(() => "x", withLimit);

        ToolInvocationHints.GetMaxDuration(declaredTool).Should().Be(TimeSpan.FromSeconds(seconds));
        ToolInvocationHints.GetMaxDuration(tool).Should().BeNull();
    }

    [Fact]
    public void A_non_positive_limit_is_rejected()
    {
        var options = new ToolInvocationOptions();
        var setZero = () => options.MaxInvocationDuration = TimeSpan.Zero;
        setZero.Should().Throw<ArgumentOutOfRangeException>();

        var tool = AIFunctionFactory.Create(() => "x", "X");
        var declareNegative = () => tool.WithMaxDuration(TimeSpan.FromSeconds(-1));
        declareNegative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
