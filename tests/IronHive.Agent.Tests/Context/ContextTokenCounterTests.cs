using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

public class ContextTokenCounterTests
{
    [Fact]
    public void CountTokens_EmptyString_ReturnsZero()
    {
        var counter = new ContextTokenCounter();
        Assert.Equal(0, counter.CountTokens(""));
    }

    [Fact]
    public void CountTokens_NullString_ReturnsZero()
    {
        var counter = new ContextTokenCounter();
        Assert.Equal(0, counter.CountTokens((string)null!));
    }

    [Fact]
    public void CountTokens_SimpleText_ReturnsPositiveCount()
    {
        var counter = new ContextTokenCounter();
        var tokens = counter.CountTokens("Hello, world!");
        Assert.True(tokens > 0);
    }

    [Fact]
    public void CountTokens_LongerText_ReturnsMoreTokens()
    {
        var counter = new ContextTokenCounter();
        var shortTokens = counter.CountTokens("Hi");
        var longTokens = counter.CountTokens("Hello, this is a much longer sentence with more words.");
        Assert.True(longTokens > shortTokens);
    }

    [Fact]
    public void CountTokens_ChatMessage_IncludesOverhead()
    {
        var counter = new ContextTokenCounter();
        var message = new ChatMessage(ChatRole.User, "Hello");
        var tokens = counter.CountTokens(message);

        // Should be more than just the text due to message overhead
        var textOnlyTokens = counter.CountTokens("Hello");
        Assert.True(tokens > textOnlyTokens);
    }

    [Fact]
    public void CountTokens_MessageList_SumsCorrectly()
    {
        var counter = new ContextTokenCounter();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.Assistant, "Hi there!")
        };

        var totalTokens = counter.CountTokens(messages);
        Assert.True(totalTokens > 0);

        // Should be close to sum of individual messages plus conversation overhead
        var msg1Tokens = counter.CountTokens(messages[0]);
        var msg2Tokens = counter.CountTokens(messages[1]);
        Assert.True(totalTokens >= msg1Tokens + msg2Tokens);
    }

    [Fact]
    public void MaxContextTokens_Gpt4o_Returns128000()
    {
        var counter = new ContextTokenCounter("gpt-4o");
        Assert.Equal(128000, counter.MaxContextTokens);
    }

    [Fact]
    public void MaxContextTokens_Claude35Sonnet_Returns200000()
    {
        var counter = new ContextTokenCounter("claude-3.5-sonnet");
        Assert.Equal(200000, counter.MaxContextTokens);
    }

    [Fact]
    public void MaxContextTokens_UnknownModel_ReturnsDefault()
    {
        var counter = new ContextTokenCounter("unknown-model");
        Assert.Equal(8192, counter.MaxContextTokens);
    }

    [Fact]
    public void MaxContextTokens_CustomOverride_ReturnsCustomValue()
    {
        var counter = new ContextTokenCounter("gpt-4o", maxContextTokens: 50000);
        Assert.Equal(50000, counter.MaxContextTokens);
    }

    [Fact]
    public void ForGpt4o_CreatesCorrectCounter()
    {
        var counter = ContextTokenCounter.ForGpt4o();
        Assert.Equal("gpt-4o", counter.ModelName);
        Assert.Equal(128000, counter.MaxContextTokens);
    }

    [Fact]
    public void ForClaude35Sonnet_CreatesCorrectCounter()
    {
        var counter = ContextTokenCounter.ForClaude35Sonnet();
        Assert.Equal("claude-3.5-sonnet", counter.ModelName);
        Assert.Equal(200000, counter.MaxContextTokens);
    }

    [Fact]
    public void CountTokens_MessageWithFunctionCall_IncludesFunctionTokens()
    {
        var counter = new ContextTokenCounter();
        var message = new ChatMessage(ChatRole.Assistant, "Let me help you with that.");
        message.Contents.Add(new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?>
        {
            ["path"] = "/some/file.txt"
        }));

        var tokens = counter.CountTokens(message);
        var textOnlyTokens = counter.CountTokens("Let me help you with that.");

        // Should include function call tokens
        Assert.True(tokens > textOnlyTokens + 10);
    }
}
