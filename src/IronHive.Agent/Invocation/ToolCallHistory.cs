using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Reads the completed tool calls of a conversation — each call the model made, paired with the result that answered
/// it, in the order the calls were made. The loop guards derive their streaks from this instead of keeping state, so
/// one middleware instance serves any number of conversations.
/// </summary>
internal static class ToolCallHistory
{
    public static List<(FunctionCallContent Call, FunctionResultContent Result)> Completed(
        IEnumerable<ChatMessage>? messages, string? excludeCallId)
    {
        var completed = new List<(FunctionCallContent, FunctionResultContent)>();
        if (messages is null)
        {
            return completed;
        }

        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var results = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        foreach (var message in list)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                results[result.CallId] = result;
            }
        }

        foreach (var message in list)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!string.Equals(call.CallId, excludeCallId, StringComparison.Ordinal)
                    && results.TryGetValue(call.CallId, out var result))
                {
                    completed.Add((call, result));
                }
            }
        }

        return completed;
    }

    /// <summary>The result as the model sees it, as comparable text: a JSON string unwrapped, other JSON as written, anything else serialized.</summary>
    public static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
        JsonElement json => json.GetRawText(),
        _ => SerializeResult(result),
    };

    private static string SerializeResult(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// The one definition of "the same call" the loop guards share: a function that keys a call by its tool name and
    /// arguments. Calls of <paramref name="current"/>, when it declares target arguments
    /// (<see cref="ToolInvocationHints.GetTargetArguments"/>), are keyed by those arguments alone; every other call by all
    /// of its arguments. A call of another tool never shares a key with a call of <paramref name="current"/>.
    /// </summary>
    public static Func<string, IEnumerable<KeyValuePair<string, object?>>?, string> KeyFor(AITool current, out IReadOnlyList<string> targets)
    {
        var declared = ToolInvocationHints.GetTargetArguments(current);
        targets = declared;
        var name = current.Name;
        return declared.Count == 0
            ? Key
            : (callName, arguments) => string.Equals(callName, name, StringComparison.Ordinal)
                ? TargetKey(callName, arguments, declared)
                : Key(callName, arguments);
    }

    private static string TargetKey(string name, IEnumerable<KeyValuePair<string, object?>>? arguments, IReadOnlyList<string> targets)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments ?? [])
        {
            values[key] = value;
        }

        // A target the call omits keys as absent, distinct from an explicit null.
        var parts = targets
            .Order(StringComparer.Ordinal)
            .Select(t => t + (values.TryGetValue(t, out var value) ? "=" + Serialize(value) : "\u001e"));
        return name + "\u001d" + string.Join("\u001f", parts);
    }

    /// <summary>A key equal for two calls of the same tool with the same arguments, whatever their order.</summary>
    public static string Key(string name, IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return name;
        }

        var parts = arguments
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + Serialize(kv.Value));
        return name + "\u001f" + string.Join("\u001f", parts);
    }

    private static string Serialize(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
