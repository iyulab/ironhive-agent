using System.Text.Json;
using IronHive.Agent.Mcp;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Recognises a tool failure carried in a result rather than thrown. The loop guards read results through this, so a
/// tool that reports failure as a value counts the same as one that throws.
/// </summary>
/// <remarks>
/// Built in: an MCP result with <c>isError: true</c> — as a <see cref="CallToolResult"/>, as the
/// <see cref="JsonElement"/> an MCP client tool returns for it, or as an <see cref="McpToolResult"/> (also serialized,
/// as a function made by <c>AIFunctionFactory</c> returns it). A host adds its
/// own shapes through <see cref="ToolInvocationOptions.FailureOf"/>, which is asked first.
/// </remarks>
internal static class ToolResultFailure
{
    /// <summary>The error text when <paramref name="result"/> is a failure, otherwise null.</summary>
    public static string? Of(object? result, ToolInvocationOptions options)
    {
        if (result is null)
        {
            return null;
        }

        // A function made by AIFunctionFactory returns its value serialized; a host recognising its own string
        // convention should not have to unwrap that.
        var forHost = result is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : result;
        return options.FailureOf?.Invoke(forHost) ?? BuiltIn(result);
    }

    private static string? BuiltIn(object result) => result switch
    {
        CallToolResult { IsError: true } call => FirstText(call.Content),
        McpToolResult { IsError: true } mcp => mcp.Content ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Object } json => FromJson(json),
        _ => null,
    };

    private static string FirstText(IList<ContentBlock>? content) =>
        content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

    private static string? FromJson(JsonElement json)
    {
        if (!json.TryGetProperty("isError", out var isError) || isError.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        if (!json.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty; // a serialized McpToolResult
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text"
                    && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
