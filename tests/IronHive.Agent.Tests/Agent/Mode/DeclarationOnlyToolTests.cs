using System.Text.Json;
using IronHive.Agent.Context;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tests.Agent.Mode;

/// <summary>
/// A host-executed tool is declared without an implementation (<see cref="AIFunctionFactory.CreateDeclaration(string, string?, JsonElement, JsonElement?)"/>).
/// Everywhere the loop reads a tool's name, description or schema it must see the declaration's own — before, these
/// sites read <see cref="AIFunction"/> only, so a declaration was known by its CLR type name and had no description.
/// </summary>
public class DeclarationOnlyToolTests
{
    private static readonly JsonElement TabSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"tab":{"type":"string","description":"The id of the open browser tab whose text to read, as listed by list_tabs."}},"required":["tab"]}""")
        .RootElement.Clone();

    private static AIFunctionDeclaration ReadPage(string description = "Reads one open browser tab's page text.")
        => AIFunctionFactory.CreateDeclaration("read_page", description, TabSchema);

    [Fact]
    public void Planning_Offers_A_Declared_ReadOnly_Host_Tool()
    {
        var filter = new ModeToolFilter(new PermissionConfig { ReadOnlyTools = ["read_page"] });
        IList<AITool> tools = [ReadPage(), AIFunctionFactory.CreateDeclaration("click_button", "Clicks.", TabSchema)];

        var offered = filter.FilterTools(tools, AgentMode.Planning).Select(t => t.Name).ToList();

        Assert.Equal(["read_page"], offered);
    }

    [Fact]
    public async Task Keyword_Retrieval_Matches_A_Declaration_By_Name_And_Description()
    {
        var retriever = new KeywordToolRetriever();
        IList<AITool> tools = [ReadPage(), AIFunctionFactory.Create(() => "ok", "delete_item", "Deletes a saved item.")];

        var result = await retriever.RetrieveAsync("read the page text of a browser tab", tools,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("read_page", result.SelectedTools[0].Name);
        Assert.True(result.RelevanceScores!.ContainsKey("read_page"));
    }

    [Fact]
    public void Compression_Compresses_A_Declaration_And_Keeps_It_Declaration_Only()
    {
        var longDescription = new string('x', 300);
        IList<AITool> tools = [ReadPage(longDescription)];

        var compressed = ToolSchemaCompressor.CompressTools(tools, ToolSchemaCompressionLevel.Aggressive).Single();

        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(compressed);
        Assert.IsNotAssignableFrom<AIFunction>(compressed); // still nothing to invoke: the host runs it
        Assert.Equal("read_page", declaration.Name);
        Assert.True(declaration.Description.Length < longDescription.Length);
        Assert.Equal("tab", declaration.JsonSchema.GetProperty("required")[0].GetString());
    }
}
