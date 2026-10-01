using System.Diagnostics;

namespace IronHive.Agent.Loop;

/// <summary>
/// OpenTelemetry tracing for the agent loop, following the GenAI semantic conventions: every turn of an
/// <see cref="AgentLoop"/> or <see cref="ThinkingAgentLoop"/> is an <c>invoke_agent</c> span on the
/// <see cref="SourceName"/> activity source. Model calls and tool runs inside the turn appear as its children when the
/// chat client carries <c>UseOpenTelemetry()</c> (Microsoft.Extensions.AI emits <c>chat</c> and <c>execute_tool</c> spans).
/// </summary>
/// <remarks>
/// Listen with <c>tracing.AddSource(AgentTelemetry.SourceName)</c> plus the source name given to <c>UseOpenTelemetry</c>.
/// No span is created while nothing listens. Message content is never recorded.
/// </remarks>
public static class AgentTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name of the agent loop's spans.</summary>
    public const string SourceName = "IronHive.Agent";

    internal static readonly ActivitySource Source = new(SourceName, typeof(AgentTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>Starts the span of one turn, or returns null when nothing listens.</summary>
    internal static Activity? StartTurn(AgentOptions options)
    {
        var name = string.IsNullOrWhiteSpace(options.Name) ? "invoke_agent" : $"invoke_agent {options.Name}";
        var activity = Source.StartActivity(name, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        if (!string.IsNullOrWhiteSpace(options.Name))
        {
            activity.SetTag("gen_ai.agent.name", options.Name);
        }

        if (!string.IsNullOrWhiteSpace(options.ModelId))
        {
            activity.SetTag("gen_ai.request.model", options.ModelId);
        }

        return activity;
    }

    /// <summary>Records what the turn used and how many tool calls it made.</summary>
    internal static void Complete(Activity? activity, TokenUsage? usage, int toolCalls, TurnStopReason stopReason)
    {
        if (activity is null)
        {
            return;
        }

        if (usage is not null)
        {
            activity.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
            activity.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
            if (usage.CachedInputTokens > 0)
            {
                activity.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CachedInputTokens);
            }
        }

        activity.SetTag("ironhive.agent.tool_calls", toolCalls);
        activity.SetTag("ironhive.agent.stop_reason", stopReason.ToString());
    }

    /// <summary>Marks the turn failed with the exception that ended it.</summary>
    internal static void Fail(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("error.type", exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
