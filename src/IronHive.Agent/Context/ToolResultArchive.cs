using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// The originals of the tool results a <see cref="ContextManager"/> masked or shortened, kept so the model can read them
/// again through <see cref="CompactedResultTool"/> instead of repeating the call. Filled at reduction time — on every
/// request, so a result masked in the middle of a turn is kept before it reaches the loop's history.
/// </summary>
internal sealed class ToolResultArchive
{
    /// <summary>Marks a shortened result with the id its original is kept under, so a later masking of it reuses the id.</summary>
    internal const string IdProperty = "ironhive.retained_result_id";

    private readonly ConcurrentDictionary<string, string> _originals = new(StringComparer.Ordinal);

    public ToolResultArchive(string toolName, int maxChars)
    {
        ToolName = toolName;
        MaxChars = maxChars;
    }

    /// <summary>The name of the tool that reads the archive — the placeholders tell the model to call it.</summary>
    public string ToolName { get; }

    /// <summary>The most characters one read returns; a longer original is read in parts.</summary>
    public int MaxChars { get; }

    public int Count => _originals.Count;

    /// <summary>
    /// Keeps <paramref name="text"/> as the original of <paramref name="result"/> and returns its id: the result's call id,
    /// the id an earlier shortening recorded on it, or else a hash of the text (the same text always gets the same id, so
    /// a placeholder stays the same from one request to the next). When the id is already kept, the longer text stays —
    /// a shortened copy never replaces the original.
    /// </summary>
    public string Retain(FunctionResultContent result, string text)
    {
        var id = IdOf(result, text);
        _originals.AddOrUpdate(id, text, (_, kept) => text.Length > kept.Length ? text : kept);
        return id;
    }

    public bool TryGet(string id, out string text) => _originals.TryGetValue(id, out text!);

    public void Clear() => _originals.Clear();

    private static string IdOf(FunctionResultContent result, string text)
    {
        if (result.AdditionalProperties?.TryGetValue(IdProperty, out var recorded) == true && recorded is string recordedId)
        {
            return recordedId;
        }

        if (!string.IsNullOrEmpty(result.CallId))
        {
            return result.CallId;
        }

        return "r-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
    }
}
