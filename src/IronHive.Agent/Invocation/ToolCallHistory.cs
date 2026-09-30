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
