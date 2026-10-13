using System.Text.Json;
using AwesomeAssertions;
using IronHive.Agent.Invocation;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Invocation;

/// <summary>
/// A tool call the model wrote as text is run as the call it meant — observed through a real
/// <see cref="FunctionInvokingChatClient"/>, so «recovered» means the tool ran, not only that a content was rewritten.
/// </summary>
public class TextToolCallRecoveryChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Probe
    {
        public List<string> Calls { get; } = [];

        public string WriteFile(string path, string content)
        {
            Calls.Add($"write {path}: {content}");
            return $"wrote {path}";
        }

        public string Repeat(string text, int times)
        {
            Calls.Add($"repeat {text} x{times}");
            return string.Concat(Enumerable.Repeat(text, times));
        }
    }

    private const string HermesBlock =
        "I will save it now.\n<tool_call>\n{\"name\": \"WriteFile\", \"arguments\": {\"path\": \"answer.txt\", \"content\": \"42\"}}\n</tool_call>";

    private const string QwenCoderBlock =
        "<tool_call>\n<function=WriteFile>\n<parameter=path>\nanswer.txt\n</parameter>\n<parameter=content>\nline one\nline two\n</parameter>\n</function>\n</tool_call>";

    private static (IChatClient Client, MockChatClient Model, Probe Probe, ChatOptions Options) Setup()
    {
        var probe = new Probe();
        var model = new MockChatClient();
        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation()
            .UseTextToolCallRecovery()
            .Build();
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(probe.WriteFile, nameof(Probe.WriteFile)),
                AIFunctionFactory.Create(probe.Repeat, nameof(Probe.Repeat)),
            ],
        };
        return (client, model, probe, options);
    }

    [Fact]
    public async Task AHermesBlock_RunsTheTool_AndTheModelSeesItsResult()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse(HermesBlock).EnqueueResponse("Saved.");

        var response = await client.GetResponseAsync("save 42", options, Ct);

        probe.Calls.Should().Equal("write answer.txt: 42");
        response.Text.Should().Contain("Saved.");
        model.ReceivedMessages[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Single().Result!.ToString().Should().Be("wrote answer.txt");
        var assistant = model.ReceivedMessages[1].First(m => m.Role == ChatRole.Assistant);
        assistant.Text.Should().Be("I will save it now.", "the prose before the block stays, the markup does not");
    }

    [Fact]
    public async Task AQwenCoderBlock_RunsTheTool_WithMultiLineParameters()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse(QwenCoderBlock).EnqueueResponse("Done.");

        await client.GetResponseAsync("write it", options, Ct);

        probe.Calls.Should().Equal("write answer.txt: line one\nline two");
    }

    [Fact]
    public async Task AParameterTheSchemaTypesAsNumber_IsPassedAsANumber()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse("<function=Repeat>\n<parameter=text>\nab\n</parameter>\n<parameter=times>\n3\n</parameter>\n</function>")
            .EnqueueResponse("ok");

        await client.GetResponseAsync("repeat", options, Ct);

        probe.Calls.Should().Equal("repeat ab x3");
    }

    [Fact]
    public async Task AWrapperOpenedButNotClosed_IsStillACall_AndNotLeftInTheProse()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse("Writing.\n<tool_call>\n<function=WriteFile>\n<parameter=path>\na\n</parameter>\n<parameter=content>\nb\n</parameter>\n</function>")
            .EnqueueResponse("ok");

        await client.GetResponseAsync("go", options, Ct);

        probe.Calls.Should().Equal("write a: b");
        model.ReceivedMessages[1].First(m => m.Role == ChatRole.Assistant).Text.Should().Be("Writing.");
    }

    [Theory]
    [InlineData("<tool_call>{\"name\": \"DeleteEverything\", \"arguments\": {}}</tool_call>")] // not an offered tool
    [InlineData("<tool_call>{\"name\": \"WriteFile\", \"arguments\": </tool_call>")]           // does not parse
    [InlineData("The format is <tool_call>, followed by JSON.")]                                // no complete block
    public async Task TextThatIsNotACompleteCallToAnOfferedTool_PassesThroughUnchanged(string text)
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse(text);

        var response = await client.GetResponseAsync("x", options, Ct);

        probe.Calls.Should().BeEmpty();
        response.Text.Should().Be(text);
    }

    [Fact]
    public async Task WithoutOfferedTools_TheTextPassesThrough()
    {
        var (client, model, probe, _) = Setup();
        model.EnqueueResponse(HermesBlock);

        var response = await client.GetResponseAsync("x", new ChatOptions(), Ct);

        probe.Calls.Should().BeEmpty();
        response.Text.Should().Be(HermesBlock);
    }

    [Fact]
    public async Task AResponseWithAStructuredCall_IsNotTouched()
    {
        var (client, model, probe, options) = Setup();
        model.EnqueueToolCallResponse("WriteFile", "{\"path\":\"s.txt\",\"content\":\"structured\"}", textContent: HermesBlock)
            .EnqueueResponse("ok");

        await client.GetResponseAsync("x", options, Ct);

        probe.Calls.Should().Equal("write s.txt: structured");
    }

    [Fact]
    public async Task Streaming_ABlockSplitAcrossChunks_RunsTheTool_AndNoMarkupReachesTheCaller()
    {
        // MockChatClient streams text in ten-character pieces, so «<tool_call>» arrives split.
        var (client, model, probe, options) = Setup();
        model.EnqueueResponse(HermesBlock).EnqueueResponse("Saved.");

        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("save 42", options, Ct))
        {
            text.Append(update.Text);
        }

        probe.Calls.Should().Equal("write answer.txt: 42");
        text.ToString().Should().NotContain("<tool").And.NotContain("tool_call>").And.Contain("I will save it now.").And.Contain("Saved.");
    }

    [Fact]
    public async Task Streaming_TextWithAnAngleBracketThatIsNotABlock_ArrivesUnchanged()
    {
        var (client, model, probe, options) = Setup();
        const string answer = "Use a < b and <b>bold</b>; the <function keyword is fine.";
        model.EnqueueResponse(answer);

        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("x", options, Ct))
        {
            text.Append(update.Text);
        }

        probe.Calls.Should().BeEmpty();
        text.ToString().Should().Be(answer);
    }

    [Fact]
    public void TheRecoveredCallsArguments_AreJsonValuesTheToolBindsTo()
    {
        // Guard on the assumption the coercion relies on: the schema M.E.AI generates types an int parameter as integer.
        var function = AIFunctionFactory.Create((string text, int times) => text, "Repeat");
        function.JsonSchema.GetProperty("properties").GetProperty("times").GetProperty("type").GetString()
            .Should().Be("integer");
        function.JsonSchema.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
