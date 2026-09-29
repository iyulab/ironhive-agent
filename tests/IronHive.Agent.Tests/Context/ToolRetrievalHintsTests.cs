using System.Text.Json;
using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Context;

/// <summary>
/// Tool-declared retrieval hints (aliases, companions), exact-name rank and the selection trace.
/// The catalogue mirrors a file-editing agent: two pins and a scored tail of four.
/// </summary>
public class ToolRetrievalHintsTests
{
    private const string UndoRequest = "Undo that change. Put note.txt back the way it was before you changed it.";

    private static readonly ToolRetrievalOptions FourScoredSlots = new()
    {
        AlwaysInclude = ["read_file", "write_file"],
        MaxTools = 6,
        MinRelevanceScore = 0.25f,
    };

    private readonly KeywordToolRetriever _retriever = new();

    private static AIFunction Tool(string name, string description) =>
        AIFunctionFactory.Create(() => "ok", name, description);

    private static List<AITool> Catalogue(bool withHints)
    {
        AITool restore = Tool("restore_file_version", "Restore a file to an earlier saved version.");
        if (withHints)
        {
            restore = restore.WithRetrievalHints(
                aliases: ["undo", "revert", "roll back", "put back"],
                companions: ["list_file_versions"]);
        }

        return
        [
            Tool("read_file", "Read the contents of a file."),
            Tool("write_file", "Write text to a file, replacing its contents."),
            Tool("ComputeFileHash", "Compute a hash of a file's contents."),
            Tool("get_file_changes", "List the changes made to files in this session."),
            Tool("memorize_file", "Add a file to long-term memory."),
            Tool("unmemorize_file", "Remove a file from long-term memory."),
            restore,
            Tool("list_file_versions", "List the saved versions of a file."),
            Tool("GrepFiles", "Search file contents with a regular expression."),
        ];
    }

    private static List<string> Names(ToolRetrievalResult result) => result.SelectedTools.Select(t => t.Name).ToList();

    [Fact]
    public async Task WithoutHints_AnIntentWordDoesNotReachTheTool()
    {
        // Positive control: the lexical scorer alone cannot bridge "undo / put back" to "restore".
        var result = await _retriever.RetrieveAsync(UndoRequest, Catalogue(withHints: false), FourScoredSlots, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("restore_file_version", Names(result));
        Assert.DoesNotContain("list_file_versions", Names(result));
    }

    [Fact]
    public async Task ADeclaredAlias_SelectsTheTool_AndItsCompanionComesAlong()
    {
        var result = await _retriever.RetrieveAsync(UndoRequest, Catalogue(withHints: true), FourScoredSlots, TestContext.Current.CancellationToken);

        var restore = Assert.Single(result.Selections, s => s.Name == "restore_file_version");
        Assert.Equal(ToolSelectionReason.Alias, restore.Reason);
        Assert.True(restore.Score >= 0.75f);

        var list = Assert.Single(result.Selections, s => s.Name == "list_file_versions");
        Assert.Equal(ToolSelectionReason.Companion, list.Reason);

        Assert.Equal(Names(result), result.Selections.Select(s => s.Name));
        Assert.Equal(ToolSelectionReason.Pinned, result.Selections[0].Reason);
    }

    [Fact]
    public async Task AnExactName_TakesTheFirstScoredSlot_EvenWhenPinsExhaustTheBudget()
    {
        var options = new ToolRetrievalOptions
        {
            AlwaysInclude = ["read_file", "write_file"],
            MaxTools = 2,
            MinRelevanceScore = 0.9f,
        };

        var result = await _retriever.RetrieveAsync("use GrepFiles to find TODO markers", Catalogue(withHints: false), options, TestContext.Current.CancellationToken);

        Assert.Equal(["read_file", "write_file", "GrepFiles"], Names(result));
        Assert.Equal(ToolSelectionReason.ExactName, result.Selections[2].Reason);
    }

    [Fact]
    public async Task AnExactName_InBackticksOrWithTrailingPunctuation_StillCounts()
    {
        var result = await _retriever.RetrieveAsync("please call `restore_file_version`.", Catalogue(withHints: false),
            new ToolRetrievalOptions { MaxTools = 1, MinRelevanceScore = 0.99f }, TestContext.Current.CancellationToken);

        var first = Assert.Single(result.Selections);
        Assert.Equal(("restore_file_version", ToolSelectionReason.ExactName), (first.Name, first.Reason));
    }

    [Theory]
    [InlineData("Put the file in the backup folder")]   // "back" only as part of "backup"
    [InlineData("roll the dice")]                       // one word of a two-word alias
    [InlineData("recompute the undocumented totals")]   // "undo" only inside a longer word
    public async Task AnAlias_MatchesOnlyWholeWords_AndEveryWordOfAMultiWordAlias(string query)
    {
        var tools = new List<AITool>
        {
            Tool("restore_file_version", "Restore a file.").WithRetrievalHints(aliases: ["undo", "roll back", "put back"]),
        };

        var result = await _retriever.RetrieveAsync(query, tools, new ToolRetrievalOptions { MinRelevanceScore = 0.7f }, TestContext.Current.CancellationToken);

        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task Companions_AreBoundedByTheDeclaration_OneLevelDeep_AndUnknownNamesAreIgnored()
    {
        var owner = Tool("owner_tool", "Owner.").WithRetrievalHints(companions: ["c1", "missing", "c2", "c3", "c4"]);
        var tools = new List<AITool>
        {
            owner,
            Tool("c1", "First.").WithRetrievalHints(companions: ["grandchild"]),
            Tool("c2", "Second."),
            Tool("c3", "Third."),
            Tool("c4", "Fourth."),
            Tool("grandchild", "Not followed."),
        };

        var result = await _retriever.RetrieveAsync("", tools, new ToolRetrievalOptions { AlwaysInclude = ["owner_tool"] }, TestContext.Current.CancellationToken);

        // The first three declared names are followed; "missing" is not available and fills no slot of its own.
        Assert.Equal(["owner_tool", "c1", "c2"], Names(result));
        Assert.All(result.Selections.Skip(1), s => Assert.Equal(ToolSelectionReason.Companion, s.Reason));
    }

    [Fact]
    public void Hints_ReadTheStringFormsAStringOnlyTransportCarries()
    {
        var fromComma = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions
        {
            Name = "a",
            AdditionalProperties = new Dictionary<string, object?> { [ToolRetrievalHints.AliasesKey] = " undo, revert ,, " },
        });
        var fromJson = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions
        {
            Name = "b",
            AdditionalProperties = new Dictionary<string, object?>
            {
                [ToolRetrievalHints.CompanionsKey] = JsonSerializer.Deserialize<JsonElement>("[\"x\", \"y\"]"),
            },
        });

        Assert.Equal(["undo", "revert"], ToolRetrievalHints.GetAliases(fromComma));
        Assert.Equal(["x", "y"], ToolRetrievalHints.GetCompanions(fromJson));
    }

    [Fact]
    public void WithRetrievalHints_MergesWithDeclaredHints_AndKeepsTheToolsSurface()
    {
        var declaration = AIFunctionFactory.CreateDeclaration(
            "host_tool", "Runs on the host.", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"));

        var hinted = declaration.WithRetrievalHints(aliases: ["first"]).WithRetrievalHints(aliases: ["second", "FIRST"]);

        var hintedDeclaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(hinted);
        Assert.IsNotAssignableFrom<AIFunction>(hinted);
        Assert.Equal("host_tool", hintedDeclaration.Name);
        Assert.Equal("Runs on the host.", hintedDeclaration.Description);
        Assert.Equal(declaration.JsonSchema.GetRawText(), hintedDeclaration.JsonSchema.GetRawText());
        Assert.Equal(["first", "second"], ToolRetrievalHints.GetAliases(hinted));
    }

    [Fact]
    public async Task AHintedFunction_StillInvokesTheOriginal()
    {
        var calls = 0;
        var hinted = (AIFunction)AIFunctionFactory.Create(() => ++calls, "counter").WithRetrievalHints(aliases: ["tally"]);

        await hinted.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
    }
}

public class McpRetrievalHintsTests
{
    private static ModelContextProtocol.Client.McpClientTool McpTool(System.Text.Json.Nodes.JsonObject? meta) =>
        new(NSubstitute.Substitute.For<ModelContextProtocol.Client.McpClient>(),
            new ModelContextProtocol.Protocol.Tool
            {
                Name = "restore_file_version",
                Description = "Restore a file to an earlier saved version.",
                InputSchema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"),
                Meta = meta,
            });

    [Fact]
    public void HintsAnMcpServerDeclaresInMeta_ReachTheRetriever()
    {
        var tool = IronHive.Agent.Mcp.McpPluginManager.WithDeclaredRetrievalHints(McpTool(new()
        {
            [ToolRetrievalHints.AliasesKey] = "undo, revert",
            [ToolRetrievalHints.CompanionsKey] = new System.Text.Json.Nodes.JsonArray("list_file_versions"),
        }));

        Assert.Equal("restore_file_version", tool.Name);
        Assert.Equal(["undo", "revert"], ToolRetrievalHints.GetAliases(tool));
        Assert.Equal(["list_file_versions"], ToolRetrievalHints.GetCompanions(tool));
    }

    [Fact]
    public void AnMcpToolWithoutHints_IsReturnedUnchanged()
    {
        var original = McpTool(new() { ["other"] = "x" });

        Assert.Same(original, IronHive.Agent.Mcp.McpPluginManager.WithDeclaredRetrievalHints(original));
    }
}
