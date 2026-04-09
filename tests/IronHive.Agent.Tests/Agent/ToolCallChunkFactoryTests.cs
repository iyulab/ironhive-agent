using System.Text.Json;
using IronHive.Agent.Loop;
using IronHive.Agent.Tests.Mocks;
using Microsoft.Extensions.AI;
using FC = Microsoft.Extensions.AI.FunctionCallContent;

namespace IronHive.Agent.Tests.Agent;

/// <summary>
/// Contract tests for <see cref="ToolCallChunkFactory"/> and the
/// "complete-in-one-chunk, JSON-serialised arguments" guarantee that every built-in
/// <see cref="IAgentLoop"/> must honour.
///
/// Regression: prior to 0.2.10 <c>ThinkingAgentLoop</c> serialised arguments via
/// <c>IDictionary.ToString()</c>, producing <c>"System.Collections.Generic.Dictionary`2[...]"</c>
/// instead of JSON. See <c>claudedocs/issues/ISSUE-ironhive-2026-04-09-toolcallchunk-contract-clarity.md</c>
/// on the Filer side for the original report.
/// </summary>
public class ToolCallChunkFactoryTests
{
    private static readonly string[] NestedTags = ["cute", "fluffy"];


    [Fact]
    public void FromFunctionCall_WithArguments_SerializesAsJson()
    {
        var fc = new FC(
            callId: "call_123",
            name: "write_file",
            arguments: new Dictionary<string, object?>
            {
                ["path"] = "/tmp/a.txt",
                ["contents"] = "hello"
            });

        var chunk = ToolCallChunkFactory.FromFunctionCall(fc);

        Assert.Equal("call_123", chunk.Id);
        Assert.Equal("write_file", chunk.NameDelta);
        Assert.True(chunk.IsComplete);
        Assert.NotNull(chunk.ArgumentsDelta);

        // Must be valid JSON — this is the core guarantee.
        using var doc = JsonDocument.Parse(chunk.ArgumentsDelta!);
        Assert.Equal("/tmp/a.txt", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("contents").GetString());
    }

    [Fact]
    public void FromFunctionCall_WithEmptyArguments_ProducesEmptyJsonObject()
    {
        var fc = new FC(
            callId: "call_empty",
            name: "ping",
            arguments: new Dictionary<string, object?>());

        var chunk = ToolCallChunkFactory.FromFunctionCall(fc);

        Assert.NotNull(chunk.ArgumentsDelta);
        using var doc = JsonDocument.Parse(chunk.ArgumentsDelta!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Empty(doc.RootElement.EnumerateObject());
        Assert.True(chunk.IsComplete);
    }

    [Fact]
    public void FromFunctionCall_WithNullArguments_EmitsNullDelta()
    {
        var fc = new FC(
            callId: "call_null",
            name: "noop",
            arguments: null);

        var chunk = ToolCallChunkFactory.FromFunctionCall(fc);

        Assert.Null(chunk.ArgumentsDelta);
        Assert.True(chunk.IsComplete);
        Assert.Equal("noop", chunk.NameDelta);
    }

    [Fact]
    public void FromFunctionCall_WithNestedArguments_PreservesStructure()
    {
        var fc = new FC(
            callId: "call_nested",
            name: "search",
            arguments: new Dictionary<string, object?>
            {
                ["query"] = "cat pictures",
                ["filters"] = new Dictionary<string, object?>
                {
                    ["safeSearch"] = true,
                    ["limit"] = 10
                },
                ["tags"] = NestedTags
            });

        var chunk = ToolCallChunkFactory.FromFunctionCall(fc);

        Assert.NotNull(chunk.ArgumentsDelta);
        using var doc = JsonDocument.Parse(chunk.ArgumentsDelta!);
        Assert.Equal("cat pictures", doc.RootElement.GetProperty("query").GetString());
        Assert.True(doc.RootElement.GetProperty("filters").GetProperty("safeSearch").GetBoolean());
        Assert.Equal(10, doc.RootElement.GetProperty("filters").GetProperty("limit").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public void FromFunctionCall_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ToolCallChunkFactory.FromFunctionCall(null!));
    }

    [Fact]
    public void FromFunctionCall_DefaultsIsCompleteToTrue()
    {
        var fc = new FC(
            callId: "call_default",
            name: "x",
            arguments: null);

        var chunk = ToolCallChunkFactory.FromFunctionCall(fc);

        Assert.True(chunk.IsComplete);
    }

    /// <summary>
    /// End-to-end guarantee at the <see cref="AgentLoop.RunStreamingAsync"/> boundary:
    /// whatever chunk the loop yields, its <see cref="ToolCallChunk.ArgumentsDelta"/>
    /// must round-trip through <see cref="JsonDocument.Parse"/>. This is the test the
    /// issue explicitly asked for.
    /// </summary>
    [Fact]
    public async Task AgentLoop_StreamingToolCall_ArgumentsDeltaIsValidJson()
    {
        var mockClient = new MockChatClient()
            .EnqueueToolCallResponse("write_file", """{"path": "/tmp/a.txt", "contents": "hi"}""");

        var agentLoop = new AgentLoop(mockClient);

        var toolCallChunks = new List<ToolCallChunk>();
        await foreach (var chunk in agentLoop.RunStreamingAsync("please write the file"))
        {
            if (chunk.ToolCallDelta is not null)
            {
                toolCallChunks.Add(chunk.ToolCallDelta);
            }
        }

        Assert.NotEmpty(toolCallChunks);
        foreach (var chunk in toolCallChunks)
        {
            Assert.True(chunk.IsComplete,
                $"built-in loop must always emit complete chunks; got IsComplete=false for chunk {chunk.Id}");

            if (chunk.ArgumentsDelta is null)
            {
                continue;
            }

            // This line is the whole point of the issue: consumers parse ArgumentsDelta as JSON
            // and it must not blow up.
            using var doc = JsonDocument.Parse(chunk.ArgumentsDelta);
            Assert.Equal("/tmp/a.txt", doc.RootElement.GetProperty("path").GetString());
        }
    }
}
