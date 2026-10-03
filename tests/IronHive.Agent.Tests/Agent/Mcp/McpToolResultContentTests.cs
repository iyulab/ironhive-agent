using AwesomeAssertions;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;
using Xunit;

namespace IronHive.Agent.Tests.Agent.Mcp;

/// <summary>
/// An MCP tool that returns an image hands the loop content (text and data parts), not a string. The context
/// machinery — token count, observation masking, compaction, result records — reads it as text with the image named,
/// never as the list's type name.
/// </summary>
public class McpToolResultContentTests
{
    private static List<AIContent> ImageResult(int bytes = 2048) => [new TextContent("redline"), new DataContent(new byte[bytes], "image/png")];

    [Fact]
    public void ToolResultText_NamesTheImage_NeverTheTypeOrTheBytes()
    {
        ToolResultText.Of(ImageResult()).Should().Be("redline\n[image image/png, 2 KB]");
        ToolResultText.ImageCount(ImageResult()).Should().Be(1);
        ToolResultText.Of(new TextContent("plain")).Should().Be("plain");
        ToolResultText.Of(null).Should().BeEmpty();
    }

    [Fact]
    public void TokenCounter_ChargesTheImage_NotTheTypeName()
    {
        var withImage = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", ImageResult(10))]);
        var textOnly = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "redline\n[image image/png, 10 B]")]);

        var counter = new ContextTokenCounter();
        (counter.CountTokens([withImage]) - counter.CountTokens([textOnly])).Should().Be(ToolResultText.ImageTokens);
    }
}
