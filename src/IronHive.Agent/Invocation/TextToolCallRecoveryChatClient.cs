using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ToolCallParser;

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
/// <b>When it acts:</b> only when the request lists tools, its <see cref="ChatOptions.ToolMode"/> allows a call (anything
/// but <see cref="ChatToolMode.None"/>), the response holds no structured call, and every block it finds is complete and
/// names a tool the request offered. The reply is then the text before the first block followed by the calls; anything
/// after the blocks is dropped. A block that names an unknown tool or does not parse passes through untouched — its text
/// may be the model talking about a tool.
/// </para>
/// <para>
/// <b>Markup that cannot become a call</b> is dropped instead of reaching the reader as answer text, keeping the text before
/// the first block, and the response says why in <see cref="ChatResponse.AdditionalProperties"/> under
/// <see cref="DroppedCallMarkupKey"/> (on the last update when streaming): <see cref="CallsNotAllowed"/> — the request
/// listed tools under <see cref="ChatToolMode.None"/> and the model wrote a call to one anyway; <see cref="Unterminated"/> —
/// the response ends inside a block (the server stopped mid-call). A host reads it to say the model tried to keep working
/// instead of answering. A request that lists no tools is not touched.
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
/// <para>
/// The markup is read by <see cref="ToolCallMarkup"/>; this client decides which blocks become calls and types their
/// arguments against the offered tools.
/// </para>
/// </remarks>
public sealed class TextToolCallRecoveryChatClient : DelegatingChatClient
{
    /// <summary>
    /// The <see cref="ChatResponse.AdditionalProperties"/> key (on the last <see cref="ChatResponseUpdate"/> when streaming)
    /// set when call markup was dropped from the text; its value is <see cref="CallsNotAllowed"/> or <see cref="Unterminated"/>.
    /// </summary>
    public const string DroppedCallMarkupKey = "ironhive.invocation.dropped_call_markup";

    /// <summary>The model wrote a call while the request's <see cref="ChatOptions.ToolMode"/> was <see cref="ChatToolMode.None"/>.</summary>
    public const string CallsNotAllowed = "calls_not_allowed";

    /// <summary>The response ends inside a call block, so it can never become a call.</summary>
    public const string Unterminated = "unterminated";

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
        if (last.Role != ChatRole.Assistant || last.Contents.Any(c => c is FunctionCallContent))
        {
            return response;
        }

        var callsAllowed = CallsAllowed(options);
        if (!callsAllowed || Recover(last.Text, tools) is not { } recovered)
        {
            if (Drop(last.Text, tools, callsAllowed) is { } dropped)
            {
                response.Messages[^1] = Rewritten(last, dropped.Prefix, []);
                (response.AdditionalProperties ??= [])[DroppedCallMarkupKey] = dropped.Reason;
            }

            return response;
        }

        response.Messages[^1] = Rewritten(last, recovered.Prefix, recovered.Calls);
        response.FinishReason = ChatFinishReason.ToolCalls;
        return response;
    }

    /// <summary><paramref name="message"/> with its text replaced by <paramref name="prefix"/> followed by <paramref name="calls"/>.</summary>
    private static ChatMessage Rewritten(ChatMessage message, string prefix, IReadOnlyList<FunctionCallContent> calls)
    {
        var contents = new List<AIContent>();
        if (prefix.Length > 0)
        {
            contents.Add(new TextContent(prefix));
        }

        contents.AddRange(calls);
        contents.AddRange(message.Contents.Where(c => c is not TextContent));
        return new ChatMessage(ChatRole.Assistant, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tools = OfferedTools(options);
        var callsAllowed = CallsAllowed(options);
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
            switch (ToolCallMarkup.Scan(heldText.ToString()))
            {
                case ToolCallMarkupScan.Found when !sawStructuredCall:
                    holdingToEnd = true;
                    break;
                case ToolCallMarkupScan.Partial when !sawStructuredCall:
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

        if (holdingToEnd && !sawStructuredCall && tools is not null && callsAllowed && Recover(heldText.ToString(), tools) is { } recovered)
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

        if (holdingToEnd && !sawStructuredCall && tools is not null && Drop(heldText.ToString(), tools, callsAllowed) is { } dropped)
        {
            var first = held[0];
            var contents = new List<AIContent>();
            if (dropped.Prefix.Length > 0)
            {
                contents.Add(new TextContent(dropped.Prefix));
            }

            contents.AddRange(held.SelectMany(u => u.Contents).Where(c => c is not TextContent));
            var properties = new AdditionalPropertiesDictionary();
            foreach (var property in held.Where(u => u.AdditionalProperties is not null).SelectMany(u => u.AdditionalProperties!))
            {
                properties[property.Key] = property.Value;
            }

            properties[DroppedCallMarkupKey] = dropped.Reason;
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                ResponseId = first.ResponseId,
                MessageId = first.MessageId,
                ModelId = held.Select(u => u.ModelId).LastOrDefault(m => m is not null),
                FinishReason = held.Select(u => u.FinishReason).LastOrDefault(r => r is not null),
                AdditionalProperties = properties,
            };
            yield break;
        }

        foreach (var pending in held)
        {
            yield return pending;
        }
    }

    private static bool CallsAllowed(ChatOptions? options) => options?.ToolMode is not NoneChatToolMode;

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

    private sealed record Recovered(string Prefix, IReadOnlyList<FunctionCallContent> Calls);

    /// <summary>The calls written in <paramref name="text"/>, or <c>null</c> unless every block is complete and offered.</summary>
    private static Recovered? Recover(string text, Dictionary<string, AITool> tools) =>
        Recover(ToolCallMarkup.Read(text), tools);

    private static Recovered? Recover(ToolCallMarkupText read, Dictionary<string, AITool> tools)
    {
        if (read.Blocks.Count == 0)
        {
            return null;
        }

        var calls = new List<FunctionCallContent>(read.Blocks.Count);
        foreach (var block in read.Blocks)
        {
            if (block.Name is not { } name || !tools.TryGetValue(name, out var tool))
            {
                return null;
            }

            calls.Add(new FunctionCallContent(NewCallId(), tool.Name, Arguments(block, tool)));
        }

        return new Recovered(read.Prefix, calls);
    }

    private sealed record Dropped(string Prefix, string Reason);

    /// <summary>
    /// The text to keep when <paramref name="text"/> holds call markup that cannot become a call — a call written while calls
    /// are not allowed, or a block the response ends inside — or <c>null</c> to leave the text as it is.
    /// </summary>
    private static Dropped? Drop(string text, Dictionary<string, AITool> tools, bool callsAllowed)
    {
        var read = ToolCallMarkup.Read(text);
        if (!callsAllowed && Recover(read, tools) is { } forbidden)
        {
            return new Dropped(forbidden.Prefix, CallsNotAllowed);
        }

        if (read.Blocks.Count > 0)
        {
            return null; // a complete block that is not an offered call: possibly the model talking about a tool
        }

        return read.UnterminatedAt is { } start ? new Dropped(text[..start].TrimEnd(), Unterminated) : null;
    }

    private static Dictionary<string, object?> Arguments(ToolCallMarkupBlock block, AITool tool)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (block.Arguments is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return arguments;
        }

        foreach (var (name, value) in block.Parameters)
        {
            arguments[name] = Coerce(value, ParameterType(tool, name));
        }

        return arguments;
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

    private static string NewCallId() => "text_call_" + Guid.NewGuid().ToString("N")[..12];
}
