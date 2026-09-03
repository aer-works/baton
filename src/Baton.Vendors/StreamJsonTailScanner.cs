using System.Text;
using System.Text.Json;

namespace Baton.Vendors;

/// <summary>
/// Finds the top-level JSON objects inside a captured stdout/stderr tail
/// (<c>Dispatch.CoreDispatcher</c>'s retained tail), for both adapters' typed
/// quota classification. One scanner rather than one per adapter: the tail's shape is a property of
/// how the engine captures it, not of either vendor.
/// <para>
/// <b>Why a scan rather than a line split (#1720 review, found while fixing F1).</b> The retained
/// tail is whitespace-COLLAPSED on the way in (<c>CoreDispatcher</c>'s <c>AppendCollapsed</c>: every
/// whitespace run, newlines included, becomes a single space), so a real multi-line stream-json tail
/// arrives as ONE line of space-separated JSON objects. A whole-string <see cref="JsonDocument.Parse"/>
/// throws on the trailing objects and a <c>'\n'</c> split yields a single line, so a per-line parse
/// finds nothing at all — the shape only survives in tests that build the tail from a raw string
/// literal with real newlines. Scanning for object starts reads both shapes.
/// </para>
/// </summary>
internal static class StreamJsonTailScanner
{
    /// <summary>
    /// True when any top-level JSON object in <paramref name="tail"/> satisfies
    /// <paramref name="predicate"/>. Unparseable candidates are skipped, never thrown: a tail is cut
    /// to a byte budget and routinely begins mid-object.
    /// </summary>
    public static bool AnyObject(string? tail, Func<JsonElement, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        for (var index = 0; index < tail.Length; index++)
        {
            if (tail[index] != '{')
            {
                continue;
            }

            // Only an object that starts the tail, follows whitespace, or directly abuts the previous
            // object's close is a candidate TOP-LEVEL object; a `{` after `:` or `[` is nested, and
            // re-parsing from there would let a nested field match a check meant for the envelope.
            var previous = index > 0 ? tail[index - 1] : ' ';
            if (!char.IsWhiteSpace(previous) && previous != '}')
            {
                continue;
            }

            if (TryMatchAt(tail, index, predicate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchAt(string tail, int index, Func<JsonElement, bool> predicate)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(tail[index..]);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return false;
            }

            using (document)
            {
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && predicate(document.RootElement);
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
