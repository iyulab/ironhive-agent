using Ironbees.Core;

namespace IronHive.Agent.Delegation;

/// <summary>
/// One Ironbees named agent exposed to a model as a tool it can delegate a sub-task to.
/// </summary>
/// <remarks>
/// The agent's tools, system prompt and default model come from its definition (<c>agents/{name}/agent.yaml</c>);
/// an agent that lists its <c>tools</c> gets exactly those. The members here override the model call for this
/// delegation only.
/// </remarks>
public sealed record DelegatedAgent
{
    /// <summary>The Ironbees agent name (its directory under the agents root).</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// The tool name the model sees. Defaults to <see cref="AgentName"/> with characters a function name cannot
    /// carry replaced by <c>_</c>.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// What the model reads when deciding whether to delegate. Defaults to the agent definition's description —
    /// write that one well, because it is how the model chooses.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The model to run this agent on, overriding its definition (sent as <see cref="ProcessOptions.ModelOverride"/>).
    /// Also the id used to price the delegation's usage against the parent's limit.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>Reasoning level for the delegated run. Null leaves the model's default.</summary>
    public ThinkingEffort? ThinkingEffort { get; init; }

    /// <summary>Output token cap for the delegated run. Null leaves the agent's configured value.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Maximum model round-trips the delegated run's tool loop may make. Null leaves the adapter default. When the
    /// run stops at this limit, the tool result tells the calling model the answer is partial.
    /// </summary>
    public int? MaxToolTurns { get; init; }
}
