using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// Makes a call to a tool the request did not offer reach the <see cref="ToolInvocationPipeline"/>. A function-invoking
/// client answers such a call itself («not found») without asking its function invoker, so the loop guards never count
/// it. The client looks a called name up in the request's tools and then in its <see cref="FunctionInvokingChatClient.AdditionalTools"/>
/// (tools it can call but does not advertise), enumerating that list on every lookup; so the pipeline client's additional
/// tools are a view (<see cref="ToolView"/>) that adds an <see cref="UnknownToolFunction"/> for each unknown name the model
/// called in this request, noted by a <see cref="Recorder"/> directly beneath the client.
/// </summary>
/// <remarks>
/// The names are kept per request in an ambient <see cref="Scope"/> — one client serves concurrent requests, and the view
/// cannot know which request is looking a name up. The pipeline client sets it before the base call (and, streaming,
/// before each step of the base stream, since an iterator's ambient values do not outlive one step).
/// </remarks>
internal static class UnknownToolCalls
{
    private static readonly AsyncLocal<Scope?> Ambient = new();

    /// <summary>The current request's scope, or null outside a pipeline client's request.</summary>
    public static Scope? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }

    /// <summary>The unknown names one request's model called, each with its stand-in.</summary>
    internal sealed class Scope
    {
        private readonly ConcurrentDictionary<string, UnknownToolFunction> _functions = new(StringComparer.Ordinal);

        public void Note(string name) => _functions.GetOrAdd(name, static n => new UnknownToolFunction(n));

        public IEnumerable<UnknownToolFunction> Functions => _functions.Values;
    }

    /// <summary>
    /// Beneath the function-invoking client: notes, in the current scope, every called name that is neither in the
    /// request's tools nor in the host's own additional tools.
    /// </summary>
    internal sealed class Recorder(IChatClient inner, Func<IList<AITool>?> hostTools) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            if (Current is { } scope)
            {
                foreach (var message in response.Messages)
                {
                    Note(scope, message.Contents, options);
                }
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (Current is { } scope)
                {
                    Note(scope, update.Contents, options);
                }

                yield return update;
            }
        }

        private void Note(Scope scope, IList<AIContent> contents, ChatOptions? options)
        {
            foreach (var content in contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.Name) && !IsKnown(call.Name, options))
                {
                    scope.Note(call.Name);
                }
            }
        }

        private bool IsKnown(string name, ChatOptions? options) =>
            Contains(options?.Tools, name) || Contains(hostTools(), name);

        private static bool Contains(IList<AITool>? tools, string name)
        {
            if (tools is null)
            {
                return false;
            }

            foreach (var tool in tools)
            {
                if (string.Equals(tool.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The function-invoking client's additional tools: the host's own list, then the current request's stand-ins.
    /// Changes go to the host's list.
    /// </summary>
    internal sealed class ToolView(IList<AITool>? host) : IList<AITool>
    {
        public IList<AITool> Host { get; } = host ?? [];

        private List<AITool> Snapshot() => [.. Host, .. Current?.Functions ?? []];

        public IEnumerator<AITool> GetEnumerator()
        {
            foreach (var tool in Host)
            {
                yield return tool;
            }

            if (Current is { } scope)
            {
                foreach (var function in scope.Functions)
                {
                    yield return function;
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => Host.Count + (Current?.Functions.Count() ?? 0);

        public bool IsReadOnly => Host.IsReadOnly;

        public AITool this[int index]
        {
            get => Snapshot()[index];
            set => Host[index] = value;
        }

        public void Add(AITool item) => Host.Add(item);

        public void Clear() => Host.Clear();

        public bool Contains(AITool item) => Snapshot().Contains(item);

        public void CopyTo(AITool[] array, int arrayIndex) => Snapshot().CopyTo(array, arrayIndex);

        public int IndexOf(AITool item) => Snapshot().IndexOf(item);

        public void Insert(int index, AITool item) => Host.Insert(index, item);

        public bool Remove(AITool item) => Host.Remove(item);

        public void RemoveAt(int index) => Host.RemoveAt(index);
    }
}
