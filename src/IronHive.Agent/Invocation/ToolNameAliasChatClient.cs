using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using IronHive.Extensions.AI;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Offers the model a provider-safe name for a tool whose own name the provider would refuse, and maps the model's call
/// back to the tool's real name — so a tool called <c>message.send</c> can be used at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> function-calling providers (OpenAI and the many OpenAI-compatible endpoints, Anthropic) accept
/// <c>[a-zA-Z0-9_-]{1,64}</c> as a function name and answer HTTP 400 to anything else. A tool catalog the app does not
/// write — an MCP server's — can hold a name the pattern rejects (<c>message.send</c>, <c>fs/read</c>); one such tool in
/// the request makes <b>every</b> model request that offers it fail, not just the calls to it.
/// </para>
/// <para>
/// <b>What it does:</b> a name that already matches the pattern passes through untouched — a request whose names are all
/// safe is forwarded as it is, the same messages and options objects. Any other name gets an alias: its unsafe characters
/// become <c>_</c>, and a short hash of the real name is appended (<c>message.send</c> → <c>message_send_</c> and ten hex
/// characters), so the alias is a pure function of the name, stable from one request to the next, and two real names share
/// one only by a 40-bit hash collision. The alias replaces the name in the request's tool list, in a required-tool choice,
/// and in the earlier calls of the history; a call the model makes by alias comes back under the real name (a whole call,
/// and the name on a streamed call's first argument fragment). Everything above this client — the function-invoking loop,
/// the approval gate, the events a host emits — sees the real name only; what sits <i>below</i> it (per-round context
/// compaction, token budget, retry, telemetry) sees the alias, so a rule there keyed on a tool's real name
/// (<c>CompactionConfig.ProtectedToolOutputs</c>) does not match an aliased tool.
/// </para>
/// <para>
/// <b>Failure:</b> if two different tools of a request would share an alias (a hash collision, or a tool whose real name is
/// another tool's alias) the request is not sent and <see cref="InvalidOperationException"/> names both, instead of
/// the provider's «invalid function name» answer. A name that is only in the history is rewritten and never raises it.
/// </para>
/// <para>
/// <b>Placement:</b> directly inside the function-invoking client and above <see cref="TextToolCallRecoveryChatClient"/>, so
/// the text-recovery step reads the same names the model was shown:
/// <c>builder.UseToolInvocationPipeline(…).UseToolNameAlias().UseTextToolCallRecovery()</c>.
/// </para>
/// </remarks>
public sealed class ToolNameAliasChatClient : DelegatingChatClient
{
    private const int MaxNameLength = 64;
    private const int HashLength = 10;

    // A message of the history rewritten once and reused: the alias is a pure function of the name, and a client below that
    // recognises messages by reference (per-round compaction does) must see the same instance on every round.
    private readonly ConditionalWeakTable<ChatMessage, ChatMessage> _rewritten = [];

    /// <summary>Wraps <paramref name="innerClient"/>.</summary>
    public ToolNameAliasChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    /// <summary>
    /// The name the model is shown for the tool whose own name is <paramref name="name"/>: the name itself when the provider
    /// accepts it, otherwise a stable alias (see the class remarks).
    /// </summary>
    /// <param name="name">The tool's real name.</param>
    public static string AliasFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (IsProviderSafe(name))
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashLength];
        var readable = new StringBuilder(MaxNameLength);
        foreach (var c in name)
        {
            if (readable.Length == MaxNameLength - HashLength - 1)
            {
                break;
            }

            readable.Append(IsSafeChar(c) ? c : '_');
        }

        if (readable.Length == 0)
        {
            readable.Append("tool");
        }

        return $"{readable}_{hash}";
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var map = NameMap.For(history, options);
        if (map is null)
        {
            return await base.GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);
        }

        var response = await base.GetResponseAsync(Outbound(history), map.Outbound(options), cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            map.Inbound(message.Contents);
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var map = NameMap.For(history, options);
        if (map is null)
        {
            await foreach (var passthrough in base.GetStreamingResponseAsync(history, options, cancellationToken).ConfigureAwait(false))
            {
                yield return passthrough;
            }

            yield break;
        }

        await foreach (var update in base.GetStreamingResponseAsync(Outbound(history), map.Outbound(options), cancellationToken).ConfigureAwait(false))
        {
            map.Inbound(update.Contents);
            yield return update;
        }
    }

    /// <summary>The history with every call of an unsafe name under its alias; a message that needs no change is passed as it is.</summary>
    private List<ChatMessage> Outbound(IList<ChatMessage> messages) =>
        messages.Select(message => message.Contents.Any(c => c is FunctionCallContent call && !IsProviderSafe(call.Name))
            ? _rewritten.GetValue(message, Rewritten)
            : message).ToList();

    private static ChatMessage Rewritten(ChatMessage message)
    {
        var contents = message.Contents
            .Select(c => c is FunctionCallContent call && !IsProviderSafe(call.Name) ? NameMap.Renamed(call, AliasFor(call.Name)) : c)
            .ToList();
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt,
            RawRepresentation = message.RawRepresentation,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    private static bool IsSafeChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';

    private static bool IsProviderSafe(string name) => name.Length is >= 1 and <= MaxNameLength && name.All(IsSafeChar);

    /// <summary>The real name ↔ alias pairs of one request; exists only when some name needs an alias.</summary>
    private sealed class NameMap
    {
        private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _real = new(StringComparer.Ordinal);

        /// <summary>The map for this request, or null when every name in it is safe (the request is then forwarded as it is).</summary>
        public static NameMap? For(IList<ChatMessage> messages, ChatOptions? options)
        {
            var names = new List<string>();
            if (options?.Tools is { } tools)
            {
                names.AddRange(tools.OfType<AIFunctionDeclaration>().Select(t => t.Name));
            }

            if (options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } required })
            {
                names.Add(required);
            }

            var historyNeedsAlias = messages.Any(m => m.Contents.Any(c => c is FunctionCallContent call && !IsProviderSafe(call.Name)));
            if (!historyNeedsAlias && names.All(IsProviderSafe))
            {
                return null;
            }

            // Only what the request offers (and requires) can come back from the model, so only those names are mapped;
            // earlier calls in the history are rewritten by their alias directly and cannot collide with anything here.
            var map = new NameMap();
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                map.Add(name);
            }

            return map;
        }

        private void Add(string name)
        {
            var alias = AliasFor(name);
            if (_real.TryGetValue(alias, out var other) && !string.Equals(other, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Tool names '{other}' and '{name}' would both be offered to the model as '{alias}'. " +
                    "Rename one of the tools; the request was not sent.");
            }

            _alias[name] = alias;
            _real[alias] = name;
        }

        private string Alias(string name) => _alias.TryGetValue(name, out var alias) ? alias : AliasFor(name);

        public ChatOptions? Outbound(ChatOptions? options)
        {
            if (options is null)
            {
                return null;
            }

            var copy = options.Clone();
            if (options.Tools is { } tools)
            {
                copy.Tools = tools
                    .Select(t => t is AIFunctionDeclaration declaration && Alias(declaration.Name) != declaration.Name
                        ? new AliasedDeclaration(declaration, Alias(declaration.Name))
                        : t)
                    .ToList();
            }

            if (options.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } required })
            {
                copy.ToolMode = ChatToolMode.RequireSpecific(Alias(required));
            }

            return copy;
        }

        /// <summary>Puts the real name back on every call the model made by alias (the contents are edited in place).</summary>
        public void Inbound(IList<AIContent> contents)
        {
            for (var i = 0; i < contents.Count; i++)
            {
                if (contents[i] is FunctionCallContent call && _real.TryGetValue(call.Name, out var real) && real != call.Name)
                {
                    contents[i] = Renamed(call, real);
                }
                else if (contents[i] is FunctionCallDeltaContent { Name: { } deltaName } delta
                    && _real.TryGetValue(deltaName, out var deltaReal) && deltaReal != deltaName)
                {
                    contents[i] = new FunctionCallDeltaContent(delta.CallId, delta.ArgumentsFragment, deltaReal)
                    {
                        RawRepresentation = delta.RawRepresentation,
                        AdditionalProperties = delta.AdditionalProperties,
                    };
                }
            }
        }

        public static FunctionCallContent Renamed(FunctionCallContent call, string name) =>
            new(call.CallId, name, call.Arguments)
            {
                Exception = call.Exception,
                InformationalOnly = call.InformationalOnly,
                RawRepresentation = call.RawRepresentation,
                AdditionalProperties = call.AdditionalProperties,
                Annotations = call.Annotations,
            };
    }

    /// <summary>A function declaration offered under another name; the provider reads its name, description and schemas.</summary>
    private sealed class AliasedDeclaration(AIFunctionDeclaration original, string name) : AIFunctionDeclaration
    {
        public override string Name { get; } = name;

        public override string Description => original.Description;

        public override System.Text.Json.JsonElement JsonSchema => original.JsonSchema;

        public override System.Text.Json.JsonElement? ReturnJsonSchema => original.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => original.AdditionalProperties;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : original.GetService(serviceType, serviceKey);

        public override string ToString() => original.ToString();
    }
}
