using System.Globalization;
using System.Text;
using Baton.Core;

namespace Baton.Vendors;

/// <summary>
/// #1391's agy counterpart to <see cref="ClaudeUsageSlashCommandSource"/> — that type's own doc
/// comment has the shared spawn-shape and spawn-site-registration reasoning. Command here is
/// <c>agy -p "/usage"</c>.
/// record-once-ok: #1391 src/Baton.Vendors/ClaudeUsageSlashCommandSource.cs
/// (only the doc-citation PHRASING pattern below repeats, not its content -- each type cites its own
/// vendor's own section of the same measured-shape document, docs/vendor-capabilities.md.)
/// Measured shape: docs/vendor-capabilities.md "Usage, cost and quota" §"agy —
/// works headlessly too, as of a CLI update", 2026-08-28.
/// </summary>
public sealed class AgyUsageSlashCommandSource : IVendorUsageSource
{
    public string Vendor => "agy";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    public async Task<VendorUsageSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        string stdout;
        try
        {
            using var task = new BatonTask("agy", "-p", "/usage")
                .WithCaptureOutput(true)
                .WithTimeout(CommandTimeout);

            var output = new StringBuilder();
            task.EventRaised += (_, e) =>
            {
                if (e.Kind == BatonTaskEventKind.StdoutChunk && e.Data is { } data)
                {
                    output.Append(Encoding.UTF8.GetString(data));
                }
            };

            await task.RunAsync(cancellationToken).ConfigureAwait(false);
            stdout = output.ToString();
        }
        catch (BatonException)
        {
            return null;
        }

        return Parse(stdout, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Pure parse of agy's <c>/usage</c> stdout, same testability shape as
    /// <see cref="ClaudeUsageSlashCommandSource.Parse"/> (that method's own doc comment has the reasoning).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Row shape, measured (docs/vendor-capabilities.md line ~642-649).</b> One tab-separated row
    /// per quota family/window: <c>"&lt;family&gt;\t&lt;window&gt;\t&lt;pct&gt;%\t&lt;reset instant&gt;"</c>,
    /// e.g. <c>"Gemini Models\tWeekly Limit Remaining\t72%\t2026-08-29T19:34:12Z"</c>. Only the two
    /// Gemini Models rows are real measured values in that doc — the Claude/GPT rows there use literal
    /// <c>&lt;pct&gt;</c>/<c>&lt;reset instant&gt;</c> placeholders and are not pinned as fixtures here.
    /// </para>
    /// <para>
    /// <b>"Remaining", not "used".</b> agy's own column is percent REMAINING
    /// (<c>"Weekly Limit Remaining"</c>), the opposite sense of claude's percent USED. This method
    /// converts to <see cref="VendorUsageWindow.PercentUsed"/> = <c>100 - remaining</c> so both
    /// vendors' windows carry the same sense on the wire — silently keeping agy's raw percentage under
    /// this field's name would show a nearly-full account as nearly-empty.
    /// </para>
    /// <para>
    /// <b>Window name.</b> The family and window text collide across rows (every family repeats
    /// "Weekly Limit Remaining" and "Five Hour Limit Remaining"), so <see cref="VendorUsageWindow.Name"/>
    /// composes both: <c>"&lt;family&gt; · &lt;window&gt;"</c>.
    /// </para>
    /// <para>
    /// <b>Reset instant.</b> Already ISO 8601 (<c>Z</c>-suffixed) — parsed directly, no year-rolling
    /// heuristic needed the way claude's format requires. A row whose fourth field does not parse
    /// leaves <see cref="VendorUsageWindow.ResetsAt"/> null.
    /// </para>
    /// <para>
    /// <b>Caveat.</b> No agy-specific machine-local disclaimer text is documented anywhere in
    /// docs/vendor-capabilities.md's "Usage, cost and quota" section as of this issue — unlike
    /// claude's explicit quoted caveat, nothing here is fabricated to match its shape.
    /// <see cref="VendorUsageSnapshot.Caveat"/> is always null for this source.
    /// </para>
    /// </remarks>
    public static VendorUsageSnapshot Parse(string stdout, DateTimeOffset harvestedAt)
    {
        List<VendorUsageWindow> windows = [];

        foreach (var rawLine in (stdout ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 4)
            {
                continue;
            }

            var family = fields[0].Trim();
            var window = fields[1].Trim();
            if (family.Length == 0 || window.Length == 0)
            {
                continue;
            }

            int? percentUsed = null;
            var pctText = fields[2].Trim().TrimEnd('%');
            if (int.TryParse(pctText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percentRemaining))
            {
                percentUsed = 100 - percentRemaining;
            }

            DateTimeOffset? resetsAt = DateTimeOffset.TryParse(
                fields[3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

            windows.Add(new VendorUsageWindow($"{family} · {window}", percentUsed, resetsAt, line));
        }

        return new VendorUsageSnapshot("agy", harvestedAt, Caveat: null, windows);
    }
}
