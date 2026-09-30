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

    /// <summary>Folds <paramref name="invocationMiddleware"/> and <paramref name="resultMiddleware"/> into one pipeline.</summary>
    /// <param name="invocationMiddleware">Steps around each call, outermost first.</param>
    /// <param name="resultMiddleware">Steps over each result, in the order they run.</param>
    public ToolInvocationPipeline(
        IEnumerable<IToolInvocationMiddleware> invocationMiddleware,
        IEnumerable<IToolResultMiddleware>? resultMiddleware = null)
    {
        ArgumentNullException.ThrowIfNull(invocationMiddleware);

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
    /// <see cref="RepeatedCallGuardMiddleware"/> and <see cref="RepeatedErrorGuardMiddleware"/>, in that order.
    /// </summary>
    /// <param name="options">Their thresholds; defaults when null.</param>
    public static ToolInvocationPipeline CreateDefault(ToolInvocationOptions? options = null) =>
        new(
        [
            new ArgumentParseFailureMiddleware(options),
            new RepeatedCallGuardMiddleware(options),
            new RepeatedErrorGuardMiddleware(options),
        ]);

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
        var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
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
}
