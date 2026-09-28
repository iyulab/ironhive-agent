using Microsoft.Extensions.AI;

namespace IronHive.Agent.Context;

/// <summary>
/// Counts tokens in chat messages for context management.
/// </summary>
public interface IContextTokenCounter
{
    /// <summary>
    /// Counts tokens in a single message.
    /// </summary>
    int CountTokens(ChatMessage message);

    /// <summary>
    /// Counts total tokens in a list of messages.
    /// </summary>
    int CountTokens(IEnumerable<ChatMessage> messages);

    /// <summary>
    /// Counts tokens in plain text.
    /// </summary>
    int CountTokens(string text);

    /// <summary>
    /// Gets the model name used for tokenization.
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// Gets the maximum context window size for the model.
    /// </summary>
    int MaxContextTokens { get; }

    /// <summary>
    /// <c>true</c> while <see cref="MaxContextTokens"/> is a guess rather than the model's known window — the model was
    /// not configured with a size and is not in the catalog. A context manager built with
    /// <see cref="CompactionConfig.CompactOnOverflow"/> then compacts when the server reports an overflow instead of
    /// pre-emptively against the guess. Defaults to <c>false</c> (a counter that does not say is taken at its word).
    /// </summary>
    bool IsContextWindowEstimated => false;

    /// <summary>
    /// Replaces <see cref="MaxContextTokens"/> with a window learned from the server (a context-overflow error that states
    /// it, or the size of a request that overflowed). Returns <c>false</c> when this counter cannot learn one.
    /// </summary>
    bool LearnContextWindow(int tokens) => false;
}
