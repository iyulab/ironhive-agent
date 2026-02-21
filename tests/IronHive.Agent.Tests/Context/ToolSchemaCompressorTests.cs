using System.ComponentModel;
using System.Text.Json;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-02: Tool Schema Compression — reduce tool definition tokens via schema compression.
/// </summary>
public class ToolSchemaCompressorTests
{
    #region Description Compression

    [Fact]
    public void CompressDescription_None_ReturnsOriginal()
    {
        var desc = "This is a long description that explains what the tool does in great detail.";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.None);
        Assert.Equal(desc, result);
    }

    [Fact]
    public void CompressDescription_Moderate_ShortDescUnchanged()
    {
        var desc = "Read a file.";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.Moderate);
        Assert.Equal(desc, result);
    }

    [Fact]
    public void CompressDescription_Moderate_LongDescTruncated()
    {
        var desc = "This is a very long description that explains in extreme detail what the tool does, including all edge cases, error handling, and performance characteristics of the operation.";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.Moderate);
        Assert.True(result.Length <= 103); // 100 + "..."
    }

    [Fact]
    public void CompressDescription_Aggressive_TruncatesMore()
    {
        var desc = "This is a moderately long description that explains things.";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.Aggressive);
        Assert.True(result.Length <= 43); // 40 + "..."
    }

    [Fact]
    public void CompressDescription_NullOrEmpty_ReturnsAsIs()
    {
        Assert.Equal(string.Empty, ToolSchemaCompressor.CompressDescription(null, ToolSchemaCompressionLevel.Moderate));
        Assert.Equal(string.Empty, ToolSchemaCompressor.CompressDescription("", ToolSchemaCompressionLevel.Moderate));
    }

    [Fact]
    public void CompressDescription_CutsAtSentenceBoundary()
    {
        // Use Moderate level (100 char limit) with a sentence boundary past half the limit
        var desc = "This is a fairly detailed first sentence that explains the main purpose. Then it goes on to explain more things in great detail that nobody needs.";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.Moderate);
        Assert.Equal("This is a fairly detailed first sentence that explains the main purpose.", result);
    }

    [Fact]
    public void CompressDescription_CutsAtWordBoundary()
    {
        // No sentence boundary available, should cut at word boundary
        var desc = "This is a long sentence without any period that goes beyond the maximum allowed length";
        var result = ToolSchemaCompressor.CompressDescription(desc, ToolSchemaCompressionLevel.Aggressive);
        Assert.EndsWith("...", result);
        Assert.True(result.Length <= 43); // 40 + "..."
    }

    #endregion

    #region JSON Schema Compression

    [Fact]
    public void CompressJsonSchema_None_ReturnsOriginal()
    {
        var schema = CreateTestSchema();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.None);
        Assert.Equal(schema.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void CompressJsonSchema_Moderate_ShortenDescriptions()
    {
        var schema = CreateSchemaWithLongDescription();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Moderate);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        var pathDesc = resultObj.RootElement
            .GetProperty("properties")
            .GetProperty("path")
            .GetProperty("description")
            .GetString();

        // Should be truncated
        Assert.True(pathDesc!.Length <= 83); // 80 + "..."
    }

    [Fact]
    public void CompressJsonSchema_Moderate_RemovesExamples()
    {
        var schema = CreateSchemaWithExamples();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Moderate);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        var pathProp = resultObj.RootElement
            .GetProperty("properties")
            .GetProperty("path");

        Assert.False(pathProp.TryGetProperty("examples", out _));
    }

    [Fact]
    public void CompressJsonSchema_Aggressive_RemovesAllDescriptions()
    {
        var schema = CreateTestSchema();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Aggressive);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        var pathProp = resultObj.RootElement
            .GetProperty("properties")
            .GetProperty("path");

        Assert.False(pathProp.TryGetProperty("description", out _));
    }

    [Fact]
    public void CompressJsonSchema_Aggressive_PreservesTypes()
    {
        var schema = CreateTestSchema();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Aggressive);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        var pathType = resultObj.RootElement
            .GetProperty("properties")
            .GetProperty("path")
            .GetProperty("type")
            .GetString();

        Assert.Equal("string", pathType);
    }

    [Fact]
    public void CompressJsonSchema_Aggressive_PreservesRequired()
    {
        var schema = CreateTestSchema();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Aggressive);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        Assert.True(resultObj.RootElement.TryGetProperty("required", out var req));
        Assert.True(req.GetArrayLength() > 0);
    }

    [Fact]
    public void CompressJsonSchema_Aggressive_RemovesDefaults()
    {
        var schema = CreateSchemaWithDefaults();
        var result = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Aggressive);

        var resultObj = JsonDocument.Parse(result.GetRawText());
        var optionalProp = resultObj.RootElement
            .GetProperty("properties")
            .GetProperty("recursive");

        Assert.False(optionalProp.TryGetProperty("default", out _));
    }

    #endregion

    #region CompressTools

    [Fact]
    public void CompressTools_None_ReturnsSameList()
    {
        var tools = CreateTestTools();
        var result = ToolSchemaCompressor.CompressTools(tools, ToolSchemaCompressionLevel.None);
        Assert.Same(tools, result);
    }

    [Fact]
    public void CompressTools_Empty_ReturnsSameList()
    {
        IList<AITool> tools = [];
        var result = ToolSchemaCompressor.CompressTools(tools, ToolSchemaCompressionLevel.Moderate);
        Assert.Same(tools, result);
    }

    [Fact]
    public void CompressTools_Moderate_WrapsAIFunctions()
    {
        var tools = CreateTestTools();
        var result = ToolSchemaCompressor.CompressTools(tools, ToolSchemaCompressionLevel.Moderate);

        Assert.NotSame(tools, result);
        Assert.Equal(tools.Count, result.Count);

        foreach (var tool in result)
        {
            Assert.IsType<CompressedAIFunction>(tool);
        }
    }

    [Fact]
    public void CompressTools_PreservesToolName()
    {
        var tools = CreateTestTools();
        var result = ToolSchemaCompressor.CompressTools(tools, ToolSchemaCompressionLevel.Moderate);

        for (var i = 0; i < tools.Count; i++)
        {
            Assert.Equal(tools[i].Name, result[i].Name);
        }
    }

    #endregion

    #region CompressedAIFunction

    [Fact]
    public void CompressedAIFunction_Moderate_CompressesDescription()
    {
        var func = CreateToolWithLongDescription();
        var compressed = new CompressedAIFunction(func, ToolSchemaCompressionLevel.Moderate);

        Assert.True(compressed.Description.Length < func.Description.Length);
    }

    [Fact]
    public void CompressedAIFunction_PreservesName()
    {
        var func = CreateSimpleTool();
        var compressed = new CompressedAIFunction(func, ToolSchemaCompressionLevel.Aggressive);

        Assert.Equal(func.Name, compressed.Name);
    }

    [Fact]
    public async Task CompressedAIFunction_DelegatesInvocation()
    {
        var func = CreateSimpleTool();
        var compressed = new CompressedAIFunction(func, ToolSchemaCompressionLevel.Aggressive);

        var result = await compressed.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["input"] = "test"
        }));

        Assert.Equal("echo: test", result?.ToString());
    }

    #endregion

    #region Token Savings Estimation

    [Fact]
    public void CompressJsonSchema_Moderate_ReducesSize()
    {
        var schema = CreateSchemaWithLongDescription();
        var original = schema.GetRawText();

        var compressed = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Moderate);
        var compressedText = compressed.GetRawText();

        Assert.True(compressedText.Length < original.Length,
            $"Compressed ({compressedText.Length}) should be smaller than original ({original.Length})");
    }

    [Fact]
    public void CompressJsonSchema_Aggressive_ReducesMoreThanModerate()
    {
        var schema = CreateSchemaWithLongDescription();

        var moderate = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Moderate);
        var aggressive = ToolSchemaCompressor.CompressJsonSchema(schema, ToolSchemaCompressionLevel.Aggressive);

        Assert.True(aggressive.GetRawText().Length <= moderate.GetRawText().Length,
            "Aggressive should be at least as small as moderate");
    }

    #endregion

    #region CompactionConfig Integration

    [Fact]
    public void CompactionConfig_DefaultToolSchemaCompression_IsNone()
    {
        var config = new CompactionConfig();
        Assert.Equal(ToolSchemaCompressionLevel.None, config.ToolSchemaCompression);
    }

    #endregion

    #region Helper Methods

    private static JsonElement CreateTestSchema()
    {
        var json = """
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "The absolute or relative path to the file to read"
                },
                "startLine": {
                    "type": "integer",
                    "description": "Line number to start reading from (1-based)"
                }
            },
            "required": ["path"]
        }
        """;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement CreateSchemaWithLongDescription()
    {
        var json = """
        {
            "type": "object",
            "description": "This is a very long schema description that explains everything about the parameters and their usage in extreme detail.",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "The absolute or relative path to the file to read. This path can be either a relative path from the working directory or an absolute path on the filesystem. Symbolic links are followed."
                },
                "startLine": {
                    "type": "integer",
                    "description": "The 1-based line number to start reading from. If not specified, reading starts from the beginning of the file. Must be a positive integer."
                }
            },
            "required": ["path"]
        }
        """;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement CreateSchemaWithExamples()
    {
        var json = """
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "File path",
                    "examples": ["/home/user/file.txt", "src/main.cs"]
                }
            }
        }
        """;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static JsonElement CreateSchemaWithDefaults()
    {
        var json = """
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Directory path"
                },
                "recursive": {
                    "type": "boolean",
                    "description": "Whether to list recursively",
                    "default": false
                }
            }
        }
        """;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static IList<AITool> CreateTestTools()
    {
        return
        [
            AIFunctionFactory.Create(SampleTool.ReadFile),
            AIFunctionFactory.Create(SampleTool.WriteFile),
        ];
    }

    private static AIFunction CreateSimpleTool()
    {
        return AIFunctionFactory.Create(SampleTool.Echo);
    }

    private static AIFunction CreateToolWithLongDescription()
    {
        return AIFunctionFactory.Create(SampleTool.ReadFile);
    }

    /// <summary>
    /// Sample tool methods for testing schema compression.
    /// </summary>
    private static class SampleTool
    {
        [Description("Read the content of a file at the specified path. Returns the file content as text. Supports partial reading with startLine and lineCount parameters for large files.")]
        public static string ReadFile(
            [Description("The absolute or relative path to the file to read. This path can be either a relative path from the working directory or an absolute filesystem path.")] string path,
            [Description("The 1-based line number to start reading from. If not specified, reads from the beginning.")] int? startLine = null,
            [Description("Number of lines to read. If not specified, reads the entire file from startLine.")] int? lineCount = null)
        {
            return $"content of {path}";
        }

        [Description("Write content to a file at the specified path. Creates the file if it doesn't exist, overwrites if it does. Can also append to existing files.")]
        public static string WriteFile(
            [Description("The path to the file to write")] string path,
            [Description("The content to write to the file")] string content,
            [Description("If true, append to existing file instead of overwriting")] bool append = false)
        {
            return $"wrote to {path}";
        }

        [Description("Echo the input")]
        public static string Echo([Description("Input text")] string input)
        {
            return $"echo: {input}";
        }
    }

    #endregion
}
