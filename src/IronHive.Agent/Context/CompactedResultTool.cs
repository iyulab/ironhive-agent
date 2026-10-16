using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// A tool that gives the model back a tool result its context manager masked or shortened, by the id the placeholder
/// names — instead of calling the original tool again, which a tool with side effects (a write, a send) cannot be, and a
/// reading tool may answer differently by then.
/// </summary>
/// <remarks>
/// <para>Off until registered: add the tool to the loop's <c>Tools</c>. From then on the manager keeps the original of
/// every result it masks or shortens, and its placeholders name this tool and the id instead of asking for the same call
/// again. A manager without the tool keeps nothing and its placeholders are unchanged.</para>
/// <para>The tool only reads what the session already produced. Approval rules see it as read-only (like
/// <c>advisor</c>), so planning mode and a permission policy allow it.</para>
/// <para>A read returns at most the manager's tool-result size limit (<see cref="CompactionConfig.MaxToolResultChars"/>,
/// 30 000 characters by default); a longer original is read in parts with <c>offset</c>.</para>
/// </remarks>
public static class CompactedResultTool
{
    /// <summary>The tool's name, which the placeholders of a manager with the tool name.</summary>
    public const string ToolName = "retrieve_tool_result";

    internal const string Description =
        "Read again a tool result that was masked or shortened to save context. Pass the id the placeholder names. " +
        "Use this instead of repeating the call — the original call may have side effects or answer differently now. " +
        "A long result is returned in parts: pass the offset the previous part names to continue.";

    /// <summary>Creates the tool and turns on retention in <paramref name="contextManager"/>.</summary>
    /// <param name="contextManager">The context manager of the loop the tool is registered on.</param>
    public static AIFunction Create(ContextManager contextManager)
    {
        ArgumentNullException.ThrowIfNull(contextManager);
        var archive = contextManager.EnableResultRetention(ToolName);

        string Retrieve(
            [Description("The id the placeholder names.")] string id,
            [Description("Character position to read from; 0 (the default) reads from the start.")] int offset = 0)
            => Read(archive, id, offset);

        return AIFunctionFactory.Create(Retrieve, new AIFunctionFactoryOptions { Name = ToolName, Description = Description });
    }

    internal static string Read(ToolResultArchive archive, string id, int offset)
    {
        if (string.IsNullOrWhiteSpace(id) || !archive.TryGet(id.Trim(), out var original))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"No kept result has the id \"{id}\". The ids are the ones named in the placeholders of masked or shortened results ({archive.Count} kept).");
        }

        if (offset < 0 || offset >= Math.Max(original.Length, 1))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The offset {offset} is outside the result \"{id}\", which has {original.Length:N0} characters.");
        }

        var length = Math.Min(archive.MaxChars, original.Length - offset);
        var part = original.Substring(offset, length);
        var end = offset + length;
        if (end >= original.Length && offset == 0)
        {
            return part;
        }

        var footer = end < original.Length
            ? string.Create(CultureInfo.InvariantCulture,
                $"\n\n[Characters {offset:N0}–{end:N0} of {original.Length:N0}. Call {archive.ToolName} with id \"{id}\" and offset {end} for the rest.]")
            : string.Create(CultureInfo.InvariantCulture,
                $"\n\n[Characters {offset:N0}–{end:N0} of {original.Length:N0} — the end of the result.]");
        return string.Concat(part, footer);
    }
}
