using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Loop;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// What a tool reports about its own work (<see cref="ToolCallStats"/>) reaches the call's
/// <see cref="ToolCallResult.Stats"/> on the streamed and non-streamed turn, and never the model.
/// </summary>
public class ToolCallStatsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static class Tools
    {
        public static string Search(string path)
        {
            ToolCallStats.Report("files_visited", 40);
            ToolCallStats.Report("bytes_read", 2_000_000L);
            ToolCallStats.Report("stopped", "budget");
            ToolCallStats.Report("truncated", true);
            ToolCallStats.Report("seconds", 1.5);
            return $"3 matches under {path}";
        }

        public static string Quiet(string path) => $"listed {path}";

        public static async Task<string> Walk(string path, CancellationToken cancellationToken)
        {
            ToolCallStats.Report("files_visited", 7);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "never";
        }
    }

    private static (AgentLoop Loop, MockChatClient Mock) Build(string tool, string finalAnswer, ToolInvocationOptions? options = null)
    {
        var mock = new MockChatClient()
            .EnqueueToolCallResponse(tool, """{"path":"C:/docs"}""")
            .EnqueueResponse(finalAnswer);
        var client = mock.AsBuilder().UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(options ?? new())).Build();
        var list = new List<AITool>
        {
            AIFunctionFactory.Create(Tools.Search, "Search"),
            AIFunctionFactory.Create(Tools.Quiet, "Quiet"),
            AIFunctionFactory.Create(Tools.Walk, "Walk"),
        };
        return (new AgentLoop(client, new AgentOptions { Tools = list }), mock);
    }

    private static void AssertReported(IReadOnlyDictionary<string, JsonElement>? stats)
    {
        stats.Should().NotBeNull();
        stats!["files_visited"].GetInt64().Should().Be(40);
        stats["bytes_read"].GetInt64().Should().Be(2_000_000);
        stats["stopped"].GetString().Should().Be("budget");
        stats["truncated"].GetBoolean().Should().BeTrue();
        stats["seconds"].GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task What_the_tool_reports_is_on_the_call_s_result_and_not_in_what_the_model_reads()
    {
        var (loop, mock) = Build("Search", "found three");

        var response = await loop.RunAsync("search", Ct);

        var call = response.ToolCalls.Should().ContainSingle().Subject;
        AssertReported(call.Stats);
        call.Result.Should().Be("3 matches under C:/docs");

        // The second round is what the model was sent after the call: the result is the tool's text alone, and nothing
        // rides on the content object a provider might serialize.
        var sent = mock.ReceivedMessages[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        sent.Result!.ToString().Should().Be("3 matches under C:/docs");
        sent.AdditionalProperties.Should().BeNull();
        JsonSerializer.Serialize(mock.ReceivedMessages[^1], AIJsonUtilities.DefaultOptions)
            .Should().NotContain("files_visited").And.NotContain("2000000");
    }

    [Fact]
    public async Task The_streamed_turn_carries_the_same_stats_on_the_result_chunk_and_the_turn_record()
    {
        var (loop, _) = Build("Search", "found three");

        ToolCallResult? arrived = null;
        TurnRecord? turn = null;
        await foreach (var chunk in loop.RunStreamingAsync("search", Ct))
        {
            arrived ??= chunk.ToolResult;
            turn ??= chunk.Turn;
        }

        AssertReported(arrived!.Stats);
        turn!.ToolCalls.Should().ContainSingle().Which.Should().Be(arrived, "both records are built from the same call");
    }

    [Fact]
    public async Task A_tool_that_reports_nothing_has_no_stats()
    {
        // Positive control for the assertions above.
        var (loop, _) = Build("Quiet", "listed");

        var response = await loop.RunAsync("list", Ct);

        response.ToolCalls.Should().ContainSingle().Which.Stats.Should().BeNull();
    }

    [Fact]
    public async Task A_call_that_timed_out_keeps_what_it_reported_before_it_was_stopped()
    {
        var (loop, _) = Build("Walk", "narrowing", new ToolInvocationOptions
        {
            MaxInvocationDuration = TimeSpan.FromMilliseconds(200),
            AbandonGrace = TimeSpan.FromMilliseconds(200),
        });

        var response = await loop.RunAsync("walk", Ct);

        var call = response.ToolCalls.Should().ContainSingle().Subject;
        call.RefusalKind.Should().Be(ToolCallRefusalKind.TimedOut);
        call.Stats!["files_visited"].GetInt64().Should().Be(7);
    }

    [Fact]
    public void Reporting_outside_a_pipeline_run_call_does_nothing()
    {
        var act = () => ToolCallStats.Report("files_visited", 1);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_channel_is_bounded_and_drops_what_exceeds_it()
    {
        var call = new FunctionCallContent("call-1", "Search");
        using (ToolCallStats.Begin(call))
        {
            for (var i = 0; i < ToolCallStats.MaxEntries + 4; i++)
            {
                ToolCallStats.Report($"k{i}", i);
            }

            ToolCallStats.Report("k0", 99);                                              // an existing key is replaced
            ToolCallStats.Report("k1", new string('x', ToolCallStats.MaxStringLength + 1)); // too long: dropped, k1 stays 1
            ToolCallStats.Report(new string('k', ToolCallStats.MaxKeyLength + 1), 1);      // key too long: dropped
            ToolCallStats.Report("k2", double.NaN);                                        // not finite: dropped
        }

        var stats = ToolCallStats.For(call)!;
        stats.Should().HaveCount(ToolCallStats.MaxEntries);
        stats["k0"].GetInt64().Should().Be(99);
        stats["k1"].GetInt64().Should().Be(1);
        stats["k2"].GetInt64().Should().Be(2);
        stats.Should().NotContainKey($"k{ToolCallStats.MaxEntries}");
    }
}
