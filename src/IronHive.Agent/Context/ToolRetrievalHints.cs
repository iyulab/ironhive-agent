using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Retrieval hints a tool declares about itself, carried in <see cref="AITool.AdditionalProperties"/>
/// so they travel with the tool whichever way it was created (an in-process <see cref="AIFunction"/>,
/// a declaration-only tool, a tool loaded from an MCP server).
/// </summary>
/// <remarks>
/// <para>
/// <b>Aliases</b> are the words people use for a tool when they do not use its name — "undo", "revert",
/// "put back" for a tool named <c>restore_file_version</c>. A lexical scorer cannot bridge those words to
/// the name. A query that holds an alias (every word of a multi-word alias, each as a whole word) scores the
/// tool as if it named it. An embedding retriever embeds the aliases with the tool's description.
/// </para>
/// <para>
/// <b>Companions</b> are tools used together with this one — a restore tool needs the tool that lists the
/// versions it can restore. When the tool is selected, its companions are selected too, outside the scored
/// budget: a companion the budget cuts leaves the selected tool half-usable. Only the first
/// <see cref="MaxCompanionsPerTool"/> declared companions are followed, one level deep.
/// </para>
/// <para>
/// A value is either a sequence of strings or one comma-separated string (the form a string-only
/// transport such as MCP <c>_meta</c> can carry).
/// </para>
/// </remarks>
public static class ToolRetrievalHints
{
    /// <summary>The <see cref="AITool.AdditionalProperties"/> key for a tool's retrieval aliases.</summary>
    public const string AliasesKey = "ironhive.retrieval.aliases";

    /// <summary>The <see cref="AITool.AdditionalProperties"/> key for the tools this tool is used with.</summary>
    public const string CompanionsKey = "ironhive.retrieval.companions";

    /// <summary>How many of a tool's declared companions are followed.</summary>
    public const int MaxCompanionsPerTool = 3;

    /// <summary>The aliases <paramref name="tool"/> declares, or an empty list.</summary>
    public static IReadOnlyList<string> GetAliases(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Read(tool.AdditionalProperties, AliasesKey);
    }

    /// <summary>The companion tool names <paramref name="tool"/> declares, or an empty list.</summary>
    public static IReadOnlyList<string> GetCompanions(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Read(tool.AdditionalProperties, CompanionsKey);
    }

    /// <summary>
    /// Returns <paramref name="tool"/> carrying the given retrieval hints, merged with any it already declares.
    /// The returned tool invokes and describes itself exactly as <paramref name="tool"/> does.
    /// </summary>
    /// <param name="tool">An <see cref="AIFunction"/> or an <see cref="AIFunctionDeclaration"/>.</param>
    /// <param name="aliases">Words people use for the tool instead of its name.</param>
    /// <param name="companions">Names of the tools it is used with.</param>
    /// <exception cref="ArgumentException"><paramref name="tool"/> is neither an <see cref="AIFunction"/> nor an <see cref="AIFunctionDeclaration"/>.</exception>
    public static AITool WithRetrievalHints(
        this AITool tool,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? companions = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in tool.AdditionalProperties)
        {
            properties[key] = value;
        }

        Merge(properties, AliasesKey, aliases);
        Merge(properties, CompanionsKey, companions);

        return tool switch
        {
            AIFunction function => new HintedFunction(function, properties),
            AIFunctionDeclaration declaration => new HintedDeclaration(declaration, properties),
            _ => throw new ArgumentException(
                $"Retrieval hints can be attached to an AIFunction or an AIFunctionDeclaration, not to {tool.GetType().Name}.",
                nameof(tool)),
        };
    }

    private static void Merge(AdditionalPropertiesDictionary properties, string key, IEnumerable<string>? added)
    {
        if (added is null)
        {
            return;
        }

        var merged = Read(properties, key).Concat(Normalize(added))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        properties[key] = merged;
    }

    private static string[] Read(IReadOnlyDictionary<string, object?>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? ParseValue(value) : [];

    /// <summary>
    /// Reads a hint value as the retrievers do: a sequence of strings, one comma-separated string, or the JSON form of
    /// either (a <see cref="JsonElement"/> string or string array). Entries are trimmed; anything else reads as no hints.
    /// For a host that carries hints from its own tool source (e.g. an MCP client of its own) into
    /// <see cref="WithRetrievalHints"/>.
    /// </summary>
    public static string[] ParseValue(object? value) => value switch
    {
        string text => Normalize(text.Split(',')),
        IEnumerable<string> items => Normalize(items),
        JsonElement { ValueKind: JsonValueKind.String } element => Normalize(element.GetString()!.Split(',')),
        JsonElement { ValueKind: JsonValueKind.Array } element => Normalize(
            element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)),
        _ => [],
    };

    private static string[] Normalize(IEnumerable<string> items) =>
        items.Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();

    private sealed class HintedFunction(AIFunction inner, AdditionalPropertiesDictionary properties)
        : DelegatingAIFunction(inner)
    {
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => properties;
    }

    // M.E.AI keeps its delegating declaration internal, so this forwards the declaration surface itself.
    private sealed class HintedDeclaration(AIFunctionDeclaration inner, AdditionalPropertiesDictionary properties)
        : AIFunctionDeclaration
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => properties;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : inner.GetService(serviceType, serviceKey);

        public override string ToString() => inner.ToString();
    }
}
