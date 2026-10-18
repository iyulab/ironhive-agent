using IronHive.Abstractions.Messages;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// <see cref="AgentOptions.Suggestions"/> for both loops: the instruction on every call, and the blocks taken out of the
/// answer text before it reaches the caller or the history. One collector per turn; the protocol is IronHive's
/// (<see cref="SuggestionCollector"/>), so a host moving from the IronHive message loop gets the same suggestions.
/// </summary>
internal sealed class TurnSuggestions
{
    private readonly SuggestionCollector? _collector;

    public TurnSuggestions(SuggestionOptions? options) => _collector = options is null ? null : new SuggestionCollector();

    /// <summary>Appends the suggestion instruction to a call's instructions (after any per-turn override).</summary>
    public static ChatOptions Request(ChatOptions options, SuggestionOptions? suggestions)
    {
        if (suggestions is not null)
        {
            options.Instructions = SuggestionCollector.Prompt(options.Instructions, suggestions);
        }

        return options;
    }

    /// <summary>Takes the blocks out of a whole response's assistant text, in place, and returns them.</summary>
    public IReadOnlyList<Suggestion>? Strip(IEnumerable<ChatMessage> messages)
    {
        if (_collector is null)
        {
            return null;
        }

        foreach (var message in messages.Where(m => m.Role == ChatRole.Assistant))
        {
            foreach (var text in message.Contents.OfType<TextContent>())
            {
                text.Text = _collector.Feed(text.Text) + _collector.Flush();
            }
        }

        return _collector.Drain();
    }

    /// <summary>The visible part of a streamed text piece (a block may span pieces).</summary>
    public string Feed(string text) => _collector is null ? text : _collector.Feed(text);

    /// <summary>Text held back to see whether a block starts — returned when the text it belongs to is complete.</summary>
    public string Flush() => _collector?.Flush() ?? string.Empty;

    /// <summary>The blocks collected from a streamed turn.</summary>
    public IReadOnlyList<Suggestion>? Drain() => _collector?.Drain();
}
