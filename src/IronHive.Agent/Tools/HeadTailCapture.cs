using System.Globalization;
using System.Text;

namespace IronHive.Agent.Tools;

/// <summary>
/// Collects the lines of a stream whose length is not known in advance, keeping its beginning and its end within a
/// fixed budget. Command output carries its most useful lines at the end (the error, the summary, the failing test),
/// so a cap that keeps only the head throws away exactly what the model needs; this one drops the middle and says how
/// much it dropped.
/// </summary>
/// <remarks>Safe to feed from the stdout and stderr callbacks of a process, which arrive on different threads.</remarks>
internal sealed class HeadTailCapture
{
    private readonly int _headChars;
    private readonly int _tailChars;
    private readonly StringBuilder _head = new();
    private readonly Queue<string> _tail = new();
    private readonly object _gate = new();
    private int _tailLength;
    private bool _headFull;
    private long _omittedChars;

    /// <param name="headChars">Characters kept from the beginning.</param>
    /// <param name="tailChars">Characters kept from the end.</param>
    public HeadTailCapture(int headChars, int tailChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailChars);
        _headChars = headChars;
        _tailChars = tailChars;
    }

    /// <summary>Appends one line (a line terminator is added).</summary>
    public void AppendLine(string line)
    {
        lock (_gate)
        {
            if (!_headFull)
            {
                var room = _headChars - _head.Length;
                if (line.Length + 1 <= room)
                {
                    _head.Append(line).Append('\n');
                    return;
                }

                // The head ends at a line boundary. Only a first line longer than the whole head is split: its start
                // is the head, the rest goes on to the tail.
                _headFull = true;
                if (_head.Length == 0)
                {
                    _head.Append(line, 0, room);
                    line = line[room..];
                }
            }

            // A single line longer than the whole tail keeps only its end.
            if (line.Length + 1 > _tailChars)
            {
                _omittedChars += line.Length + 1 - _tailChars;
                line = line[^(_tailChars - 1)..];
            }

            var entry = line + "\n";
            _tail.Enqueue(entry);
            _tailLength += entry.Length;
            while (_tailLength > _tailChars)
            {
                var dropped = _tail.Dequeue();
                _tailLength -= dropped.Length;
                _omittedChars += dropped.Length;
            }
        }
    }

    /// <summary>Whether nothing has been appended.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _head.Length == 0 && _tail.Count == 0;
            }
        }
    }

    /// <summary>The kept head and tail, with a marker where the middle was dropped.</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            var result = new StringBuilder(_head.Length + _tailLength + 64);
            result.Append(_head);
            if (_omittedChars > 0)
            {
                if (result.Length > 0 && result[^1] != '\n')
                {
                    result.Append('\n');
                }

                result.Append(CultureInfo.InvariantCulture, $"[... {_omittedChars} characters omitted ...]\n");
            }

            foreach (var entry in _tail)
            {
                result.Append(entry);
            }

            return result.ToString();
        }
    }
}
