using AwesomeAssertions;
using Ironbees.Core;
using IronHive.Agent.Extensions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Ironbees;
using IronHive.Agent.Loop;
using IronHive.Agent.Mcp;
using IronHive.Agent.Mode;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// The contract of the tool invocation pipeline, observed through a real <see cref="FunctionInvokingChatClient"/> driven
/// by an <see cref="AgentLoop"/> over a scripted model: what the model receives, and whether the tool ran.
/// </summary>
public class ToolInvocationPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Probe
    {
        public int Invocations;

        public string Lookup(string query)
        {
            Invocations++;
            return $"found {query}";
        }

        public string WriteFile(string path, string content)
        {
            Invocations++;
            return $"wrote {path}";
        }

        public string Broken(string query)
        {
            Invocations++;
            throw new InvalidOperationException("disk offline");
        }
    }

    private sealed class Recording(string name, List<string> log, Func<FunctionInvocationContext, object?>? shortCircuit = null) : IToolInvocationMiddleware
    {
        public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
        {
            log.Add($"{name}:before");
            if (shortCircuit?.Invoke(context) is { } answer)
            {
                log.Add($"{name}:short-circuit");
                return answer;
            }

            var result = await next(context, cancellationToken);
            log.Add($"{name}:after:{result}");
            return result;
        }
    }

    private sealed class Terminating : IToolInvocationMiddleware
    {
        public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken)
        {
            var result = await next(context, cancellationToken);
            context.Terminate = true;
            return result;
        }
    }

    private sealed class Suffixing(string suffix, List<string>? log = null) : IToolResultMiddleware
    {
        public ValueTask<object?> OnResultAsync(ToolResultContext context, CancellationToken cancellationToken)
        {
            log?.Add($"{suffix}:{context.ToolName}:{context.IsHostResult}");
            return new ValueTask<object?>($"{context.Result}{suffix}");
        }
    }

    private static (AgentLoop Loop, MockChatClient Mock, Probe Probe) Build(ToolInvocationPipeline pipeline, Action<MockChatClient> script)
    {
        var probe = new Probe();
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder().UseToolInvocationPipeline(pipeline).Build();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(probe.Lookup, "Lookup"),
            AIFunctionFactory.Create(probe.WriteFile, "WriteFile"),
            AIFunctionFactory.Create(probe.Broken, "Broken"),
        };
        return (new AgentLoop(client, new AgentOptions { Tools = tools }), mock, probe);
    }

    private static List<FunctionResultContent> ResultsTheModelRead(MockChatClient mock) =>
        mock.ReceivedMessages[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();

    [Fact]
    public async Task Middleware_runs_in_registration_order_first_outermost_and_results_flow_back_out()
    {
        var log = new List<string>();
        var pipeline = new ToolInvocationPipeline(
            [new Recording("outer", log), new Recording("inner", log)],
            [new Suffixing("!", log)]);
        var (loop, mock, probe) = Build(pipeline, m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done"));

        await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(1);
        log.Should().Equal("outer:before", "inner:before", "!:Lookup:False", "inner:after:found q!", "outer:after:found q!");
        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Be("found q!");
    }

    [Fact]
    public async Task A_middleware_that_does_not_call_next_short_circuits_the_rest_and_the_tool()
    {
        var log = new List<string>();
        var pipeline = new ToolInvocationPipeline(
            [new Recording("outer", log, _ => "cached answer"), new Recording("inner", log)],
            [new Suffixing("!", log)]);
        var (loop, mock, probe) = Build(pipeline, m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done"));

        var response = await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(0);
        log.Should().Equal("outer:before", "outer:short-circuit");
        ResultsTheModelRead(mock).Single().Result!.ToString().Should().Be("cached answer");
        response.ToolCalls[0].Result.Should().Be("cached answer");
    }

    [Fact]
    public async Task Terminate_ends_the_request_after_the_call_without_asking_the_model_again()
    {
        var (loop, mock, probe) = Build(
            new ToolInvocationPipeline([new Terminating()]),
            m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("never requested"));

        await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(1);
        mock.ReceivedMessages.Should().HaveCount(1);
        loop.History[^1].Role.Should().Be(ChatRole.Tool);
    }

    [Fact]
    public async Task A_call_whose_arguments_could_not_be_parsed_is_refused_and_the_tool_does_not_run()
    {
        var (loop, mock, probe) = Build(
            ToolInvocationPipeline.CreateDefault(),
            m => m.EnqueueMalformedToolCallResponse("WriteFile", new System.Text.Json.JsonException("unexpected end of data"))
                .EnqueueResponse("retrying"));

        var response = await loop.RunAsync("write", Ct);

        probe.Invocations.Should().Be(0);
        var refusal = ResultsTheModelRead(mock).Single().Result.Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.InvalidArguments);
        refusal.Message.Should().Contain("could not be parsed").And.Contain("unexpected end of data");
        response.ToolCalls[0].Success.Should().BeFalse();
    }

    // A model that keeps re-issuing a refused call is stuck. Measured: 33 refusals in a row until the step budget ran out,
    // and the turn read as "needed more steps". The second refusal in a row ends the request instead.
    [Fact]
    public async Task A_call_re_issued_after_being_refused_ends_the_request_as_tool_terminated_not_as_a_step_limit()
    {
        var (loop, mock, probe) = Build(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedCalls = 3 }),
            m =>
            {
                for (var i = 0; i < 12; i++)
                {
                    m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""");
                }

                m.EnqueueResponse("never requested");
            });

        var response = await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(3);
        mock.ReceivedMessages.Should().HaveCount(5, "three runs, one refusal the model may correct, then the second refusal ends it");
        response.StopReason.Should().Be(TurnStopReason.ToolTerminated);
        var last = loop.History[^1].Contents.OfType<FunctionResultContent>().Single();
        last.Result.Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
    }

    [Fact]
    public async Task With_MaxRefusedRepeats_0_the_guard_keeps_refusing_without_ending_the_request()
    {
        var (loop, mock, probe) = Build(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedCalls = 3, MaxRefusedRepeats = 0 }),
            m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueResponse("done"));

        var response = await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(3);
        mock.ReceivedMessages.Should().HaveCount(6);
        response.StopReason.Should().Be(TurnStopReason.Completed);
    }

    [Fact]
    public async Task The_same_successful_call_past_the_limit_is_not_run_again_within_one_request()
    {
        var (loop, mock, probe) = Build(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedCalls = 3 }),
            m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"other"}""")
                .EnqueueResponse("done"));

        await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(4); // three identical runs, the refused fourth, then a different call runs
        var fourth = mock.ReceivedMessages[4].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Last();
        fourth.Result.Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedCall);
        ResultsTheModelRead(mock).Last().Result!.ToString().Should().Be("found other");
    }

    [Fact]
    public async Task The_same_error_repeated_up_to_the_limit_ends_the_request_with_a_result_not_an_exception()
    {
        var (loop, mock, probe) = Build(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 3 }),
            m => m.EnqueueToolCallResponse("Broken", """{"query":"q"}""")
                .EnqueueToolCallResponse("Broken", """{"query":"q"}""")
                .EnqueueToolCallResponse("Broken", """{"query":"q"}""")
                .EnqueueResponse("never requested"));

        var response = await loop.RunAsync("go", Ct);

        probe.Invocations.Should().Be(3);
        mock.ReceivedMessages.Should().HaveCount(3);
        var last = loop.History[^1].Contents.OfType<FunctionResultContent>().Single();
        var refusal = last.Result.Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
        refusal.Message.Should().Contain("disk offline").And.Contain("3 times");
        response.ToolCalls.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_failing_tool_s_message_reaches_the_model_by_default()
    {
        // The library's tools report failure by throwing; the model has to read why to correct the call.
        var (loop, mock, _) = Build(
            ToolInvocationPipeline.CreateDefault(),
            m => m.EnqueueToolCallResponse("Broken", """{"query":"q"}""").EnqueueResponse("done"));

        await loop.RunAsync("go", Ct);

        ResultsTheModelRead(mock).Single().Result.Should().BeOfType<string>()
            .Which.Should().Contain("disk offline");
    }

    [Fact]
    public async Task A_host_that_turns_detailed_errors_off_sends_only_a_generic_failure()
    {
        var probe = new Probe();
        var mock = new MockChatClient();
        mock.EnqueueToolCallResponse("Broken", """{"query":"q"}""").EnqueueResponse("done");
        var client = mock.AsBuilder()
            .UseToolInvocationPipeline(ToolInvocationPipeline.CreateDefault(), c => c.IncludeDetailedErrors = false)
            .Build();
        var loop = new AgentLoop(client, new AgentOptions { Tools = [AIFunctionFactory.Create(probe.Broken, "Broken")] });

        await loop.RunAsync("go", Ct);

        probe.Invocations.Should().Be(1);
        ResultsTheModelRead(mock).Single().Result.Should().BeOfType<string>()
            .Which.Should().NotContain("disk offline");
    }

    /// <summary>
    /// A real MCP client tool over a session that answers every <c>tools/call</c> with <c>isError: true</c> — the tool
    /// returns the failure as its result and never throws.
    /// </summary>
    private static (ModelContextProtocol.Client.McpClientTool Tool, Func<int> Calls) FailingMcpTool(string name, Func<int, string> errorText)
    {
        var calls = 0;
        var client = NSubstitute.Substitute.For<ModelContextProtocol.Client.McpClient>();
        client.SendRequestAsync(NSubstitute.Arg.Any<ModelContextProtocol.Protocol.JsonRpcRequest>(), NSubstitute.Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ModelContextProtocol.Protocol.JsonRpcRequest>();
                var result = new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = true,
                    Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = errorText(++calls) }],
                };
                return Task.FromResult(new ModelContextProtocol.Protocol.JsonRpcResponse
                {
                    Id = request.Id,
                    Result = System.Text.Json.JsonSerializer.SerializeToNode(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                });
            });
        var tool = new ModelContextProtocol.Client.McpClientTool(client, new ModelContextProtocol.Protocol.Tool
        {
            Name = name,
            InputSchema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""{"type":"object","properties":{"path":{"type":"string"}}}"""),
        });
        return (tool, () => calls);
    }

    private static (AgentLoop Loop, MockChatClient Mock) BuildWith(ToolInvocationPipeline pipeline, AITool tool, Action<MockChatClient> script)
    {
        var mock = new MockChatClient();
        script(mock);
        var client = mock.AsBuilder().UseToolInvocationPipeline(pipeline).Build();
        return (new AgentLoop(client, new AgentOptions { Tools = [tool] }), mock);
    }

    [Fact]
    public async Task An_MCP_tool_that_keeps_reporting_the_same_isError_result_ends_the_request()
    {
        var (tool, calls) = FailingMcpTool("read_file", _ => "ENOENT: no such file");
        var (loop, mock) = BuildWith(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 3 }),
            tool,
            m => m.EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueResponse("never requested"));

        await loop.RunAsync("read it", Ct);

        calls().Should().Be(3);
        mock.ReceivedMessages.Should().HaveCount(3);
        // below the threshold the model read the MCP failure itself, not a refusal
        mock.ReceivedMessages[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single().Result
            .Should().BeOfType<System.Text.Json.JsonElement>();
        var refusal = loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
            .Should().BeOfType<ToolCallRefusal>().Subject;
        refusal.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
        refusal.Message.Should().Contain("ENOENT: no such file").And.Contain("3 times");
    }

    [Fact]
    public async Task A_built_in_file_tool_that_keeps_failing_the_same_way_ends_the_request()
    {
        // The built-in tools report a failure by throwing, so the library's own guard sees it with no host convention.
        var dir = Directory.CreateTempSubdirectory("builtin-failure-").FullName;
        try
        {
            var readFile = IronHive.Agent.Tools.BuiltInTools.GetAll(dir).Single(t => t.Name == "ReadFile");
            var (loop, mock) = BuildWith(
                ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 3 }),
                readFile,
                m => m.EnqueueToolCallResponse("ReadFile", """{"path":"missing.txt"}""")
                    .EnqueueToolCallResponse("ReadFile", """{"path":"missing.txt"}""")
                    .EnqueueToolCallResponse("ReadFile", """{"path":"missing.txt"}""")
                    .EnqueueResponse("never requested"));

            await loop.RunAsync("read it", Ct);

            mock.ReceivedMessages.Should().HaveCount(3);
            var refusal = loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
                .Should().BeOfType<ToolCallRefusal>().Subject;
            refusal.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
            refusal.Message.Should().Contain("File not found: missing.txt").And.Contain("3 times");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_MCP_tool_failing_with_different_errors_is_not_a_repeated_error()
    {
        var (tool, calls) = FailingMcpTool("read_file", n => $"error {n}");
        var (loop, mock) = BuildWith(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 3, MaxRepeatedCalls = 0 }),
            tool,
            m => m.EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueResponse("gave up"));

        var response = await loop.RunAsync("read it", Ct);

        calls().Should().Be(3);
        response.Content.Should().Be("gave up");
        ResultsTheModelRead(mock).Should().NotContain(r => r.Result is ToolCallRefusal);
    }

    [Fact]
    public async Task A_failing_MCP_call_repeated_with_the_same_arguments_is_a_repeated_error_not_a_repeated_success()
    {
        var (tool, calls) = FailingMcpTool("read_file", _ => "ENOENT");
        var (loop, _) = BuildWith(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedCalls = 2, MaxRepeatedErrors = 4 }),
            tool,
            m => m.EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueToolCallResponse("read_file", """{"path":"a.txt"}""")
                .EnqueueResponse("never requested"));

        await loop.RunAsync("read it", Ct);

        // the repeated-call guard (limit 2) would have refused the third call had it counted failures as runs
        calls().Should().Be(4);
        loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
            .Should().BeOfType<ToolCallRefusal>().Which.Kind.Should().Be(ToolCallRefusalKind.RepeatedError);
    }

    [Fact]
    public async Task A_host_tool_that_reports_failure_as_a_value_is_recognised_through_FailureOf()
    {
        var runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return $"Error: cannot open {path}"; }, "open_doc");
        var options = new ToolInvocationOptions
        {
            MaxRepeatedErrors = 2,
            FailureOf = r => r is string s && s.StartsWith("Error:", StringComparison.Ordinal) ? s : null,
        };
        var (loop, mock) = BuildWith(
            ToolInvocationPipeline.CreateDefault(options),
            tool,
            m => m.EnqueueToolCallResponse("open_doc", """{"path":"x"}""")
                .EnqueueToolCallResponse("open_doc", """{"path":"x"}""")
                .EnqueueResponse("never requested"));

        await loop.RunAsync("open", Ct);

        runs.Should().Be(2);
        mock.ReceivedMessages.Should().HaveCount(2);
        loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
            .Should().BeOfType<ToolCallRefusal>().Which.Message.Should().Contain("cannot open x");
    }

    [Fact]
    public async Task Without_FailureOf_a_value_that_merely_reads_like_an_error_is_a_success()
    {
        var runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return $"Error: cannot open {path}"; }, "open_doc");
        var (loop, _) = BuildWith(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 2, MaxRepeatedCalls = 0 }),
            tool,
            m => m.EnqueueToolCallResponse("open_doc", """{"path":"x"}""")
                .EnqueueToolCallResponse("open_doc", """{"path":"x"}""")
                .EnqueueResponse("done"));

        var response = await loop.RunAsync("open", Ct);

        runs.Should().Be(2);
        response.Content.Should().Be("done");
    }

    [Fact]
    public async Task An_McpToolResult_error_counts_as_a_failure()
    {
        var runs = 0;
        var tool = AIFunctionFactory.Create((string path) => { runs++; return McpToolResult.Error("Plugin 'fs' is not connected."); }, "fs_read");
        var (loop, _) = BuildWith(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedErrors = 2 }),
            tool,
            m => m.EnqueueToolCallResponse("fs_read", """{"path":"x"}""")
                .EnqueueToolCallResponse("fs_read", """{"path":"x"}""")
                .EnqueueResponse("never requested"));

        await loop.RunAsync("read", Ct);

        runs.Should().Be(2);
        loop.History[^1].Contents.OfType<FunctionResultContent>().Single().Result
            .Should().BeOfType<ToolCallRefusal>().Which.Message.Should().Contain("not connected");
    }

    [Fact]
    public async Task Guards_set_to_zero_are_off()
    {
        var (loop, _, probe) = Build(
            ToolInvocationPipeline.CreateDefault(new ToolInvocationOptions { MaxRepeatedCalls = 0 }),
            m => m.EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
                .EnqueueResponse("done"));

        await loop.RunAsync("look", Ct);

        probe.Invocations.Should().Be(4);
    }

    [Fact]
    public async Task Without_a_registered_gate_the_container_pipeline_has_no_permission_gate()
    {
        // *.json edits are Ask by default: with a gate and no approver they are refused (next fact); without one they run.
        var services = new ServiceCollection().AddIronHiveAgent();
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ToolInvocationPipeline>().InvocationMiddleware.Should().NotContain(m => m is ApprovalGateMiddleware);

        var probe = new Probe();
        var mock = new MockChatClient().EnqueueToolCallResponse("WriteFile", """{"path":"app.json","content":"{}"}""").EnqueueResponse("done");
        var loop = new AgentLoop(
            mock.AsBuilder().UseToolInvocationPipeline().Build(sp),
            new AgentOptions { Tools = [AIFunctionFactory.Create(probe.WriteFile, "WriteFile")] });

        await loop.RunAsync("write", Ct);

        probe.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task A_registered_gate_with_no_approval_service_refuses_an_Ask_verdict()
    {
        var services = new ServiceCollection().AddIronHiveAgent().AddIronHiveAgentApprovalGate();
        using var sp = services.BuildServiceProvider();

        var probe = new Probe();
        var mock = new MockChatClient().EnqueueToolCallResponse("WriteFile", """{"path":"app.json","content":"{}"}""").EnqueueResponse("done");
        var loop = new AgentLoop(
            mock.AsBuilder().UseToolInvocationPipeline().Build(sp),
            new AgentOptions { Tools = [AIFunctionFactory.Create(probe.WriteFile, "WriteFile")] });

        var response = await loop.RunAsync("write", Ct);

        probe.Invocations.Should().Be(0);
        response.ToolCalls[0].Success.Should().BeFalse();
        response.ToolCalls[0].Result.Should().Contain("no approval service is configured");
    }

    [Fact]
    public void The_container_pipeline_holds_the_default_guards_first_then_what_the_consumer_registered()
    {
        var services = new ServiceCollection()
            .AddIronHiveAgent(o => o.ToolInvocation = new ToolInvocationOptions { MaxRepeatedCalls = 7 })
            .AddIronHiveAgentApprovalGate()
            .AddIronHiveAgentApprovalGate(); // idempotent
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ToolInvocationPipeline>().InvocationMiddleware.Select(m => m.GetType()).Should().Equal(
            typeof(ArgumentParseFailureMiddleware), typeof(RepeatedCallGuardMiddleware), typeof(RepeatedErrorGuardMiddleware),
            typeof(ApprovalGateMiddleware));
        sp.GetRequiredService<ToolInvocationOptions>().MaxRepeatedCalls.Should().Be(7);
    }

    [Fact]
    public void A_registered_tool_result_guard_is_applied_by_the_container_pipeline()
    {
        var guard = NSubstitute.Substitute.For<IToolResultGuard>();
        var services = new ServiceCollection().AddIronHiveAgent();
        services.AddSingleton(guard);
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ToolInvocationPipeline>().ResultMiddleware
            .Should().ContainSingle().Which.Should().BeOfType<ToolResultGuardMiddleware>().Which.Guard.Should().BeSameAs(guard);
    }

    [Fact]
    public void Building_without_a_pipeline_in_the_services_throws_instead_of_running_tools_unguarded()
    {
        var builder = new MockChatClient().AsBuilder().UseToolInvocationPipeline();

        var act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddIronHiveAgent*");
    }

    [Fact]
    public void Setting_the_function_invoker_in_configure_throws()
    {
        var builder = new MockChatClient().AsBuilder().UseToolInvocationPipeline(
            ToolInvocationPipeline.CreateDefault(),
            c => c.FunctionInvoker = (_, _) => new ValueTask<object?>("bypass"));

        var act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>().WithMessage("*IToolInvocationMiddleware*");
    }

    [Fact]
    public async Task Ironbees_adapter_runs_its_tool_calls_through_the_pipeline()
    {
        // The adapter's own loop used to gate and guard by hand; a middleware it does not know about never ran there.
        var log = new List<string>();
        var mock = new MockChatClient()
            .EnqueueToolCallResponse("Lookup", """{"query":"q"}""")
            .EnqueueResponse("never requested");
        var probe = new Probe();
        var adapter = new ChatClientFrameworkAdapter(
            _ => mock,
            toolsFactory: () => [AIFunctionFactory.Create(probe.Lookup, "Lookup")],
            toolInvocationPipeline: new ToolInvocationPipeline([new Recording("seen", log), new Terminating()], [new Suffixing("!")]));
        var agent = await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "a", Description = "d", Version = "1.0.0", SystemPrompt = "s", Model = new ModelConfig { Deployment = "m" }
        }, Ct);

        var result = await adapter.RunStructuredAsync(agent, "look", cancellationToken: Ct);

        log.Should().Equal("seen:before", "seen:after:found q!");
        probe.Invocations.Should().Be(1);
        mock.ReceivedMessages.Should().HaveCount(1); // Terminate ended the run after the tool turn
        result.TurnLimitReached.Should().BeFalse();
    }

    [Fact]
    public async Task Ironbees_adapter_stream_ends_as_tool_terminated_and_later_calls_of_that_turn_do_not_run()
    {
        var probe = new Probe();
        var twoCalls = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("c1", "Lookup", new Dictionary<string, object?> { ["query"] = "a" }),
            new FunctionCallContent("c2", "Lookup", new Dictionary<string, object?> { ["query"] = "b" }),
        ]);
        var client = new ScriptedClient(new ChatResponse(twoCalls));
        var adapter = new ChatClientFrameworkAdapter(
            _ => client,
            toolsFactory: () => [AIFunctionFactory.Create(probe.Lookup, "Lookup")],
            toolInvocationPipeline: new ToolInvocationPipeline([new Terminating()]));
        var agent = await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "a", Description = "d", Version = "1.0.0", SystemPrompt = "s", Model = new ModelConfig { Deployment = "m" }
        }, Ct);

        var chunks = new List<global::Ironbees.Core.Streaming.StreamChunk>();
        await foreach (var chunk in adapter.StreamStructuredAsync(agent, "look", cancellationToken: Ct))
        {
            chunks.Add(chunk);
        }

        probe.Invocations.Should().Be(1);
        chunks.OfType<global::Ironbees.Core.Streaming.CompletionChunk>().Single().FinishReason
            .Should().Be(ChatClientFrameworkAdapter.ToolTerminatedFinishReason);
    }

    [Fact]
    public async Task Ironbees_adapter_over_a_single_client_runs_tools_given_per_run()
    {
        // The single-client constructor used to leave the tool-turn limit at 0: a run with tools stopped before its first model call.
        var probe = new Probe();
        var mock = new MockChatClient().EnqueueToolCallResponse("Lookup", """{"query":"q"}""").EnqueueResponse("done");
        var adapter = new ChatClientFrameworkAdapter(mock);
        var agent = await adapter.CreateAgentAsync(new AgentConfig
        {
            Name = "a", Description = "d", Version = "1.0.0", SystemPrompt = "s", Model = new ModelConfig { Deployment = "m" }
        }, Ct);

        var result = await adapter.RunStructuredAsync(
            agent, "look", options: new AgentRunOptions { Tools = [AIFunctionFactory.Create(probe.Lookup, "Lookup")] }, cancellationToken: Ct);

        result.Text.Should().Be("done");
        result.TurnLimitReached.Should().BeFalse();
        probe.Invocations.Should().Be(1);
    }

    private sealed class ScriptedClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
