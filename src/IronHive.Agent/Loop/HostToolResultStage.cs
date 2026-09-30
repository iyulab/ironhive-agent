using System.Runtime.CompilerServices;
using IronHive.Agent.Invocation;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Puts the tool results a host appended before a continuation through the result stage of the
/// <see cref="ToolInvocationPipeline"/> the loop's chat client carries — each result once. Results the loop's own turns
/// produced already went through the pipeline and are never processed here; neither are results before the trailing
/// tool messages.
/// </summary>
internal sealed class HostToolResultStage
{
    private readonly ToolInvocationPipeline? _pipeline;
    // By identity, and without keeping a result alive once the history no longer holds it.
    private readonly ConditionalWeakTable<FunctionResultContent, object> _processed = [];

    public HostToolResultStage(IChatClient chatClient)
    {
        _pipeline = chatClient.GetService<ToolInvocationPipeline>();
    }

    private static readonly object Processed = new();

    private bool Active => _pipeline is { ResultMiddleware.Count: > 0 };

    /// <summary>Records the results in <paramref name="messages"/> (a turn's output) as already processed.</summary>
    public void MarkProduced(IEnumerable<ChatMessage> messages)
    {
        if (!Active)
        {
            return;
        }

        foreach (var result in messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()))
        {
            _processed.AddOrUpdate(result, Processed);
        }
    }

    /// <summary>
    /// Runs the result stage over the unprocessed results in the trailing tool messages of <paramref name="history"/>,
    /// replacing each result with what the stage returns.
    /// </summary>
    public async Task ApplyAsync(List<ChatMessage> history, CancellationToken cancellationToken)
    {
        if (!Active || history.Count == 0 || history[^1].Role != ChatRole.Tool)
        {
            return;
        }

        var first = history.Count - 1;
        while (first > 0 && history[first - 1].Role == ChatRole.Tool)
        {
            first--;
        }

        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        if (first > 0)
        {
            foreach (var call in history[first - 1].Contents.OfType<FunctionCallContent>())
            {
                calls.TryAdd(call.CallId, call);
            }
        }

        for (var i = first; i < history.Count; i++)
        {
            foreach (var result in history[i].Contents.OfType<FunctionResultContent>())
            {
                if (_processed.TryGetValue(result, out _))
                {
                    continue;
                }

                calls.TryGetValue(result.CallId, out var call);
                result.Result = await _pipeline!.ProcessResultAsync(new ToolResultContext
                {
                    CallId = result.CallId,
                    ToolName = call?.Name ?? string.Empty,
                    Arguments = call?.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments),
                    Result = result.Result,
                    Messages = history,
                    IsHostResult = true,
                }, cancellationToken);
                _processed.AddOrUpdate(result, Processed);
            }
        }
    }
}
