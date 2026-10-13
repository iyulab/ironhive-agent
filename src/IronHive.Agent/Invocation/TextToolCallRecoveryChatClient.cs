using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Turns a tool call the model wrote as text into the call it meant, so the function-invoking client runs it instead of
/// ending the turn on a reply that only describes a call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> open models served behind an OpenAI-compatible endpoint emit their calls in their chat template's markup,
/// and the server's parser turns that markup into structured <c>tool_calls</c>. When the parser misses one, the call
/// arrives as plain assistant text — <c>&lt;tool_call&gt;{"name": …, "arguments": …}&lt;/tool_call&gt;</c> (Hermes, Qwen
/// 2.5) or <c>&lt;tool_call&gt;&lt;function=Name&gt;&lt;parameter=x&gt;…&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;</c>
/// (Qwen3-Coder) — and a loop sees a finished answer. The work the model meant to do is never done, and the turn reports
/// <c>completed</c>.
/// </para>
/// <para>
/// <b>When it acts:</b> only when the request offered tools, the response holds no structured call, and every block it
/// finds is complete and names a tool the request offered. The reply is then the text before the first block followed by
/// the calls; anything after the blocks is dropped. Otherwise the response passes through untouched — including a block
/// that names an unknown tool or does not parse.
/// </para>
/// <para>
/// <b>Arguments:</b> a JSON block's <c>arguments</c> (an object, or a string holding one) are used as given. A Qwen3-Coder
/// parameter's text is a string unless the tool's schema types that parameter otherwise and the text parses as JSON.
/// </para>
/// <para>
/// <b>Streaming:</b> text is passed on as it arrives until something that may start a block appears; from there on it is
/// held to the end of the response, then sent as the recovered calls or, if no call is recovered, as it was.
/// </para>
/// <para>
/// <b>Placement:</b> directly inside the function-invoking client, so the calls it produces are the ones that client runs:
/// <c>builder.UseToolInvocationPipeline(…).UseTextToolCallRecovery()</c>.
/// </para>
/// </remarks>
public sealed partial class TextToolCallRecoveryChatClient : DelegatingChatClient
{
    private static readonly string[] Markers = ["<tool_call>", "<function="];

    /// <summary>Wraps <paramref name="innerClient"/>.</summary>
    public TextToolCallRecoveryChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (OfferedTools(options) is not { } tools || response.Messages.Count == 0)
        {
            return response;
        }

        var last = response.Messages[^1];
        if (last.Role != ChatRole.Assistant || last.Contents.Any(c => c is FunctionCallContent)
            || Recover(last.Text, tools) is not { } recovered)
        {
            return response;
        }

        var contents = new List<AIContent>();
        if (recovered.Prefix.Length > 0)
        {
            contents.Add(new TextContent(recovered.Prefix));
        }

        contents.AddRange(recovered.Calls);
        contents.AddRange(last.Contents.Where(c => c is not TextContent));
        response.Messages[^1] = new ChatMessage(ChatRole.Assistant, contents)
        {
            AuthorName = last.AuthorName,
            MessageId = last.MessageId,
            AdditionalProperties = last.AdditionalProperties,
        };
        response.FinishReason = ChatFinishReason.ToolCalls;
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tools = OfferedTools(options);
        var held = new List<ChatResponseUpdate>();
        var heldText = new StringBuilder();
        var holdingToEnd = false;
        var sawStructuredCall = false;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (tools is null)
            {
                yield return update;
                continue;
            }

            sawStructuredCall |= update.Contents.Any(c => c is FunctionCallContent);
            if (holdingToEnd)
            {
                held.Add(update);
                heldText.Append(update.Text);
                continue;
            }

            held.Add(update);
            heldText.Append(update.Text);
            switch (Scan(heldText.ToString()))
            {
                case MarkerScan.Found when !sawStructuredCall:
                    holdingToEnd = true;
                    break;
                case MarkerScan.Partial when !sawStructuredCall:
                    break; // keep holding until the next update says whether a block starts here
                default:
                    foreach (var pending in held)
                    {
                        yield return pending;
                    }

                    held.Clear();
                    heldText.Clear();
                    break;
            }
        }

        if (held.Count == 0)
        {
            yield break;
        }

        if (holdingToEnd && !sawStructuredCall && tools is not null && Recover(heldText.ToString(), tools) is { } recovered)
        {
            var first = held[0];
            var contents = new List<AIContent>();
            if (recovered.Prefix.Length > 0)
            {
                contents.Add(new TextContent(recovered.Prefix));
            }

            contents.AddRange(recovered.Calls);
            contents.AddRange(held.SelectMany(u => u.Contents).Where(c => c is not TextContent));
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                ResponseId = first.ResponseId,
                MessageId = first.MessageId,
                ModelId = held.Select(u => u.ModelId).LastOrDefault(m => m is not null),
                FinishReason = ChatFinishReason.ToolCalls,
            };
            yield break;
        }

        foreach (var pending in held)
        {
            yield return pending;
        }
    }

    private static Dictionary<string, AITool>? OfferedTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools)
        {
            return null;
        }

        var byName = new Dictionary<string, AITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            byName.TryAdd(tool.Name, tool);
        }

        return byName;
    }

    private enum MarkerScan { None, Partial, Found }

    /// <summary>Whether <paramref name="text"/> holds a block marker, or ends with what may become one.</summary>
    private static MarkerScan Scan(string text)
    {
        foreach (var marker in Markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return MarkerScan.Found;
            }
        }

        var lastOpen = text.LastIndexOf('<');
        if (lastOpen < 0)
        {
            return MarkerScan.None;
        }

        var tail = text.AsSpan(lastOpen);
        foreach (var marker in Markers)
        {
            if (marker.AsSpan().StartsWith(tail, StringComparison.Ordinal))
            {
                return MarkerScan.Partial;
            }
        }

        return MarkerScan.None;
    }

    private sealed record Recovered(string Prefix, IReadOnlyList<FunctionCallContent> Calls);

    /// <summary>The calls written in <paramref name="text"/>, or <c>null</c> unless every block is complete and offered.</summary>
    private static Recovered? Recover(string text, Dictionary<string, AITool> tools)
    {
        var blocks = new List<(int Start, string Body)>();
        foreach (Match m in ToolCallBlock().Matches(text))
        {
            blocks.Add((m.Index, m.Groups["body"].Value));
        }

        if (blocks.Count == 0)
        {
            // Qwen3-Coder sometimes leaves out the <tool_call> wrapper.
            foreach (Match m in FunctionBlock().Matches(text))
            {
                blocks.Add((m.Index, m.Value));
            }
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        var calls = new List<FunctionCallContent>();
        foreach (var (_, body) in blocks)
        {
            if (ParseBlock(body.Trim(), tools) is not { } call)
            {
                return null;
            }

            calls.Add(call);
        }

        // A wrapper opened but never closed («<tool_call> <function=…>…</function>») is part of the call, not of the prose.
        var prefix = text[..blocks[0].Start].TrimEnd();
        if (prefix.EndsWith(Markers[0], StringComparison.Ordinal))
        {
            prefix = prefix[..^Markers[0].Length].TrimEnd();
        }

        return new Recovered(prefix, calls);
    }

    private static FunctionCallContent? ParseBlock(string body, Dictionary<string, AITool> tools)
    {
        if (body.StartsWith('{'))
        {
            return ParseJsonBlock(body, tools);
        }

        var function = FunctionBlock().Match(body);
        if (!function.Success || !tools.TryGetValue(function.Groups["name"].Value.Trim(), out var tool))
        {
            return null;
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (Match parameter in ParameterBlock().Matches(function.Groups["params"].Value))
        {
            var name = parameter.Groups["name"].Value.Trim();
            arguments[name] = Coerce(TrimOneNewline(parameter.Groups["value"].Value), ParameterType(tool, name));
        }

        return new FunctionCallContent(NewCallId(), tool.Name, arguments);
    }

    private static FunctionCallContent? ParseJsonBlock(string body, Dictionary<string, AITool> tools)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
                || !tools.TryGetValue(nameElement.GetString()!, out var tool))
            {
                return null;
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (root.TryGetProperty("arguments", out var args) || root.TryGetProperty("parameters", out args))
            {
                if (args.ValueKind == JsonValueKind.String)
                {
                    using var inner = JsonDocument.Parse(args.GetString()!);
                    if (inner.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    foreach (var property in inner.RootElement.EnumerateObject())
                    {
                        arguments[property.Name] = property.Value.Clone();
                    }
                }
                else if (args.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in args.EnumerateObject())
                    {
                        arguments[property.Name] = property.Value.Clone();
                    }
                }
                else if (args.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
            }

            return new FunctionCallContent(NewCallId(), tool.Name, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON Schema <c>type</c> of <paramref name="parameter"/>, when the tool declares one.</summary>
    private static string? ParameterType(AITool tool, string parameter)
    {
        if (tool is not AIFunctionDeclaration declaration
            || declaration.JsonSchema.ValueKind != JsonValueKind.Object
            || !declaration.JsonSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(parameter, out var schema)
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type))
        {
            return null;
        }

        return type.ValueKind == JsonValueKind.String ? type.GetString() : null;
    }

    private static object? Coerce(string value, string? schemaType)
    {
        if (schemaType is null or "string")
        {
            return value;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string TrimOneNewline(string value)
    {
        if (value.StartsWith("\r\n", StringComparison.Ordinal))
        {
            value = value[2..];
        }
        else if (value.StartsWith('\n'))
        {
            value = value[1..];
        }

        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            value = value[..^2];
        }
        else if (value.EndsWith('\n'))
        {
            value = value[..^1];
        }

        return value;
    }

    private static string NewCallId() => "text_call_" + Guid.NewGuid().ToString("N")[..12];

    [GeneratedRegex(@"<tool_call>(?<body>.*?)</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallBlock();

    [GeneratedRegex(@"<function=(?<name>[^>\n]+)>(?<params>.*?)</function>", RegexOptions.Singleline)]
    private static partial Regex FunctionBlock();

    [GeneratedRegex(@"<parameter=(?<name>[^>\n]+)>(?<value>.*?)</parameter>", RegexOptions.Singleline)]
    private static partial Regex ParameterBlock();
}
