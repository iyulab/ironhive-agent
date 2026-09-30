using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>The rest of a <see cref="ToolInvocationPipeline"/>: the next middleware, or the tool itself.</summary>
/// <param name="context">The call being made.</param>
/// <param name="cancellationToken">Cancels the call.</param>
/// <returns>The call's result, as the model will read it.</returns>
public delegate ValueTask<object?> ToolInvocationNext(FunctionInvocationContext context, CancellationToken cancellationToken);

/// <summary>
/// One step around every in-process tool call a <see cref="ToolInvocationPipeline"/> makes — a permission gate, a
/// loop guard, an observer. Steps run in registration order, the first registered outermost.
/// </summary>
/// <remarks>
/// <para>
/// To run the call, return <c>await next(context, cancellationToken)</c> (possibly after changing
/// <see cref="FunctionInvocationContext.Arguments"/>, or with the result replaced afterwards). To not run it, return a
/// result without calling <c>next</c> — a <see cref="Mode.ToolCallRefusal"/> is the conventional one: the model reads
/// its message and changes course, and a loop reports the call with <c>Success = false</c>. To end the request after
/// this call, set <see cref="FunctionInvocationContext.Terminate"/>; do not throw for that — an exception is reported
/// to the model as a failed call and the request goes on.
/// </para>
/// <para>
/// A middleware is shared by every conversation the pipeline serves, so keep it free of per-conversation state; what
/// happened earlier in the conversation is in <see cref="FunctionInvocationContext.Messages"/>.
/// </para>
/// </remarks>
public interface IToolInvocationMiddleware
{
    /// <summary>Handles one tool call.</summary>
    /// <param name="context">The call: function, arguments, the model's call content and the conversation so far.</param>
    /// <param name="next">Runs the rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<object?> InvokeAsync(FunctionInvocationContext context, ToolInvocationNext next, CancellationToken cancellationToken);
}

/// <summary>
/// A step over every tool result before the model reads it — results of calls the pipeline ran in-process, and results
/// a host supplies for tools it runs itself (declaration-only tools, appended before <c>IAgentLoop.ContinueAsync</c>).
/// Steps run in registration order; each receives the previous one's output.
/// </summary>
/// <remarks>
/// For an in-process call the result stage runs right after the tool returns, inside every
/// <see cref="IToolInvocationMiddleware"/>, so those see the processed result. A call a middleware refused never
/// reaches it, and once a step returns a <see cref="Mode.ToolCallRefusal"/> the later steps are skipped.
/// </remarks>
public interface IToolResultMiddleware
{
    /// <summary>Returns what the model receives for <see cref="ToolResultContext.Result"/>.</summary>
    /// <param name="context">The result and the call it answers.</param>
    /// <param name="cancellationToken">Cancels the inspection.</param>
    ValueTask<object?> OnResultAsync(ToolResultContext context, CancellationToken cancellationToken);
}

/// <summary>A tool result on its way to the model, as an <see cref="IToolResultMiddleware"/> sees it.</summary>
public sealed record ToolResultContext
{
    /// <summary>The id of the call the result answers.</summary>
    public required string CallId { get; init; }

    /// <summary>The tool that produced the result; empty when the result answers no call the conversation holds.</summary>
    public required string ToolName { get; init; }

    /// <summary>The arguments the call was made with, when known.</summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>The result as it stands after the previous steps.</summary>
    public object? Result { get; init; }

    /// <summary>The conversation the result belongs to.</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>
    /// True when a host ran the tool and supplied the result (a declaration-only tool); false when the pipeline ran it.
    /// </summary>
    public bool IsHostResult { get; init; }
}
