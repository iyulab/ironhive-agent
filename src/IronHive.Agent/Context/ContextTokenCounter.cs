using IndexThinking.Tokenization;
using Microsoft.Extensions.AI;
using TokenMeter;

namespace IronHive.Agent.Context;

/// <summary>
/// Token counter for chat messages using character-based estimation and TokenMeter model catalog.
/// </summary>
/// <remarks>
/// Counts are IndexThinking's <see cref="ApproximateTokenCounter"/>: letters, digits, punctuation and whitespace counted
/// apart per script, with ratios fitted to o200k (within a few percent on English, Korean, mixed prose, code and JSON) - a
/// single four-characters-per-token rule counts Korean text at a third of its size, and every budget measured with it lets
/// that much more through.
/// </remarks>
public class ContextTokenCounter : IContextTokenCounter
{
    private int _maxContextTokens;

    public ContextTokenCounter(string modelName = "gpt-4o", int? maxContextTokens = null)
    {
        ModelName = modelName;
        var known = maxContextTokens ?? ModelCatalog.FindModel(modelName)?.ContextWindow;
        _maxContextTokens = known ?? 8192;
        IsContextWindowEstimated = known is null;
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public int MaxContextTokens => Volatile.Read(ref _maxContextTokens);

    /// <inheritdoc />
    public bool IsContextWindowEstimated { get; private set; }

    /// <inheritdoc />
    public bool LearnContextWindow(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokens);
        Volatile.Write(ref _maxContextTokens, tokens);
        IsContextWindowEstimated = false;
        return true;
    }

    /// <inheritdoc />
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return EstimateTokens(text);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Everything the message sends counts: its text, a tool call's name and arguments, a tool result (a content result
    /// such as an MCP tool's text and images by its parts), reasoning text, and
    /// <see cref="ApproximateTokenCounter.ImageTokens"/> per image - IndexThinking's message count, so a budget measured
    /// here matches the estimate the chat client checks against the window.
    /// </remarks>
    public int CountTokens(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Estimator.Count(message);
    }

    /// <inheritdoc />
    public int CountTokens(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        foreach (var message in messages)
        {
            total += CountTokens(message);
        }

        total += 3; // conversation priming overhead
        return total;
    }

    private static readonly ApproximateTokenCounter Estimator = new();

    private static int EstimateTokens(string text) => Estimator.Count(text);

    public static ContextTokenCounter ForGpt4o() => new("gpt-4o", 128000);
    public static ContextTokenCounter ForClaude35Sonnet() => new("claude-3.5-sonnet", 200000);
    public static ContextTokenCounter Default() => new("gpt-4", 8192);
}
