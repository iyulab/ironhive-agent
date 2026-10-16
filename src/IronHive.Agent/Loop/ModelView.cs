using IronHive.Agent.Context;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Loop;

/// <summary>
/// What a loop sends to the model when context management has reduced it, kept apart from the loop's history. The history
/// keeps every message as it happened; the view is the reduced front (a summary, masked or trimmed tool results) standing
/// for the history's first <c>covered</c> messages, followed by the history's later messages as they are. The next
/// preparation starts from the view, so a summary is made once and then extended, not redone from the whole history.
/// </summary>
/// <remarks>
/// The split is found by reference: preparation returns the messages it did not change as the same objects, so the longest
/// run at the end of the prepared list that is the history's own tail is what the view does not cover. Composed blocks
/// (<see cref="ContextManager.IsInjected"/>) are recomposed on every preparation and never kept.
/// </remarks>
internal sealed class ModelView
{
    private List<ChatMessage>? _front;
    private int _covered;

    /// <summary>The view of <paramref name="history"/> to prepare: the reduced front, then the history it does not cover.</summary>
    public IReadOnlyList<ChatMessage> Compose(IReadOnlyList<ChatMessage> history)
    {
        if (_front is null || _covered > history.Count)
        {
            _front = null;
            _covered = 0;
            return history;
        }

        var view = new List<ChatMessage>(_front.Count + history.Count - _covered);
        view.AddRange(_front);
        for (var i = _covered; i < history.Count; i++)
        {
            view.Add(history[i]);
        }

        return view;
    }

    /// <summary>Records what preparation made of the view, for the next turn.</summary>
    public void Update(IReadOnlyList<ChatMessage> prepared, IReadOnlyList<ChatMessage> history)
    {
        // Walk back over the history's own tail as it appears, unchanged, at the end of the prepared list
        var p = prepared.Count - 1;
        var h = history.Count - 1;
        while (p >= 0 && h >= 0)
        {
            if (ContextManager.IsInjected(prepared[p]))
            {
                p--;
                continue;
            }

            if (!ReferenceEquals(prepared[p], history[h]))
            {
                break;
            }

            p--;
            h--;
        }

        var covered = h + 1;
        var front = new List<ChatMessage>(p + 1);
        for (var i = 0; i <= p; i++)
        {
            if (!ContextManager.IsInjected(prepared[i]))
            {
                front.Add(prepared[i]);
            }
        }

        // Nothing reduced: the front is the history's own start
        if (front.Count == covered && front.Select((m, i) => ReferenceEquals(m, history[i])).All(same => same))
        {
            _front = null;
            _covered = 0;
            return;
        }

        _front = front;
        _covered = covered;
    }

    /// <summary>Forgets the view - the history was replaced.</summary>
    public void Reset()
    {
        _front = null;
        _covered = 0;
    }
}
