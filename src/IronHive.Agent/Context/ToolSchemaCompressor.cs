using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Compression level for tool schemas.
/// </summary>
public enum ToolSchemaCompressionLevel
{
    /// <summary>No compression — schemas passed as-is.</summary>
    None,

    /// <summary>Moderate compression — shorten descriptions, remove examples.</summary>
    Moderate,

    /// <summary>Aggressive compression — remove all descriptions, keep names and types only.</summary>
    Aggressive
}

/// <summary>
/// Compresses tool schemas to reduce token usage when sending tool definitions to the LLM.
/// </summary>
public static class ToolSchemaCompressor
{
    /// <summary>
    /// Compresses a list of AITool schemas by wrapping AIFunction instances.
    /// </summary>
    /// <param name="tools">Tools to compress.</param>
    /// <param name="level">Compression level.</param>
    /// <returns>Compressed tool list, or the original list if no compression is needed.</returns>
    public static IList<AITool> CompressTools(IList<AITool> tools, ToolSchemaCompressionLevel level)
    {
        if (level == ToolSchemaCompressionLevel.None || tools.Count == 0)
        {
            return tools;
        }

        var compressed = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is AIFunction func)
            {
                compressed.Add(new CompressedAIFunction(func, level));
            }
            else
            {
                compressed.Add(tool);
            }
        }

        return compressed;
    }

    /// <summary>
    /// Compresses a function/tool description based on the compression level.
    /// </summary>
    public static string CompressDescription(string? description, ToolSchemaCompressionLevel level)
    {
        if (string.IsNullOrEmpty(description))
        {
            return description ?? string.Empty;
        }

        return level switch
        {
            ToolSchemaCompressionLevel.Moderate => TruncateDescription(description, 100),
            ToolSchemaCompressionLevel.Aggressive => TruncateDescription(description, 40),
            _ => description
        };
    }

    /// <summary>
    /// Compresses a JSON Schema by removing verbose descriptions and examples.
    /// </summary>
    public static JsonElement CompressJsonSchema(JsonElement schema, ToolSchemaCompressionLevel level)
    {
        if (level == ToolSchemaCompressionLevel.None)
        {
            return schema;
        }

        var node = JsonNode.Parse(schema.GetRawText());
        if (node is not JsonObject obj)
        {
            return schema;
        }

        CompressSchemaNode(obj, level);

        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
    }

    private static void CompressSchemaNode(JsonObject schema, ToolSchemaCompressionLevel level)
    {
        if (level == ToolSchemaCompressionLevel.Aggressive)
        {
            schema.Remove("description");
        }
        else if (level == ToolSchemaCompressionLevel.Moderate)
        {
            TruncateNodeDescription(schema, 120);
        }

        if (schema.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject props)
        {
            foreach (var (_, value) in props.ToList())
            {
                if (value is JsonObject propObj)
                {
                    CompressPropertySchema(propObj, level);
                }
            }
        }
    }

    private static void CompressPropertySchema(JsonObject property, ToolSchemaCompressionLevel level)
    {
        switch (level)
        {
            case ToolSchemaCompressionLevel.Moderate:
                TruncateNodeDescription(property, 80);
                property.Remove("examples");
                break;

            case ToolSchemaCompressionLevel.Aggressive:
                property.Remove("description");
                property.Remove("examples");
                property.Remove("default");
                break;
        }

        // Recurse into nested object properties
        if (property.TryGetPropertyValue("properties", out var nestedProps) && nestedProps is JsonObject nested)
        {
            foreach (var (_, value) in nested.ToList())
            {
                if (value is JsonObject propObj)
                {
                    CompressPropertySchema(propObj, level);
                }
            }
        }

        // Recurse into array items
        if (property.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject items)
        {
            CompressPropertySchema(items, level);
        }
    }

    private static void TruncateNodeDescription(JsonObject obj, int maxLength)
    {
        if (obj.TryGetPropertyValue("description", out var descNode)
            && descNode is JsonValue descVal
            && descVal.TryGetValue<string>(out var desc)
            && desc.Length > maxLength)
        {
            obj["description"] = TruncateDescription(desc, maxLength);
        }
    }

    internal static string TruncateDescription(string desc, int maxLength)
    {
        if (desc.Length <= maxLength)
        {
            return desc;
        }

        // Try to cut at sentence boundary
        var cutoff = desc.LastIndexOf('.', maxLength - 1);
        if (cutoff > maxLength / 2)
        {
            return desc[..(cutoff + 1)];
        }

        // Cut at word boundary
        cutoff = desc.LastIndexOf(' ', maxLength - 4);
        if (cutoff > 0)
        {
            return string.Concat(desc.AsSpan(0, cutoff), "...");
        }

        return string.Concat(desc.AsSpan(0, maxLength - 3), "...");
    }
}

/// <summary>
/// Wraps an AIFunction with compressed description and JSON schema.
/// Delegates all invocations to the inner function.
/// </summary>
public sealed class CompressedAIFunction : DelegatingAIFunction
{
    private readonly string _description;
    private readonly JsonElement _jsonSchema;

    public CompressedAIFunction(AIFunction innerFunction, ToolSchemaCompressionLevel level)
        : base(innerFunction)
    {
        _description = ToolSchemaCompressor.CompressDescription(innerFunction.Description, level);
        _jsonSchema = ToolSchemaCompressor.CompressJsonSchema(innerFunction.JsonSchema, level);
    }

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _jsonSchema;
}
