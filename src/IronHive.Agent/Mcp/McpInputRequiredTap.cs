using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// A pass-through <see cref="IClientTransport"/> that keeps what the SDK's own retry loop keeps to itself: the
/// <c>requestState</c> and the input requests of every <c>input_required</c> result a server answers a call with.
/// </summary>
/// <remarks>
/// The SDK resolves such a result in-process and sends the call again; the elicitation handler sees the request for a
/// person but never the state, so a handler that wants to release the call (to ask the person later, in another turn) has
/// nothing to persist. This tap reads the raw responses on their way to the SDK and forwards them unchanged.
/// <para>
/// Temporary: <c>TODO(upstream: modelcontextprotocol/csharp-sdk#1985)</c> - removed when the SDK hands the state to the handler or returns the
/// result on request.
/// </para>
/// </remarks>
internal sealed class McpInputRequiredTap(IClientTransport inner) : IClientTransport
{
    private readonly ConcurrentQueue<CapturedInputRequired> _captured = new();

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <summary>The <c>input_required</c> results seen so far, oldest first.</summary>
    public IReadOnlyCollection<CapturedInputRequired> Captured => _captured;

    /// <summary>
    /// The newest captured result that asked for <paramref name="request"/> (matched by the elicitation id, else the URL, else
    /// the message), or <c>null</c> when none did.
    /// </summary>
    public CapturedInputRequired? Find(ElicitRequestParams request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _captured.Reverse().FirstOrDefault(c => c.InputRequests.Values.Any(i => Matches(i, request)));
    }

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Tapped(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), _captured);

    private static bool Matches(InputRequest candidate, ElicitRequestParams request)
    {
        var asked = candidate.ElicitationParams;
        if (asked is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(request.ElicitationId))
        {
            return string.Equals(asked.ElicitationId, request.ElicitationId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(request.Url))
        {
            return string.Equals(asked.Url, request.Url, StringComparison.Ordinal);
        }

        return string.Equals(asked.Message, request.Message, StringComparison.Ordinal);
    }

    private sealed class Tapped : ITransport
    {
        private readonly ITransport _inner;
        private readonly ConcurrentQueue<CapturedInputRequired> _captured;
        private readonly Channel<JsonRpcMessage> _out = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly Task _pump;

        public Tapped(ITransport inner, ConcurrentQueue<CapturedInputRequired> captured)
        {
            _inner = inner;
            _captured = captured;
            _pump = Task.Run(PumpAsync);
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _out.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            _inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _pump.ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var message in _inner.MessageReader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (message is JsonRpcResponse response && TryRead(response, out var captured))
                    {
                        _captured.Enqueue(captured);
                    }

                    await _out.Writer.WriteAsync(message).ConfigureAwait(false);
                }

                _out.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _out.Writer.TryComplete(ex);
            }
        }

        private static bool TryRead(JsonRpcResponse response, out CapturedInputRequired captured)
        {
            captured = null!;
            if (response.Result is not JsonObject result
                || !result.TryGetPropertyValue("resultType", out var type)
                || type?.GetValue<string>() != "input_required")
            {
                return false;
            }

            var parsed = result.Deserialize<InputRequiredResult>(McpJsonUtilities.DefaultOptions);
            if (parsed is null)
            {
                return false;
            }

            captured = new CapturedInputRequired(
                response.Id, parsed.RequestState, parsed.InputRequests ?? new Dictionary<string, InputRequest>());
            return true;
        }
    }
}

/// <summary>An <c>input_required</c> result as the server sent it.</summary>
/// <param name="RequestId">The JSON-RPC id of the call it answered.</param>
/// <param name="RequestState">The server's opaque state, to be sent back with the answers (may be absent).</param>
/// <param name="InputRequests">What the server asked for, by the key the answer must carry.</param>
internal sealed record CapturedInputRequired(
    RequestId RequestId, string? RequestState, IDictionary<string, InputRequest> InputRequests);
