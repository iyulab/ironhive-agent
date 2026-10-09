using IronHive.Agent.Mode;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Invocation;

/// <summary>
/// The ordered middleware every tool call and every tool result goes through, folded into the single
/// <see cref="FunctionInvokingChatClient.FunctionInvoker"/> delegate of a function-invoking client
/// (<see cref="ToolInvocationPipelineChatClientBuilderExtensions.UseToolInvocationPipeline(ChatClientBuilder, Action{FunctionInvokingChatClient}?)"/>).
/// The agent loops find the pipeline on their chat client and run its result stage over results a host supplies before
/// continuing; the Ironbees adapter runs its tool calls through it too — so one set of rules covers every path that
/// runs tools.
/// </summary>
/// <remarks>
/// Nothing is in the pipeline unless it is added: without an <see cref="ApprovalGateMiddleware"/> there is no
/// permission gate. <see cref="CreateDefault"/> is the set the container registers by default (the loop guards of
/// <see cref="ToolInvocationOptions"/>); DI adds the rest (<c>AddToolInvocationMiddleware</c>,
/// <c>AddToolResultMiddleware</c>, <c>AddIronHiveAgentApprovalGate</c>).
/// </remarks>
public sealed class ToolInvocationPipeline
{
    private readonly IToolResultMiddleware[] _results;
    private readonly ToolInvocationOptions _options;

    /// <summary>Folds <paramref name="invocationMiddleware"/> and <paramref name="resultMiddleware"/> into one pipeline.</summary>
    /// <param name="invocationMiddleware">Steps around each call, outermost first.</param>
    /// <param name="resultMiddleware">Steps over each result, in the order they run.</param>
    /// <param name="options">
    /// What the pipeline itself applies to the tool's run, innermost of every step:
    /// <see cref="ToolInvocationOptions.MaxInvocationDuration"/> (and a tool's own limit,
    /// <see cref="ToolInvocationHints.WithMaxDuration"/>). Defaults when null — no time limit unless a tool declares one.
    /// </param>
    public ToolInvocationPipeline(
        IEnumerable<IToolInvocationMiddleware> invocationMiddleware,
        IEnumerable<IToolResultMiddleware>? resultMiddleware = null,
        ToolInvocationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(invocationMiddleware);

        _options = options ?? new ToolInvocationOptions();

        InvocationMiddleware = [.. invocationMiddleware];
        _results = resultMiddleware is null ? [] : [.. resultMiddleware];
        ResultMiddleware = _results;

        if (InvocationMiddleware.Any(m => m is null) || _results.Any(m => m is null))
        {
            throw new ArgumentException("A tool invocation pipeline cannot contain a null middleware.");
        }

        ToolInvocationNext next = InvokeToolAsync;
        for (var i = InvocationMiddleware.Count - 1; i >= 0; i--)
        {
            var middleware = InvocationMiddleware[i];
            var inner = next;
            next = (context, cancellationToken) => middleware.InvokeAsync(context, inner, cancellationToken);
        }

        var composed = next;
        Invoker = (context, cancellationToken) => composed(context, cancellationToken);
    }

    /// <summary>
    /// The loop guards a container registers by default: <see cref="ArgumentParseFailureMiddleware"/>,
    /// <see cref="RepeatedCallGuardMiddleware"/>, <see cref="RepeatedResultGuardMiddleware"/> and
    /// <see cref="RepeatedErrorGuardMiddleware"/>, in that order.
    /// </summary>
    /// <param name="options">Their thresholds, and the time limit per call; defaults when null.</param>
    public static ToolInvocationPipeline CreateDefault(ToolInvocationOptions? options = null) =>
        new(
        [
            new ArgumentParseFailureMiddleware(options),
            new RepeatedCallGuardMiddleware(options),
            new RepeatedResultGuardMiddleware(options),
            new RepeatedErrorGuardMiddleware(options),
        ],
        resultMiddleware: null,
        options);

    /// <summary>The steps around each call, outermost first.</summary>
    public IReadOnlyList<IToolInvocationMiddleware> InvocationMiddleware { get; }

    /// <summary>The steps over each result, in the order they run.</summary>
    public IReadOnlyList<IToolResultMiddleware> ResultMiddleware { get; }

    /// <summary>
    /// The whole pipeline as one delegate — what a <see cref="FunctionInvokingChatClient.FunctionInvoker"/> is set to.
    /// </summary>
    public Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Invoker { get; }

    /// <summary>Runs one call through the pipeline, for a host's own tool loop.</summary>
    /// <param name="context">The call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Invoker(context, cancellationToken);
    }

    /// <summary>
    /// Runs the result stage over one result — what the model receives in its place. A host that runs a tool itself
    /// calls this before handing the result to the model; <c>IAgentLoop.ContinueAsync</c> does it for the results
    /// appended since the loop's last turn.
    /// </summary>
    /// <param name="context">The result and the call it answers.</param>
    /// <param name="cancellationToken">Cancels the inspection.</param>
    public async ValueTask<object?> ProcessResultAsync(ToolResultContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = context.Result;
        foreach (var step in _results)
        {
            if (result is ToolCallRefusal)
            {
                break;
            }

            result = await step.OnResultAsync(context with { Result = result }, cancellationToken);
        }

        return result;
    }

    private async ValueTask<object?> InvokeToolAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        var limit = ToolInvocationHints.GetMaxDuration(context.Function) ?? _options.MaxInvocationDuration;
        object? result;
        if (limit is { } bounded && bounded != Timeout.InfiniteTimeSpan)
        {
            var timedOut = await InvokeWithinAsync(context, bounded, cancellationToken);
            if (timedOut.Refusal is { } refusal)
            {
                return refusal;
            }

            result = timedOut.Result;
        }
        else
        {
            result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        }

        if (_results.Length == 0)
        {
            return result;
        }

        return await ProcessResultAsync(new ToolResultContext
        {
            CallId = context.CallContent?.CallId ?? string.Empty,
            ToolName = context.Function.Name,
            Arguments = context.Arguments,
            Result = result,
            Messages = context.Messages as IReadOnlyList<ChatMessage> ?? [.. context.Messages],
            IsHostResult = false,
        }, cancellationToken);
    }

    /// <summary>
    /// Runs the tool with its token cancelled at <paramref name="limit"/>. A tool that stops on its token, or one that
    /// does not but finishes within <see cref="ToolInvocationOptions.AbandonGrace"/>, ends the wait; past that the run is
    /// abandoned. Either way past the limit the call answers a <see cref="ToolCallRefusalKind.TimedOut"/> refusal. The
    /// caller's own cancellation still throws.
    /// </summary>
    private async ValueTask<(object? Result, ToolCallRefusal? Refusal)> InvokeWithinAsync(
        FunctionInvocationContext context,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        var run = context.Function.InvokeAsync(context.Arguments, deadline.Token).AsTask();

        try
        {
            using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            abandon.CancelAfter(limit + _options.AbandonGrace);
            await Task.WhenAny(run, Task.Delay(Timeout.Infinite, abandon.Token));
            cancellationToken.ThrowIfCancellationRequested();
            if (run.IsCompleted)
            {
                return (await run, null);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The tool stopped on the deadline's token: it timed out.
        }

        if (!run.IsCompleted)
        {
            // Abandoned: nobody awaits it any more, so its eventual failure must not surface as an unobserved exception.
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        // The same text every time for the same tool and limit: the repeated-error guard keys on it.
        return (null, new ToolCallRefusal(
            ToolCallRefusalKind.TimedOut,
            $"'{context.Function.Name}' did not finish within {limit.TotalSeconds:0.###} s and was stopped; " +
            "ask for less at once (a narrower path, a smaller range) or try another way."));
    }
}
