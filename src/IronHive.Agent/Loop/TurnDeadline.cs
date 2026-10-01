namespace IronHive.Agent.Loop;

/// <summary>The cancellation token of one turn: the caller's, plus <see cref="AgentOptions.MaxTurnDuration"/>.</summary>
internal sealed class TurnDeadline : IDisposable
{
    private readonly CancellationTokenSource? _source;
    private readonly CancellationToken _caller;
    private readonly TimeSpan _limit;

    private TurnDeadline(TimeSpan? limit, CancellationToken caller)
    {
        _caller = caller;
        _limit = limit ?? Timeout.InfiniteTimeSpan;
        if (limit is { } value)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _source.CancelAfter(value);
        }
    }

    public CancellationToken Token => _source?.Token ?? _caller;

    public static TurnDeadline Start(TimeSpan? limit, CancellationToken caller)
    {
        if (limit is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), value, "MaxTurnDuration must be positive.");
        }

        return new TurnDeadline(limit, caller);
    }

    /// <summary>True when the turn was cancelled by this deadline passing, not by the caller.</summary>
    public bool Expired(OperationCanceledException exception) =>
        exception is not null && _source is { IsCancellationRequested: true } && !_caller.IsCancellationRequested;

    public TimeoutException TimeoutException() =>
        new($"The turn did not finish within {_limit.TotalSeconds:0.###} s (AgentOptions.MaxTurnDuration) and was cancelled.");

    public void Dispose() => _source?.Dispose();
}
