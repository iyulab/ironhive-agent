namespace IronHive.Agent.Invocation;

/// <summary>
/// Thresholds of the loop guards a tool invocation pipeline carries by default
/// (<see cref="ToolInvocationPipeline.CreateDefault"/>, and the container's pipeline after <c>AddIronHiveAgent</c>).
/// Set it through <c>AddIronHiveAgent(o =&gt; o.ToolInvocation = ...)</c>.
/// </summary>
public sealed class ToolInvocationOptions
{
    /// <summary>
    /// Refuse a call whose arguments the model's response could not be parsed into (the call content carries the parse
    /// error), instead of running the tool on partial arguments. The model reads the parse error and can re-issue the
    /// call. Default: true.
    /// </summary>
    public bool RefuseUnparseableArguments { get; set; } = true;

    /// <summary>
    /// How many times in a row the same tool may run successfully with identical arguments — or with the same target, for
    /// a tool that declares target arguments (<see cref="ToolInvocationHints.WithTargetArguments"/>), whatever its other
    /// arguments. The next such call is not run; the model reads that it already has the result and should change course.
    /// 0 turns the guard off. Default: 3.
    /// </summary>
    public int MaxRepeatedCalls { get; set; } = 3;

    /// <summary>
    /// How many times in a row the repeated-call guard refuses the same call before it ends the request
    /// (<c>FunctionInvocationContext.Terminate</c>; the turn ends as <c>TurnStopReason.ToolTerminated</c> on a
    /// <see cref="IronHive.Agent.Mode.ToolCallRefusal"/> of kind <c>RepeatedCall</c>). A model that keeps re-issuing a
    /// refused call is stuck; without this it spends the rest of the step budget on refusals and the turn ends as a step
    /// limit, which reads as "the task was too big". 0 keeps refusing without ending the request. Default: 2.
    /// </summary>
    public int MaxRefusedRepeats { get; set; } = 2;

    /// <summary>
    /// How many times in a row the same tool may fail with the same error before the request is ended
    /// (<c>FunctionInvocationContext.Terminate</c>) instead of letting the model retry again. 0 turns the guard off.
    /// Default: 3.
    /// </summary>
    /// <remarks>
    /// A function-invoking client also stops on its own after <c>MaximumConsecutiveErrorsPerRequest</c> (default 3)
    /// consecutive failing rounds, by throwing. A value above that limit plus one is never reached unless the client's
    /// limit is raised too (through the <c>configure</c> callback of <c>UseToolInvocationPipeline</c>).
    /// </remarks>
    public int MaxRepeatedErrors { get; set; } = 3;

    /// <summary>
    /// On how many separate visits the same tool may return the same result to identical arguments (or to the same target,
    /// for a tool that declares target arguments) before the request is
    /// ended (<c>FunctionInvocationContext.Terminate</c>; the turn ends as <c>TurnStopReason.ToolTerminated</c> on a
    /// <see cref="IronHive.Agent.Mode.ToolCallRefusal"/> of kind <c>RepeatedResult</c>). A visit is a run of identical
    /// calls with other calls before it; consecutive identical calls are the repeated-call guard's. A model that keeps
    /// coming back for content it already has is usually re-reading what masking pushed out — the working set does not
    /// fit <c>CompactionConfig.ObservationMaskingProtectedTokens</c> — and once it stops re-reading it writes from memory.
    /// A re-read whose result changed does not count. 0 turns the guard off. Default: 3.
    /// </summary>
    public int MaxRepeatedResults { get; set; } = 3;

    /// <summary>
    /// Recognises a failure a tool reports as its result instead of throwing: returns the error text when the result is
    /// a failure, or null when it is not. The loop guards count such a result as a failure keyed by this text — the
    /// repeated-error guard ends the request on the same text <see cref="MaxRepeatedErrors"/> times in a row, and the
    /// repeated-call guard does not count it as a successful run. Asked first; when it returns null the built-in
    /// recognition still applies (an MCP result with <c>isError: true</c>, keyed by its first text content). It receives
    /// the result as the tool returned it, except that a JSON string — how a function made by <c>AIFunctionFactory</c>
    /// returns a <c>string</c> — arrives as that <c>string</c>. Null by default.
    /// </summary>
    public Func<object?, string?>? FailureOf { get; set; }

    /// <summary>
    /// Longest one tool call may run. When it is exceeded the call's cancellation token is cancelled, and the model
    /// receives a <see cref="IronHive.Agent.Mode.ToolCallRefusal"/> of kind
    /// <see cref="IronHive.Agent.Mode.ToolCallRefusalKind.TimedOut"/> naming the tool and the limit, so it can narrow the
    /// request or stop. A tool that ignores its token is abandoned a few seconds later (it keeps running in the
    /// background, but the turn no longer waits for it). <see cref="MaxRepeatedErrors"/> counts timeouts like any other
    /// failure, so a model that repeats the same slow call is stopped. Only the tool's own run is timed — the steps around
    /// it (an approval gate waiting for a person among them) are not. A tool can carry its own limit
    /// (<see cref="ToolInvocationHints.WithMaxDuration"/>), which wins over this one. Null (the default): no limit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to zero or a negative value.</exception>
    public TimeSpan? MaxInvocationDuration
    {
        get => _maxInvocationDuration;
        set
        {
            if (value is { } limit && limit <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), limit, "MaxInvocationDuration must be positive; null means no limit.");
            }

            _maxInvocationDuration = value;
        }
    }

    private TimeSpan? _maxInvocationDuration;

    /// <summary>How long a timed-out call that ignores its cancellation token is still awaited before it is abandoned.</summary>
    internal TimeSpan AbandonGrace { get; init; } = TimeSpan.FromSeconds(5);
}
