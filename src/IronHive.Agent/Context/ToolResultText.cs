using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// The text of a function result as the context machinery measures, masks and records it. A result that is content
/// (a list of <see cref="AIContent"/>, as an MCP tool returns when it carries an image) reads as its text parts with
/// each image named — <c>[image image/png, 12.3 KB]</c> — never as a type name and never as the bytes.
/// </summary>
public static class ToolResultText
{
    /// <summary>The result's text: a string as is, content as its text parts with images named, anything else via <c>ToString()</c>.</summary>
    public static string Of(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        AIContent content => Describe(content),
        IEnumerable<AIContent> contents => string.Join("\n", contents.Select(Describe)),
        _ => result.ToString() ?? string.Empty,
    };

    private static bool IsImage(DataContent data) => data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string Describe(AIContent content) => content switch
    {
        TextContent text => text.Text ?? string.Empty,
        DataContent data => $"[{(IsImage(data) ? "image" : "data")} {data.MediaType}, {Size(data.Data.Length)}]",
        _ => $"[{content.GetType().Name}]",
    };

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";
}
