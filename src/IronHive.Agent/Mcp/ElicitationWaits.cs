using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace IronHive.Agent.Mcp;

/// <summary>
/// The waits a client has open on its server's URL-mode elicitations, so that the server's own «this step is done»
/// (<c>notifications/elicitation/complete</c>) can end one — the person finished the step somewhere the host never heard of.
/// </summary>
/// <remarks>
/// A wait is keyed by <see cref="ElicitRequestParams.ElicitationId"/>. The completion cancels the token the handler was given
/// and the wait ends as <c>accept</c>: the MCP client then sends the call again with the server's <c>requestState</c>, and the
/// server judges — <c>accept</c> here means «ask again», not «approved». A completion that arrives before its wait starts is
/// remembered (a small bounded set) and answers the wait at once. A request without an id cannot be correlated and is passed
/// through untouched.
/// </remarks>
internal sealed class ElicitationWaits
{
    private const int RememberedCompletions = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Wait> _waiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedOrder = new();

    private sealed class Wait(CancellationTokenSource source)
    {
        public CancellationTokenSource Source { get; } = source;

        public bool Completed { get; set; }
    }

    /// <summary>
    /// Runs <paramref name="ask"/> — the person-facing wait — until it answers, the caller cancels, or the server reports the
    /// elicitation complete (then <c>accept</c>, whatever the wait returned or threw).
    /// </summary>
    public async ValueTask<ElicitResult> RunAsync(
        ElicitRequestParams request, Func<CancellationToken, ValueTask<ElicitResult>> ask, CancellationToken cancellationToken)
    {
        if (request.ElicitationId is not { Length: > 0 } id)
        {
            return await ask(cancellationToken);
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wait = new Wait(source);
        lock (_gate)
        {
            if (_completed.Remove(id))
            {
                return Accepted();
            }

            _waiting[id] = wait;
        }

        try
        {
            var result = await ask(source.Token);
            return IsServerRelease(wait, cancellationToken) ? Accepted() : result;
        }
        catch (OperationCanceledException) when (IsServerRelease(wait, cancellationToken))
        {
            return Accepted();
        }
        finally
        {
            lock (_gate)
            {
                if (_waiting.TryGetValue(id, out var current) && ReferenceEquals(current, wait))
                {
                    _waiting.Remove(id);
                }
            }
        }
    }

    /// <summary>Ends the wait the notification names, or remembers it when the wait has not started.</summary>
    public void Complete(JsonRpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification.Params is not JsonObject { } parameters
            || parameters["elicitationId"] is not JsonValue value
            || !value.TryGetValue<string>(out var id)
            || id.Length == 0)
        {
            return;
        }

        Wait? wait;
        lock (_gate)
        {
            if (!_waiting.TryGetValue(id, out wait))
            {
                Remember(id);
                return;
            }

            wait.Completed = true;
        }

        try
        {
            wait.Source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait ended on its own between the lookup and the cancel.
        }
    }

    private static bool IsServerRelease(Wait wait, CancellationToken cancellationToken) =>
        wait.Completed && !cancellationToken.IsCancellationRequested;

    private static ElicitResult Accepted() => new() { Action = "accept" };

    private void Remember(string id)
    {
        if (!_completed.Add(id))
        {
            return;
        }

        _completedOrder.Enqueue(id);
        while (_completedOrder.Count > RememberedCompletions)
        {
            _completed.Remove(_completedOrder.Dequeue());
        }
    }
}
