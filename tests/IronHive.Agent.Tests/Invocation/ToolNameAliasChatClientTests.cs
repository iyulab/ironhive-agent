using System.Text.RegularExpressions;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// A tool whose name a provider would refuse (<c>message.send</c>) is offered under a safe alias and runs under its real
/// name — observed through a real <see cref="FunctionInvokingChatClient"/> over a model that records what it was sent.
/// </summary>
public partial class ToolNameAliasChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ProviderSafe();

    private sealed class Probe
    {
        public List<string> Calls { get; } = [];

        public string Send(string to)
        {
            Calls.Add($"send {to}");
            return $"sent to {to}";
        }
    }

    private static (IChatClient Client, MockChatClient Model, Probe Probe, ChatOptions Options) Setup(string toolName = "message.send")
    {
        var probe = new Probe();
        var model = new MockChatClient();
        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation()
            .UseToolNameAlias()
            .UseTextToolCallRecovery()
            .Build();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(probe.Send, toolName)] };
        return (client, model, probe, options);
    }

    private static string OfferedName(ChatOptions? options) => ((AIFunctionDeclaration)options!.Tools!.Single()).Name;

    [Fact]
    public async Task ADottedTool_IsOfferedUnderAnAlias_AndRunsUnderItsRealName()
    {
        var (client, model, probe, options) = Setup();
        var alias = ToolNameAliasChatClient.AliasFor("message.send");
        model.EnqueueToolCallResponse(alias, "{\"to\": \"ann\"}").EnqueueResponse("Done.");

        var response = await client.GetResponseAsync("tell ann", options, Ct);

        OfferedName(model.ReceivedOptions[0]).Should().Be(alias).And.MatchRegex(ProviderSafe().ToString());
        probe.Calls.Should().Equal("send ann");
        response.Text.Should().Contain("Done.");
    }

    [Fact]
    public async Task TheCallersOptions_AreNotChanged()
    {
        var (client, model, _, options) = Setup();
        model.EnqueueResponse("ok");

        await client.GetResponseAsync("hi", options, Ct);

        OfferedName(options).Should().Be("message.send");
    }

    [Fact]
    public async Task TheHistoryTheModelSees_CarriesTheAliasOnTheEarlierCall()
    {
        var (client, model, _, options) = Setup();
        var alias = ToolNameAliasChatClient.AliasFor("message.send");
        model.EnqueueToolCallResponse(alias, "{\"to\": \"ann\"}").EnqueueResponse("Done.");

        await client.GetResponseAsync("tell ann", options, Ct);

        var secondRequest = model.ReceivedMessages[1];
        secondRequest.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single().Name.Should().Be(alias);
        secondRequest.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString().Should().Be("sent to ann");
        OfferedName(model.ReceivedOptions[1]).Should().Be(alias);
    }

    [Fact]
    public async Task AHistoryCallOfAToolNoLongerOffered_IsStillAliased()
    {
        var (client, model, _, _) = Setup();
        model.EnqueueResponse("ok");
        var earlier = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "fs/read", new Dictionary<string, object?>())]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "text")]);

        await client.GetResponseAsync([new(ChatRole.User, "hi"), earlier, result, new(ChatRole.User, "go on")], new ChatOptions(), Ct);

        model.ReceivedMessages[0].SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single().Name
            .Should().Be(ToolNameAliasChatClient.AliasFor("fs/read")).And.MatchRegex(ProviderSafe().ToString());
        earlier.Contents.OfType<FunctionCallContent>().Single().Name.Should().Be("fs/read");
    }

    [Fact]
    public async Task ARequiredTool_IsRequiredUnderItsAlias()
    {
        var (client, model, _, options) = Setup();
        options.ToolMode = ChatToolMode.RequireSpecific("message.send");
        model.EnqueueResponse("ok");

        await client.GetResponseAsync("hi", options, Ct);

        ((RequiredChatToolMode)model.ReceivedOptions[0]!.ToolMode!).RequiredFunctionName
            .Should().Be(ToolNameAliasChatClient.AliasFor("message.send"));
    }

    [Fact]
    public async Task ASafeRequest_IsForwardedAsItIs()
    {
        var (client, model, probe, options) = Setup("send_message");
        model.EnqueueToolCallResponse("send_message", "{\"to\": \"ann\"}").EnqueueResponse("Done.");

        await client.GetResponseAsync("tell ann", options, Ct);

        probe.Calls.Should().Equal("send ann");
        OfferedName(model.ReceivedOptions[0]).Should().Be("send_message");
        // The function-invoking client above clones the options, so identity cannot be asserted; the tool object can.
        model.ReceivedOptions[0]!.Tools!.Single().Should().BeSameAs(options.Tools!.Single());
    }

    [Fact]
    public async Task ADottedTool_WorksWhenStreaming()
    {
        var (client, model, probe, options) = Setup();
        var alias = ToolNameAliasChatClient.AliasFor("message.send");
        model.EnqueueToolCallResponse(alias, "{\"to\": \"ann\"}").EnqueueResponse("Done.");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync("tell ann", options, Ct))
        {
            updates.Add(update);
        }

        probe.Calls.Should().Equal("send ann");
        updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single().Name.Should().Be("message.send");
        OfferedName(model.ReceivedOptions[0]).Should().Be(alias);
        OfferedName(model.ReceivedOptions[1]).Should().Be(alias, "the second streamed request offers the alias too");
        model.ReceivedMessages[1].SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single().Name
            .Should().Be(alias, "and its history carries the earlier call under the alias");
    }

    [Fact]
    public async Task ACallWrittenAsText_UnderTheAlias_IsRecoveredAndRunsUnderTheRealName()
    {
        var (client, model, probe, options) = Setup();
        var alias = ToolNameAliasChatClient.AliasFor("message.send");
        model.EnqueueResponse($"<tool_call>\n{{\"name\": \"{alias}\", \"arguments\": {{\"to\": \"ann\"}}}}\n</tool_call>").EnqueueResponse("Done.");

        await client.GetResponseAsync("tell ann", options, Ct);

        probe.Calls.Should().Equal("send ann");
    }

    [Fact]
    public async Task ACallByAnUnknownName_PassesThroughUntouched()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueToolCallResponse("invented_tool", "{}").EnqueueResponse("Done.");

        await client.GetResponseAsync("hi", options, Ct);

        probe.Calls.Should().BeEmpty();
        model.ReceivedMessages[1].SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single().Name.Should().Be("invented_tool");
    }

    [Fact]
    public async Task TwoRealNamesWithTheSameAlias_FailTheRequestWithBothNames()
    {
        var model = new MockChatClient();
        var client = new ToolNameAliasChatClient(model);
        var probe = new Probe();
        var taken = ToolNameAliasChatClient.AliasFor("a.b");
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(probe.Send, "a.b"), AIFunctionFactory.Create(probe.Send, taken)],
        };

        // «taken» is the alias of a.b and also another tool's real (safe) name: the model could not tell them apart.
        var act = () => client.GetResponseAsync("hi", options, Ct);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("a.b").And.Contain(taken);
        model.ReceivedOptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("message.send")]
    [InlineData("fs/read")]
    [InlineData("héllo wörld")]
    [InlineData(".")]
    [InlineData("a:b:c")]
    public void AnAlias_MatchesTheProviderPattern_AndIsStable(string name)
    {
        var alias = ToolNameAliasChatClient.AliasFor(name);

        alias.Should().MatchRegex(ProviderSafe().ToString());
        ToolNameAliasChatClient.AliasFor(name).Should().Be(alias);
    }

    [Fact]
    public void AnAlias_KeepsADistinctNameDistinct_EvenWhenTheReadablePartIsTheSame()
    {
        ToolNameAliasChatClient.AliasFor("a.b").Should().NotBe(ToolNameAliasChatClient.AliasFor("a/b"));
        ToolNameAliasChatClient.AliasFor("a.b").Should().NotBe("a_b");
    }

    [Fact]
    public void ALongName_GetsAnAliasWithinTheLengthLimit()
    {
        var alias = ToolNameAliasChatClient.AliasFor("x." + new string('y', 200));

        alias.Length.Should().BeLessThanOrEqualTo(64);
        alias.Should().MatchRegex(ProviderSafe().ToString());
    }

    [Theory]
    [InlineData("search_files")]
    [InlineData("Read-File")]
    [InlineData("a")]
    public void ASafeName_IsItsOwnAlias(string name) =>
        ToolNameAliasChatClient.AliasFor(name).Should().Be(name);

    [Fact]
    public void AnEmptyOrTooLongSafeName_IsAliased()
    {
        ToolNameAliasChatClient.AliasFor("").Should().MatchRegex(ProviderSafe().ToString()).And.NotBe("");
        ToolNameAliasChatClient.AliasFor(new string('z', 65)).Length.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public async Task ASafeRequest_IsForwardedAsTheSameObjects_ByTheClientAlone()
    {
        var model = new MockChatClient().EnqueueResponse("ok");
        var client = new ToolNameAliasChatClient(model);
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(new Probe().Send, "send_message")] };
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };

        await client.GetResponseAsync(messages, options, Ct);

        model.ReceivedOptions[0].Should().BeSameAs(options);
        model.ReceivedMessages[0].Single().Should().BeSameAs(messages[0]);
    }

    [Fact]
    public async Task ADottedRequest_LeavesTheCallersMessagesAndOptionsAsTheyWere()
    {
        var model = new MockChatClient().EnqueueResponse("ok");
        var client = new ToolNameAliasChatClient(model);
        var tool = AIFunctionFactory.Create(new Probe().Send, "message.send");
        var options = new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.RequireSpecific("message.send"), ModelId = "m", Temperature = 0.2f };
        var call = new FunctionCallContent("c1", "message.send", new Dictionary<string, object?> { ["to"] = "ann" });
        var assistant = new ChatMessage(ChatRole.Assistant, [call]);
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi"), assistant };

        await client.GetResponseAsync(messages, options, Ct);

        options.Tools!.Single().Should().BeSameAs(tool);
        ((RequiredChatToolMode)options.ToolMode!).RequiredFunctionName.Should().Be("message.send");
        messages[1].Should().BeSameAs(assistant);
        assistant.Contents.Single().Should().BeSameAs(call);
        call.Name.Should().Be("message.send");
        model.ReceivedMessages[0][0].Should().BeSameAs(messages[0], "a message with no aliased call is passed as it is");
        model.ReceivedMessages[0][1].Should().NotBeSameAs(assistant);
        model.ReceivedOptions[0]!.ModelId.Should().Be("m");
        model.ReceivedOptions[0]!.Temperature.Should().Be(0.2f, "the other options survive the copy");
    }

    [Fact]
    public async Task ARewrittenMessage_KeepsEverythingButTheName_AndIsTheSameInstanceOnEveryRound()
    {
        var model = new MockChatClient().EnqueueResponse("ok").EnqueueResponse("ok");
        var client = new ToolNameAliasChatClient(model);
        var args = new Dictionary<string, object?> { ["to"] = "ann" };
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("sending"),
            new FunctionCallContent("c-safe", "send_message", args),
            new FunctionCallContent("c-dotted", "message.send", args) { InformationalOnly = true },
        ])
        {
            AuthorName = "bot",
            MessageId = "m1",
        };
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi"), assistant };

        await client.GetResponseAsync(messages, new ChatOptions(), Ct);
        await client.GetResponseAsync(messages, new ChatOptions(), Ct);

        var rewritten = model.ReceivedMessages[0][1];
        rewritten.AuthorName.Should().Be("bot");
        rewritten.MessageId.Should().Be("m1");
        rewritten.Contents[0].Should().BeOfType<TextContent>().Which.Text.Should().Be("sending");
        var calls = rewritten.Contents.OfType<FunctionCallContent>().ToList();
        calls[0].Name.Should().Be("send_message");
        calls[1].Name.Should().Be(ToolNameAliasChatClient.AliasFor("message.send"));
        calls[1].CallId.Should().Be("c-dotted");
        calls[1].Arguments.Should().BeSameAs(args);
        calls[1].InformationalOnly.Should().BeTrue();
        model.ReceivedMessages[1][1].Should().BeSameAs(rewritten, "a client below that recognises messages by reference must see the same instance each round");
    }

    [Fact]
    public async Task SeveralDottedTools_AreEachAliased_AndParallelCallsRunUnderTheirRealNames()
    {
        var probe = new Probe();
        var model = new MockChatClient();
        var client = new ChatClientBuilder(model).UseFunctionInvocation().UseToolNameAlias().Build();
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(probe.Send, "message.send"),
                AIFunctionFactory.Create(probe.Send, "send_message"),
                AIFunctionFactory.Create(probe.Send, "fs/read"),
            ],
        };
        model.EnqueueToolCallsResponse(
            (ToolNameAliasChatClient.AliasFor("message.send"), "{\"to\": \"a\"}"),
            ("send_message", "{\"to\": \"b\"}"),
            (ToolNameAliasChatClient.AliasFor("fs/read"), "{\"to\": \"c\"}"))
            .EnqueueResponse("Done.");

        await client.GetResponseAsync("go", options, Ct);

        probe.Calls.Should().BeEquivalentTo(["send a", "send b", "send c"]);
        model.ReceivedOptions[0]!.Tools!.OfType<AIFunctionDeclaration>().Select(t => t.Name).Should().BeEquivalentTo(
            [ToolNameAliasChatClient.AliasFor("message.send"), "send_message", ToolNameAliasChatClient.AliasFor("fs/read")]);
    }

    [Fact]
    public async Task AnAliasedTool_KeepsItsDescriptionAndSchema()
    {
        var model = new MockChatClient().EnqueueResponse("ok");
        var client = new ToolNameAliasChatClient(model);
        var tool = AIFunctionFactory.Create(new Probe().Send, "message.send", "Sends a message to someone.");

        await client.GetResponseAsync("hi", new ChatOptions { Tools = [tool] }, Ct);

        var offered = (AIFunctionDeclaration)model.ReceivedOptions[0]!.Tools!.Single();
        offered.Description.Should().Be("Sends a message to someone.");
        offered.JsonSchema.GetRawText().Should().Be(tool.JsonSchema.GetRawText());
        offered.GetService<AIFunctionDeclaration>().Should().BeSameAs(offered);
    }

    /// <summary>A client that streams the updates it was given, whatever it is asked.</summary>
    private sealed class ScriptedStream(params ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task AStreamedCall_FirstArgumentFragmentAndCall_ComeBackUnderTheRealName()
    {
        var alias = ToolNameAliasChatClient.AliasFor("message.send");
        var client = new ToolNameAliasChatClient(new ScriptedStream(
            new ChatResponseUpdate(ChatRole.Assistant, [new IronHive.Extensions.AI.FunctionCallDeltaContent("c1", "{\"to\"", alias)]),
            new ChatResponseUpdate(ChatRole.Assistant, [new IronHive.Extensions.AI.FunctionCallDeltaContent("c1", ": \"ann\"}")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("c1", alias, new Dictionary<string, object?> { ["to"] = "ann" })])));
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(new Probe().Send, "message.send")] };

        var contents = new List<AIContent>();
        await foreach (var update in client.GetStreamingResponseAsync("go", options, Ct))
        {
            contents.AddRange(update.Contents);
        }

        var deltas = contents.OfType<IronHive.Extensions.AI.FunctionCallDeltaContent>().ToList();
        deltas[0].Name.Should().Be("message.send");
        deltas[0].ArgumentsFragment.Should().Be("{\"to\"");
        deltas[1].Name.Should().BeNull("only the first fragment carries a name");
        contents.OfType<FunctionCallContent>().Single().Name.Should().Be("message.send");
    }

    [Fact]
    public void TheAlias_IsAFixedFunctionOfTheName()
    {
        // A change to the hash, the length or the format would change what a provider's server-side conversation state holds.
        ToolNameAliasChatClient.AliasFor("message.send").Should().Be("message_send_c0cd13f497");
        ToolNameAliasChatClient.AliasFor("fs/read").Should().Be("fs_read_96379919f6");
    }

    [Fact]
    public void ASafeNameOfExactlyTheLimit_IsItsOwnAlias_AndOneMoreIsNot()
    {
        var limit = new string('a', 64);

        ToolNameAliasChatClient.AliasFor(limit).Should().Be(limit);
        ToolNameAliasChatClient.AliasFor(limit + "a").Should().NotBe(limit + "a");
    }
}
