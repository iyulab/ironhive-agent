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
}
