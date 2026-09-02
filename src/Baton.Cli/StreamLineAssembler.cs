using System.Text;

namespace Baton.Cli;

/// <summary>
/// Assembles complete lines out of bytes that arrive in arbitrary chunks -- a poll's delta read from
/// a growing file, or a bounded tail read from an arbitrary offset (#1574). A partial trailing line
/// (no newline yet) is held across <see cref="Append"/> calls and never rendered, so a line split
/// across two polls renders exactly once, whole, on whichever poll completes it. One instance per
/// stream: the held partial line is per-instance state, not per-call.
/// </summary>
public sealed class StreamLineAssembler
{
    private byte[] _pending = [];

    /// <summary>
    /// Appends <paramref name="data"/> and returns every newline-terminated line it completes, in
    /// order, with a trailing <c>\r</c> stripped so CRLF and LF both terminate a line the same way.
    /// Any bytes after the last newline are held for the next call.
    /// </summary>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> data)
    {
        var combined = _pending.Length == 0 ? data.ToArray() : Combine(_pending, data);

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < combined.Length; i++)
        {
            if (combined[i] != (byte)'\n')
            {
                continue;
            }

            var end = i;
            if (end > start && combined[end - 1] == (byte)'\r')
            {
                end--;
            }

            lines.Add(Encoding.UTF8.GetString(combined, start, end - start));
            start = i + 1;
        }

        _pending = start == 0 ? combined : combined[start..];
        return lines;
    }

    /// <summary>
    /// Drains and returns whatever partial trailing line is still held (<c>null</c> if none), for a
    /// caller that knows no more bytes are coming -- a one-shot reader at EOF, or a poll loop about to
    /// stop -- so that content is not silently lost the way it would be by simply never calling this.
    /// A held line is genuinely incomplete for a caller that WILL see more bytes later (that's the
    /// whole point of holding it); <see cref="Flush"/> exists for the caller that knows it will not.
    /// </summary>
    public string? Flush()
    {
        if (_pending.Length == 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(_pending);
        _pending = [];
        return text;
    }

    private static byte[] Combine(byte[] left, ReadOnlySpan<byte> right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined, 0);
        right.CopyTo(combined.AsSpan(left.Length));
        return combined;
    }
}
