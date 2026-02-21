using System.ComponentModel;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// CE-04: IToolRetriever abstraction — keyword-based tool retrieval tests.
/// </summary>
public class KeywordToolRetrieverTests
{
    private readonly KeywordToolRetriever _retriever = new();

    #region Empty / Edge Cases

    [Fact]
    public async Task RetrieveAsync_EmptyTools_ReturnsEmpty()
    {
        var result = await _retriever.RetrieveAsync("read a file", []);

        Assert.Empty(result.SelectedTools);
        Assert.NotNull(result.RelevanceScores);
        Assert.Empty(result.RelevanceScores);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyQuery_ReturnsAlwaysIncludeOnly()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["ReadFile"]
        };

        var result = await _retriever.RetrieveAsync("", tools, options);

        Assert.Single(result.SelectedTools);
        Assert.Equal("ReadFile", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyQueryNoAlwaysInclude_ReturnsEmpty()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("   ", tools);

        Assert.Empty(result.SelectedTools);
    }

    #endregion

    #region Name Matching

    [Fact]
    public async Task RetrieveAsync_ExactNameMatch_HighScore()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read file", tools);

        Assert.NotEmpty(result.SelectedTools);
        var topTool = result.SelectedTools[0];
        Assert.Equal("ReadFile", GetName(topTool));

        var score = result.RelevanceScores!["ReadFile"];
        Assert.True(score > 0.5f, $"Expected high score for exact match, got {score}");
    }

    [Fact]
    public async Task RetrieveAsync_PartialNameMatch_IncludedAboveThreshold()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("file operations", tools);

        // Both ReadFile and WriteFile should match on "file"
        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ReadFile", names);
        Assert.Contains("WriteFile", names);
    }

    [Fact]
    public async Task RetrieveAsync_SnakeCaseQuery_MatchesCamelCase()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read_file", tools);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ReadFile", names);
    }

    #endregion

    #region Description Matching

    [Fact]
    public async Task RetrieveAsync_DescriptionKeyword_Matches()
    {
        var tools = CreateTestTools();

        // "directory" appears in ListDirectory description
        var result = await _retriever.RetrieveAsync("list directory contents", tools);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ListDirectory", names);
    }

    #endregion

    #region Scoring & Ranking

    [Fact]
    public async Task RetrieveAsync_RanksNameMatchAboveDescriptionMatch()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("grep files", tools);

        // GrepFiles should rank highest (name match)
        Assert.Equal("GrepFiles", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_BelowThreshold_Excluded()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { MinRelevanceScore = 0.9f };

        var result = await _retriever.RetrieveAsync("something completely unrelated xyz", tools, options);

        // Very high threshold + unrelated query → nothing should pass
        Assert.Empty(result.SelectedTools);
    }

    [Fact]
    public async Task RetrieveAsync_AllScoresReturned()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read", tools);

        // RelevanceScores should contain entries for all tools
        Assert.Equal(tools.Count, result.RelevanceScores!.Count);
    }

    #endregion

    #region MaxTools Limit

    [Fact]
    public async Task RetrieveAsync_RespectsMaxTools()
    {
        var tools = CreateTestTools(); // 5 tools
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f // Accept all
        };

        var result = await _retriever.RetrieveAsync("file read write list grep", tools, options);

        Assert.True(result.SelectedTools.Count <= 2);
    }

    [Fact]
    public async Task RetrieveAsync_AlwaysIncludeCountsTowardMax()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f,
            AlwaysInclude = ["ExecuteCommand"]
        };

        var result = await _retriever.RetrieveAsync("read file", tools, options);

        Assert.True(result.SelectedTools.Count <= 2);
        Assert.Contains("ExecuteCommand", result.SelectedTools.Select(GetName));
    }

    #endregion

    #region AlwaysInclude

    [Fact]
    public async Task RetrieveAsync_AlwaysInclude_AlwaysPresent()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["GrepFiles"],
            MaxTools = 10
        };

        // Query unrelated to grep
        var result = await _retriever.RetrieveAsync("write content to output", tools, options);

        Assert.Contains("GrepFiles", result.SelectedTools.Select(GetName));
    }

    [Fact]
    public async Task RetrieveAsync_AlwaysInclude_NoDuplication()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["ReadFile"],
            MinRelevanceScore = 0.0f
        };

        var result = await _retriever.RetrieveAsync("read file", tools, options);

        var readFileCount = result.SelectedTools.Count(t => GetName(t) == "ReadFile");
        Assert.Equal(1, readFileCount);
    }

    [Fact]
    public async Task RetrieveAsync_AlwaysInclude_NonExistentTool_Ignored()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["NonExistentTool"]
        };

        var result = await _retriever.RetrieveAsync("read file", tools, options);

        // Should not crash; NonExistentTool simply not found
        Assert.DoesNotContain("NonExistentTool", result.SelectedTools.Select(GetName));
    }

    #endregion

    #region Scoring Behavior (via public API)

    [Fact]
    public async Task RetrieveAsync_NameMatchScoresHigherThanDescriptionMatch()
    {
        var tools = CreateTestTools();

        // "grep" matches tool name GrepFiles directly
        var result = await _retriever.RetrieveAsync("grep", tools);

        var scores = result.RelevanceScores!;
        // GrepFiles has name match, others only have description match at best
        Assert.True(scores["GrepFiles"] > scores["ReadFile"]);
    }

    [Fact]
    public async Task RetrieveAsync_UnrelatedQuery_NoResults()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { MinRelevanceScore = 0.3f };

        var result = await _retriever.RetrieveAsync("quantum physics xyz", tools, options);

        Assert.Empty(result.SelectedTools);
    }

    [Fact]
    public async Task RetrieveAsync_CamelCaseTokenization_WorksForMatching()
    {
        var tools = CreateTestTools();

        // Query "list" should match "ListDirectory" via camelCase split
        var result = await _retriever.RetrieveAsync("list", tools);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ListDirectory", names);
    }

    #endregion

    #region Interface Contract

    [Fact]
    public async Task RetrieveAsync_ImplementsIToolRetriever()
    {
        // Verify polymorphic usage works correctly
        var retriever = new KeywordToolRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("read file", tools);

        Assert.NotNull(result);
        Assert.NotNull(result.SelectedTools);
    }

    [Fact]
    public void ToolRetrievalOptions_DefaultValues()
    {
        var options = new ToolRetrievalOptions();

        Assert.Equal(10, options.MaxTools);
        Assert.Equal(0.3f, options.MinRelevanceScore);
        Assert.Null(options.AlwaysInclude);
    }

    #endregion

    #region Helpers

    private static string GetName(AITool tool) =>
        tool is AIFunction func ? func.Name : tool.GetType().Name;

    private static IList<AITool> CreateTestTools()
    {
        return
        [
            AIFunctionFactory.Create(SampleTools.ReadFile),
            AIFunctionFactory.Create(SampleTools.WriteFile),
            AIFunctionFactory.Create(SampleTools.ListDirectory),
            AIFunctionFactory.Create(SampleTools.GrepFiles),
            AIFunctionFactory.Create(SampleTools.ExecuteCommand),
        ];
    }

    private static class SampleTools
    {
        [Description("Read the content of a file at the specified path.")]
        public static string ReadFile(
            [Description("File path to read")] string path) => $"content of {path}";

        [Description("Write content to a file. Creates the file if it doesn't exist.")]
        public static string WriteFile(
            [Description("File path to write")] string path,
            [Description("Content to write")] string content) => "ok";

        [Description("List the contents of a directory.")]
        public static string ListDirectory(
            [Description("Directory path")] string path) => "files";

        [Description("Search for a pattern in files using regex matching.")]
        public static string GrepFiles(
            [Description("Regex pattern")] string pattern,
            [Description("Directory to search")] string? path = null) => "matches";

        [Description("Execute a shell command and return the output.")]
        public static string ExecuteCommand(
            [Description("The command to execute")] string command) => "output";
    }

    #endregion
}
