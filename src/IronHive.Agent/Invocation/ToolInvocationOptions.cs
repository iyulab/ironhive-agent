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
    /// How many times in a row the same tool may run successfully with identical arguments. The next identical call is
    /// not run; the model reads that it already has the result and should change course. 0 turns the guard off.
    /// Default: 3.
    /// </summary>
    public int MaxRepeatedCalls { get; set; } = 3;

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
    /// Recognises a failure a tool reports as its result instead of throwing: returns the error text when the result is
    /// a failure, or null when it is not. The loop guards count such a result as a failure keyed by this text — the
    /// repeated-error guard ends the request on the same text <see cref="MaxRepeatedErrors"/> times in a row, and the
    /// repeated-call guard does not count it as a successful run. Asked first; when it returns null the built-in
    /// recognition still applies (an MCP result with <c>isError: true</c>, keyed by its first text content). It receives
    /// the result as the tool returned it, except that a JSON string — how a function made by <c>AIFunctionFactory</c>
    /// returns a <c>string</c> — arrives as that <c>string</c>. Null by default.
    /// </summary>
    public Func<object?, string?>? FailureOf { get; set; }
}
