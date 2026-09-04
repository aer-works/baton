using System.Text;

namespace Baton.Cli;

/// <summary>
/// Assembles complete lines out of bytes that arrive in arbitrary chunks -- a poll's delta read from
/// a growing file, or a bounded tail read from an arbitrary offset (#1574). A partial trailing line
/// (no newline yet) is held across <see cref="Append"/> calls and never rendered, so a line split
/// across two polls renders exactly once, whole, on whichever poll completes it. One instance per
/// stream: the held partial line is per-instance state, not per-call.
/// <para>
/// A bare <c>\r</c> (a <c>\r</c>-driven progress spinner, or any non-stream-json binding's raw
/// stdout) also terminates a line, same as <c>\n</c> -- otherwise that output sat buffered and
/// invisible under <c>--follow</c> until either a stray <c>\n</c> eventually arrived or the run
/// reached Terminal (#1574 second-reader finding 3). A CRLF pair (<c>\r</c> immediately followed by
/// <c>\n</c>) is still exactly one line break, not two: see <see cref="Append"/>.
/// </para>
/// </summary>
public sealed class StreamLineAssembler
{
    private byte[] _pending = [];

    /// <summary>
    /// Appends <paramref name="data"/> and returns every line it completes, in order, terminated by
    /// <c>\n</c> or a bare <c>\r</c> (the terminator byte itself is never included in the returned
    /// text). A <c>\r</c> immediately followed by <c>\n</c> in the same call is treated as the single
    /// CRLF terminator it is, not two separate line breaks; a <c>\r</c> that lands as the very last
    /// byte of <paramref name="data"/> has no next byte to check and terminates a line on its own --
    /// if the completing <c>\n</c> of a CRLF pair happens to arrive in the very next call, it renders
    /// as one extra empty line rather than being folded in, a rare byte-timing edge accepted as the
    /// cost of this fix. Any bytes after the last terminator are held for the next call.
    /// </summary>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> data)
    {
        var combined = _pending.Length == 0 ? data.ToArray() : Combine(_pending, data);

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < combined.Length; i++)
        {
            var current = combined[i];
            if (current != (byte)'\n' && current != (byte)'\r')
            {
                continue;
            }

            // Let the '\n' below terminate a CRLF pair and strip the '\r' as its trailing byte,
            // rather than this '\r' terminating a (near-)empty line of its own first.
            if (current == (byte)'\r' && i + 1 < combined.Length && combined[i + 1] == (byte)'\n')
            {
                continue;
            }

            var end = i;
            if (current == (byte)'\n' && end > start && combined[end - 1] == (byte)'\r')
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
