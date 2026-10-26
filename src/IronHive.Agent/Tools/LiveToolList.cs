using System.Collections;
using Microsoft.Extensions.AI;

namespace IronHive.Agent.Tools;

/// <summary>
/// A tool list that can change while a loop runs. Every read - enumeration, <see cref="Count"/>, the indexer - sees one
/// consistent snapshot, and a change replaces the snapshot, so a turn that is already enumerating the tools is not
/// disturbed by a tool added behind it (a <see cref="List{T}"/> would throw, or hand out a half-changed list).
/// </summary>
/// <remarks>
/// Hand it to <see cref="IronHive.Agent.Loop.AgentOptions.Tools"/> and keep the reference: the loop reads the list on every
/// turn, so tools added later (an MCP server that connected after the engine started) are offered from the next turn on.
/// </remarks>
public sealed class LiveToolList : IList<AITool>
{
    private readonly object _gate = new();
    private AITool[] _items;

    /// <param name="items">The tools the list starts with.</param>
    public LiveToolList(IEnumerable<AITool>? items = null)
    {
        _items = items is null ? [] : [.. items];
    }

    /// <summary>
    /// Takes <paramref name="remove"/> out and puts <paramref name="add"/> at the end, as one change: no reader sees the
    /// tools of a reconnected plugin missing or listed twice. A tool in <paramref name="remove"/> that is not in the list is
    /// ignored; tools are matched by reference.
    /// </summary>
    public void Replace(IEnumerable<AITool> remove, IEnumerable<AITool> add)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        var gone = new HashSet<AITool>(remove, ReferenceEqualityComparer.Instance);
        var added = add.ToArray();
        lock (_gate)
        {
            _items = [.. _items.Where(tool => !gone.Contains(tool)), .. added];
        }
    }

    /// <inheritdoc />
    public int Count => Volatile.Read(ref _items).Length;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public AITool this[int index]
    {
        get => Volatile.Read(ref _items)[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                var next = (AITool[])_items.Clone();
                next[index] = value;
                _items = next;
            }
        }
    }

    /// <inheritdoc />
    public void Add(AITool item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _items = [.. _items, item];
        }
    }

    /// <inheritdoc />
    public void Insert(int index, AITool item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_items.Length, nameof(index));
            var list = new List<AITool>(_items);
            list.Insert(index, item);
            _items = [.. list];
        }
    }

    /// <inheritdoc />
    public bool Remove(AITool item)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_items, item);
            if (index < 0)
            {
                return false;
            }

            _items = [.. _items.Where((_, i) => i != index)];
            return true;
        }
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_items.Length, nameof(index));
            _items = [.. _items.Where((_, i) => i != index)];
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _items = [];
        }
    }

    /// <inheritdoc />
    public bool Contains(AITool item) => Array.IndexOf(Volatile.Read(ref _items), item) >= 0;

    /// <inheritdoc />
    public int IndexOf(AITool item) => Array.IndexOf(Volatile.Read(ref _items), item);

    /// <inheritdoc />
    public void CopyTo(AITool[] array, int arrayIndex) => Volatile.Read(ref _items).CopyTo(array, arrayIndex);

    /// <summary>An enumerator over the tools as they are now; later changes do not reach it.</summary>
    public IEnumerator<AITool> GetEnumerator() => ((IEnumerable<AITool>)Volatile.Read(ref _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
