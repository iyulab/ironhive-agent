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
        var result = await _retriever.RetrieveAsync("read a file", [], cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("", tools, options, TestContext.Current.CancellationToken);

        Assert.Single(result.SelectedTools);
        Assert.Equal("ReadFile", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyQueryNoAlwaysInclude_ReturnsEmpty()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("   ", tools, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.SelectedTools);
    }

    #endregion

    #region Name Matching

    [Fact]
    public async Task RetrieveAsync_ExactNameMatch_HighScore()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read file", tools, cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("file operations", tools, cancellationToken: TestContext.Current.CancellationToken);

        // Both ReadFile and WriteFile should match on "file"
        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ReadFile", names);
        Assert.Contains("WriteFile", names);
    }

    [Fact]
    public async Task RetrieveAsync_SnakeCaseQuery_MatchesCamelCase()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read_file", tools, cancellationToken: TestContext.Current.CancellationToken);

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
        var result = await _retriever.RetrieveAsync("list directory contents", tools, cancellationToken: TestContext.Current.CancellationToken);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ListDirectory", names);
    }

    #endregion

    #region Scoring & Ranking

    [Fact]
    public async Task RetrieveAsync_RanksNameMatchAboveDescriptionMatch()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("grep files", tools, cancellationToken: TestContext.Current.CancellationToken);

        // GrepFiles should rank highest (name match)
        Assert.Equal("GrepFiles", GetName(result.SelectedTools[0]));
    }

    [Fact]
    public async Task RetrieveAsync_BelowThreshold_Excluded()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { MinRelevanceScore = 0.9f };

        var result = await _retriever.RetrieveAsync("something completely unrelated xyz", tools, options, TestContext.Current.CancellationToken);

        // Very high threshold + unrelated query → nothing should pass
        Assert.Empty(result.SelectedTools);
    }

    [Fact]
    public async Task RetrieveAsync_AllScoresReturned()
    {
        var tools = CreateTestTools();

        var result = await _retriever.RetrieveAsync("read", tools, cancellationToken: TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("file read write list grep", tools, options, TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("read file", tools, options, TestContext.Current.CancellationToken);

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
        var result = await _retriever.RetrieveAsync("write content to output", tools, options, TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("read file", tools, options, TestContext.Current.CancellationToken);

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

        var result = await _retriever.RetrieveAsync("read file", tools, options, TestContext.Current.CancellationToken);

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
        var result = await _retriever.RetrieveAsync("grep", tools, cancellationToken: TestContext.Current.CancellationToken);

        var scores = result.RelevanceScores!;
        // GrepFiles has name match, others only have description match at best
        Assert.True(scores["GrepFiles"] > scores["ReadFile"]);
    }

    [Fact]
    public async Task RetrieveAsync_UnrelatedQuery_NoResults()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { MinRelevanceScore = 0.3f };

        var result = await _retriever.RetrieveAsync("quantum physics xyz", tools, options, TestContext.Current.CancellationToken);

        Assert.Empty(result.SelectedTools);
    }

    [Fact]
    public async Task RetrieveAsync_CamelCaseTokenization_WorksForMatching()
    {
        var tools = CreateTestTools();

        // Query "list" should match "ListDirectory" via camelCase split
        var result = await _retriever.RetrieveAsync("list", tools, cancellationToken: TestContext.Current.CancellationToken);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ListDirectory", names);
    }

    #endregion

    #region Query Length Independence

    /// <summary>
    /// A relevance score must answer "does this tool fit the query", not "what fraction of the
    /// query is about this tool". Scoring the latter makes every tool fall towards zero as the
    /// prompt grows, so tool access starves exactly when the prompt carries the most instruction.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_LongerQuery_DoesNotLowerAToolsScore()
    {
        var tools = CreateTestTools();

        var shortResult = await _retriever.RetrieveAsync("read file", tools, cancellationToken: TestContext.Current.CancellationToken);
        var longResult = await _retriever.RetrieveAsync(ReadFilePromptWithInstructions, tools, cancellationToken: TestContext.Current.CancellationToken);

        var shortScore = shortResult.RelevanceScores!["ReadFile"];
        var longScore = longResult.RelevanceScores!["ReadFile"];

        Assert.True(
            longScore >= shortScore,
            $"The long prompt names the tool and carries strictly more matching evidence, " +
            $"yet scored {longScore} against {shortScore}");
    }

    [Fact]
    public async Task RetrieveAsync_LongInstructionPrompt_StillOffersTheNamedTool()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions { AlwaysInclude = ["ExecuteCommand"] };

        var result = await _retriever.RetrieveAsync(ReadFilePromptWithInstructions, tools, options, TestContext.Current.CancellationToken);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Contains("ReadFile", names);
    }

    /// <summary>
    /// Normalising name and description against one shared denominator buries the name signal
    /// under a verbose description, so a well-documented tool scores lower than a sparse one.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_VerboseDescription_DoesNotScoreBelowASparseOne()
    {
        IList<AITool> tools =
        [
            AIFunctionFactory.Create(DocumentationTools.ConvertAudio),
            AIFunctionFactory.Create(DocumentationTools.ConvertVideo),
        ];

        var result = await _retriever.RetrieveAsync("convert audio", tools, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            result.RelevanceScores!["ConvertAudio"] >= result.RelevanceScores!["ConvertVideo"],
            "the tool the query names must not be outranked by a less documented sibling");
    }

    [Fact]
    public async Task RetrieveAsync_TwoCharacterQueryToken_DoesNotClaimAToolNameMatch()
    {
        var tools = CreateTestTools();

        // "to" is a substring of "Directory". Accepting that as a name match hands an unrelated
        // tool full name coverage on a stopword.
        var result = await _retriever.RetrieveAsync("to", tools, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(
            result.RelevanceScores!["ListDirectory"] < 0.3f,
            $"a two-character stopword must not carry a name match, got {result.RelevanceScores!["ListDirectory"]}");
        Assert.DoesNotContain("ListDirectory", result.SelectedTools.Select(GetName));
    }

    private const string ReadFilePromptWithInstructions = """
        Use ReadFile to inspect the target before making any change. Work through the request one
        step at a time and explain what you are doing as you go. Prefer the smallest change that
        satisfies the requirement, and keep the existing structure and naming intact wherever it
        already works. When several approaches are viable, pick the one a reviewer would find
        easiest to follow later. Do not restate the request back before starting, and do not
        summarise what you already said. If something in the request is ambiguous, state the
        assumption you are proceeding under instead of stopping to ask. Report what you changed
        once the work is finished, including anything you deliberately left alone and why.
        """;

    private static class DocumentationTools
    {
        [Description("Convert an audio file from one encoding to another. Supports the common " +
                     "container formats, resamples when the target rate differs, preserves the " +
                     "channel layout unless an explicit downmix is requested, and writes the " +
                     "result next to the source unless another destination is given.")]
        public static string ConvertAudio(
            [Description("Source path")] string path,
            [Description("Target format")] string format) => "converted";

        [Description("Convert a video file.")]
        public static string ConvertVideo(
            [Description("Source path")] string path,
            [Description("Target format")] string format) => "converted";
    }

    #endregion

    #region Interface Contract

    [Fact]
    public async Task RetrieveAsync_ImplementsIToolRetriever()
    {
        // Verify polymorphic usage works correctly
        var retriever = new KeywordToolRetriever();
        var tools = CreateTestTools();

        var result = await retriever.RetrieveAsync("read file", tools, cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(0, options.MinScoredSlots);
    }

    #endregion

    #region MinScoredSlots (reserved scored-slot budget)

    /// <summary>
    /// A caller that grows AlwaysInclude at runtime (e.g. merging plugin tool names into the pin
    /// list) can push pinnedCount past MaxTools. Without a reserved floor the scored tail collapses
    /// to zero — MinScoredSlots exists exactly to stop that.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_PinsExceedMaxTools_ScoredTailStillReservesMinScoredSlots()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f,
            AlwaysInclude = ["ReadFile", "WriteFile", "ListDirectory"], // 3 pins, already > MaxTools
            MinScoredSlots = 2
        };

        var result = await _retriever.RetrieveAsync("grep execute", tools, options, TestContext.Current.CancellationToken);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Equal(5, names.Count); // 3 pins + 2 reserved scored slots
        Assert.Contains("GrepFiles", names);
        Assert.Contains("ExecuteCommand", names);
    }

    /// <summary>
    /// Default MinScoredSlots (0) must reproduce the exact prior behavior: pins alone exceeding
    /// MaxTools leave zero budget for scored tools. This pins the backward-compat contract.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_MinScoredSlotsDefault_PinsExceedingMaxTools_LeavesNoScoredBudget()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f,
            AlwaysInclude = ["ReadFile", "WriteFile", "ListDirectory"] // 3 pins, already > MaxTools
            // MinScoredSlots left at default (0)
        };

        var result = await _retriever.RetrieveAsync("grep execute", tools, options, TestContext.Current.CancellationToken);

        var names = result.SelectedTools.Select(GetName).ToList();
        Assert.Equal(3, names.Count); // pins only, no scored tail
        Assert.DoesNotContain("GrepFiles", names);
        Assert.DoesNotContain("ExecuteCommand", names);
    }

    /// <summary>
    /// The floor must never exceed MaxTools, so a deployment that has deliberately lowered MaxTools
    /// (e.g. a small-context model capping tool-schema token cost) is respected.
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_MinScoredSlotsAboveMaxTools_ClampedToMaxTools()
    {
        var tools = CreateTestTools();
        var options = new ToolRetrievalOptions
        {
            MaxTools = 2,
            MinRelevanceScore = 0.0f,
            MinScoredSlots = 10 // far above MaxTools and the tool count
        };

        var result = await _retriever.RetrieveAsync("file read write list grep execute", tools, options, TestContext.Current.CancellationToken);

        Assert.True(result.SelectedTools.Count <= 2,
            $"MinScoredSlots must clamp to MaxTools, got {result.SelectedTools.Count} tools");
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
