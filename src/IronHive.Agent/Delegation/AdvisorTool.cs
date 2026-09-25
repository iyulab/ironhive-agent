using System.Globalization;
using System.Text;
using System.Text.Json;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Delegation;

/// <summary>
/// A tool that lets the working model consult a stronger one: calling it sends the conversation so far to an advisor
/// model, whose review comes back as the tool result.
/// </summary>
/// <remarks>
/// <para>The tool takes no arguments — the advisor sees the whole conversation, so the working model does not have to
/// summarise its situation (and cannot leave out what it did not notice). Typically the working model is a fast one and
/// the advisor a stronger one, so the strong model is paid for only at the points where judgment matters.</para>
/// <para>The conversation is sent as a rendered transcript, not as the original messages: a history containing tool
/// calls is rejected by some providers when the request defines no tools, and the advisor is never given tools.</para>
/// </remarks>
public static class AdvisorTool
{
    internal const string DefaultDescription =
        "Consult a stronger reviewer model about the task so far. Takes no input: it reads the whole conversation, " +
        "including your tool calls and their results. Call it before committing to an approach on a non-trivial task, " +
        "when you are stuck or results do not fit, and before declaring the task done. Weigh its advice seriously.";

    internal const string DefaultInstructions =
        "You are the advisor to an AI agent that is partway through a task. You see its conversation: the user's " +
        "request, its reasoning, the tools it called and what they returned. Review it as a senior colleague would. " +
        "Say whether the approach is sound; point out mistakes, unverified assumptions and missed requirements; and " +
        "state the concrete next step you recommend. Be brief and specific — a few sentences or a short list. " +
        "Do not redo the work, and do not repeat what the agent already knows.";

    /// <summary>Creates the advisor tool.</summary>
    /// <param name="advisor">The client for the advisor model — usually a stronger model than the one calling the tool.</param>
    /// <param name="options">Name, description, limits, and accounting.</param>
    public static AIFunction Create(IChatClient advisor, AdvisorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(advisor);
        var settings = options ?? new AdvisorOptions();
        if (settings.MaxCalls is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), settings.MaxCalls, "AdvisorOptions.MaxCalls must be at least 1 when set.");
        }

        var calls = 0;

        async Task<string> ConsultAsync(CancellationToken cancellationToken)
        {
            var conversation = settings.Conversation?.Invoke()
                ?? (IReadOnlyList<ChatMessage>?)FunctionInvokingChatClient.CurrentContext?.Messages?.ToList()
                ?? ToolInvocationScope.Messages
                ?? throw new InvalidOperationException(
                    "The advisor could not see a conversation: it was not called from a tool loop that publishes one " +
                    "(FunctionInvokingChatClient, or a loop that sets ToolInvocationScope such as ChatClientFrameworkAdapter). Set AdvisorOptions.Conversation to supply it.");

            if (settings.MaxCalls is { } max && Interlocked.Increment(ref calls) > max)
            {
                return $"The advisor has already been consulted {max} times in this session. Proceed on your own judgment.";
            }

            if (settings.UsageLimiter?.CheckLimits() is { ShouldStop: true } limit)
            {
                return $"The advisor was not consulted: the session usage limit is reached ({limit.Message}). Proceed on your own judgment.";
            }

            var request = new List<ChatMessage>
            {
                new(ChatRole.System, settings.Instructions),
                new(ChatRole.User, Render(conversation, settings.MaxToolResultChars)),
            };

            var chatOptions = settings.ChatOptions?.Clone() ?? new ChatOptions();
            chatOptions.Tools = null;
            chatOptions.ToolMode = null;

            var response = await advisor.GetResponseAsync(request, chatOptions, cancellationToken);
            Account(settings, response.Usage);

            var advice = response.Text;
            return string.IsNullOrWhiteSpace(advice)
                ? "The advisor returned no advice. Proceed on your own judgment."
                : advice;
        }

        return AIFunctionFactory.Create(ConsultAsync, new AIFunctionFactoryOptions
        {
            Name = settings.ToolName,
            Description = settings.Description,
        });
    }

    /// <summary>
    /// Renders a conversation as a plain transcript for the advisor: every role and text, each tool call with its
    /// arguments, each tool result (cut to <paramref name="maxToolResultChars"/>). Reasoning content is left out —
    /// the advisor reviews what the agent did and said.
    /// </summary>
    internal static string Render(IReadOnlyList<ChatMessage> conversation, int maxToolResultChars)
    {
        var text = new StringBuilder("Here is the agent's conversation so far.\n\n");

        foreach (var message in conversation)
        {
            var label = message.Role == ChatRole.System ? "Agent instructions"
                : message.Role == ChatRole.User ? "User"
                : message.Role == ChatRole.Assistant ? "Agent"
                : message.Role == ChatRole.Tool ? "Tool results"
                : message.Role.Value;

            var body = new StringBuilder();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text: { Length: > 0 } t }:
                        body.AppendLine(t);
                        break;
                    case FunctionCallContent call:
                        body.Append(CultureInfo.InvariantCulture, $"-> calls {call.Name}(")
                            .Append(call.Arguments is null ? string.Empty : JsonSerializer.Serialize(call.Arguments))
                            .AppendLine(")");
                        break;
                    case FunctionResultContent result:
                        body.Append("<- ").AppendLine(Cut(result.Result?.ToString() ?? "(no result)", maxToolResultChars));
                        break;
                }
            }

            if (body.Length > 0)
            {
                text.Append('[').Append(label).AppendLine("]").Append(body).AppendLine();
            }
        }

        text.Append("Review the work so far and advise the agent on what to do next.");
        return text.ToString();
    }

    private static string Cut(string value, int max)
        => value.Length <= max ? value : string.Concat(value.AsSpan(0, max), $" ... [{value.Length - max} more characters cut]");

    private static void Account(AdvisorOptions settings, UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        var tokens = TokenUsage.From(usage)!;
        settings.UsageTracker?.Record(tokens);

        if (settings.UsageLimiter is { } limiter)
        {
            var pricing = !string.IsNullOrEmpty(settings.ModelId) ? ModelCatalog.FindModel(settings.ModelId) : null;
            var cost = tokens.CostAt(pricing) ?? 0m;
            limiter.RecordTokenUsage((int)tokens.TotalTokens, cost);
        }
    }
}
