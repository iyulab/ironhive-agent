using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Delegation;

/// <summary>
/// Settings for <see cref="AdvisorTool"/>.
/// </summary>
public sealed record AdvisorOptions
{
    /// <summary>The tool name the model sees. Default <c>advisor</c>.</summary>
    public string ToolName { get; init; } = "advisor";

    /// <summary>
    /// What the model reads when deciding whether to consult. The default says when consulting pays off — before
    /// committing to an approach, when stuck, and before declaring the task done.
    /// </summary>
    public string Description { get; init; } = AdvisorTool.DefaultDescription;

    /// <summary>The advisor's system prompt. The default asks for a short, concrete review of the work so far.</summary>
    public string Instructions { get; init; } = AdvisorTool.DefaultInstructions;

    /// <summary>
    /// How many times one tool instance may be consulted; further calls return a result telling the model to go on
    /// with its own judgment. Create one tool per session for this to mean «per session». Null means no limit.
    /// Default 5.
    /// </summary>
    public int? MaxCalls { get; init; } = 5;

    /// <summary>
    /// Tool results longer than this are cut when the conversation is shown to the advisor — it needs what happened,
    /// not every byte a tool returned. Default 2000 characters.
    /// </summary>
    public int MaxToolResultChars { get; init; } = 2000;

    /// <summary>Options for the advisor's own model call (output cap, reasoning level). No tools are ever sent.</summary>
    public ChatOptions? ChatOptions { get; init; }

    /// <summary>
    /// Supplies the conversation to review. By default the advisor reads it from the tool loop it is called from —
    /// <see cref="FunctionInvokingChatClient.CurrentContext"/>, or the loop of
    /// <see cref="Ironbees.ChatClientFrameworkAdapter"/>. Set this when calling it from somewhere else.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>>? Conversation { get; init; }

    /// <summary>The model id the advisor runs on, used to price its usage against <see cref="UsageLimiter"/>.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// The session's usage limit: a consultation is refused once it is reached, and each consultation's usage is fed
    /// into it — consulting is not a way around the limit.
    /// </summary>
    public IUsageLimiter? UsageLimiter { get; init; }

    /// <summary>The session's usage accounting; each consultation's tokens are recorded into it.</summary>
    public IUsageTracker? UsageTracker { get; init; }
}
