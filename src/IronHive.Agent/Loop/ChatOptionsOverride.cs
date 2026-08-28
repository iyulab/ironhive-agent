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
    /// Applies <paramref name="overrideOptions"/> onto <paramref name="baseline"/> field by field, for
    /// every settable <see cref="ChatOptions"/> property. A set override field replaces the baseline
    /// value; an unset (<c>null</c>) override field keeps the baseline value.
    /// <see cref="ChatOptions.AdditionalProperties"/> entries are merged, with the override winning on
    /// key conflicts. Returns <paramref name="baseline"/> unchanged when <paramref name="overrideOptions"/>
    /// is <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Every <see cref="ChatOptions"/> property must be listed here — the per-call override seam exists
    /// so a caller can change any chat behavior for one call without touching the loop's baseline
    /// configuration, and a property missing from this list is silently unreachable through that seam
    /// (see <c>ChatOptionsOverrideTests.ChatOptionsOverrideSource_ReferencesEveryChatOptionsProperty</c>,
    /// which fails when <c>Microsoft.Extensions.AI</c> adds a new property this method does not yet
    /// handle).
    /// </remarks>
    public static ChatOptions Apply(ChatOptions baseline, ChatOptions? overrideOptions)
    {
        if (overrideOptions is null)
        {
            return baseline;
        }

        if (overrideOptions.ConversationId is not null)
        {
            baseline.ConversationId = overrideOptions.ConversationId;
        }

        if (overrideOptions.Instructions is not null)
        {
            baseline.Instructions = overrideOptions.Instructions;
        }

        if (overrideOptions.Temperature is not null)
        {
            baseline.Temperature = overrideOptions.Temperature;
        }

        if (overrideOptions.MaxOutputTokens is not null)
        {
            baseline.MaxOutputTokens = overrideOptions.MaxOutputTokens;
        }

        if (overrideOptions.TopP is not null)
        {
            baseline.TopP = overrideOptions.TopP;
        }

        if (overrideOptions.TopK is not null)
        {
            baseline.TopK = overrideOptions.TopK;
        }

        if (overrideOptions.FrequencyPenalty is not null)
        {
            baseline.FrequencyPenalty = overrideOptions.FrequencyPenalty;
        }

        if (overrideOptions.PresencePenalty is not null)
        {
            baseline.PresencePenalty = overrideOptions.PresencePenalty;
        }

        if (overrideOptions.Seed is not null)
        {
            baseline.Seed = overrideOptions.Seed;
        }

        if (overrideOptions.Reasoning is not null)
        {
            baseline.Reasoning = overrideOptions.Reasoning;
        }

        if (overrideOptions.ResponseFormat is not null)
        {
            baseline.ResponseFormat = overrideOptions.ResponseFormat;
        }

        if (overrideOptions.ModelId is not null)
        {
            baseline.ModelId = overrideOptions.ModelId;
        }

        if (overrideOptions.StopSequences is not null)
        {
            baseline.StopSequences = overrideOptions.StopSequences;
        }

        if (overrideOptions.AllowMultipleToolCalls is not null)
        {
            baseline.AllowMultipleToolCalls = overrideOptions.AllowMultipleToolCalls;
        }

        if (overrideOptions.ToolMode is not null)
        {
            baseline.ToolMode = overrideOptions.ToolMode;
        }

        if (overrideOptions.Tools is not null)
        {
            baseline.Tools = overrideOptions.Tools;
        }

        if (overrideOptions.AllowBackgroundResponses is not null)
        {
            baseline.AllowBackgroundResponses = overrideOptions.AllowBackgroundResponses;
        }

        if (overrideOptions.ContinuationToken is not null)
        {
            baseline.ContinuationToken = overrideOptions.ContinuationToken;
        }

        if (overrideOptions.RawRepresentationFactory is not null)
        {
            baseline.RawRepresentationFactory = overrideOptions.RawRepresentationFactory;
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
