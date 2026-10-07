using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// Provider-native reasoning streamed as M.E.AI <see cref="TextReasoningContent"/> — what a reasoning-capable model's
/// bridge yields while it thinks (Chat Completions <c>reasoning_content</c>, Anthropic thinking blocks, the streaming
/// reasoning separator). Both loops bridge it to <see cref="AgentResponseChunk.ThinkingDelta"/> as it arrives.
/// </summary>
internal static class LiveReasoning
{
    /// <summary>The reasoning text this update carries, or <see langword="null"/> when it carries none.</summary>
    public static string? Extract(ChatResponseUpdate update)
    {
        var joined = string.Concat(update.Contents
            .OfType<TextReasoningContent>()
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrEmpty(t)));

        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    /// <summary>The reasoning a non-streamed response carries, or <see langword="null"/> when it carries none.</summary>
    public static string? Extract(ChatResponse response)
    {
        var joined = string.Concat(response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextReasoningContent>()
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrEmpty(t)));

        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    /// <summary>The turn's recorded reasoning, or <see langword="null"/> when the model wrote none.</summary>
    public static ThinkingContent? ToThinkingContent(string? reasoning)
        => string.IsNullOrEmpty(reasoning) ? null : new ThinkingContent { Content = reasoning };
}
