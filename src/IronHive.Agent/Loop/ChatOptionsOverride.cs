using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Merges a per-turn <see cref="ChatOptions"/> override onto a loop-computed baseline. Shared by
/// <see cref="AgentLoop"/> and <see cref="ThinkingAgentLoop"/> so the merge semantics stay identical
/// across both implementations.
/// </summary>
internal static class ChatOptionsOverride
{
    /// <summary>
    /// Applies <paramref name="overrideOptions"/> onto <paramref name="baseline"/> field by field.
    /// <see cref="ChatOptions.Temperature"/>, <see cref="ChatOptions.MaxOutputTokens"/> and
    /// <see cref="ChatOptions.Tools"/> are replaced when set on the override; unset fields keep the
    /// baseline value. <see cref="ChatOptions.AdditionalProperties"/> entries are merged, with the
    /// override winning on key conflicts. Returns <paramref name="baseline"/> unchanged when
    /// <paramref name="overrideOptions"/> is <c>null</c>.
    /// </summary>
    public static ChatOptions Apply(ChatOptions baseline, ChatOptions? overrideOptions)
    {
        if (overrideOptions is null)
        {
            return baseline;
        }

        if (overrideOptions.Temperature is not null)
        {
            baseline.Temperature = overrideOptions.Temperature;
        }

        if (overrideOptions.MaxOutputTokens is not null)
        {
            baseline.MaxOutputTokens = overrideOptions.MaxOutputTokens;
        }

        if (overrideOptions.Tools is not null)
        {
            baseline.Tools = overrideOptions.Tools;
        }

        if (overrideOptions.AdditionalProperties is { Count: > 0 } overrideProperties)
        {
            baseline.AdditionalProperties ??= [];
            foreach (var (key, value) in overrideProperties)
            {
                baseline.AdditionalProperties[key] = value;
            }
        }

        return baseline;
    }
}
