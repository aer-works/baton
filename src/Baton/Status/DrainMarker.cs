using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Status;

/// <summary>
/// The read side of the tool-refresh drain marker (#1645, operator ruling 2026-09-02): while
/// <c>tools/tool-refresh/refresh.py</c> refreshes the installed <c>baton</c> global tool it writes
/// <see cref="BatonPaths.DrainMarkerFile"/>, and this type is what makes a verb refuse under it. The
/// contract itself — which verbs, what the refusal leaves behind, why waiting alone is not enough — is
/// <c>spec/baton.md</c>'s C-10 entry, not restated here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the population is what it is</b> (the list itself is C-10's, not restated here).
/// <c>baton status</c> is the one exclusion that is load-bearing rather than a judgement call:
/// refresh.py's own drain predicate shells out to <c>baton status &lt;room&gt; --json</c> to decide
/// whether a room is live, so refusing there would deadlock the tool against its own marker. The other
/// non-refusing verbs — <c>cancel</c>/<c>decide</c>/<c>supply</c>, and <c>run</c>, which does start an
/// engine — act on work an operator already has in hand, and the ruling drew the line at the verbs that
/// start a lane. Widening it is an operator call, not an implementation detail: note that
/// <c>TerminalSentinelEndToEndTests</c> spawns <c>baton run</c> subprocesses with no isolated
/// <c>BATON_HOME</c>, so adding <c>run</c> would need that suite isolated too (see
/// <c>IsolatedBatonHome</c>).
/// </para>
/// <para>
/// <b>Fail closed.</b> A marker that exists but cannot be read or parsed still refuses: a partially
/// written file must not read as an open gate. The only "absent" verdicts are the file not existing and
/// the file vanishing mid-read (refresh.py's <c>finally</c> removing it while this read was in flight,
/// which is the refresh finishing — precisely the case where starting is fine).
/// </para>
/// <para>
/// <b>Not a lock, and not crash-proof.</b> refresh.py removes the marker in a <c>finally</c>, which
/// covers a failed step, an exception and Ctrl-C — but not a machine losing power or the interpreter
/// being killed outright. That is why <c>pixi run tool-refresh --abort</c> exists and why every refusal
/// message names it: a stale marker is cleared by hand, not waited out.
/// </para>
/// </remarks>
public static class DrainMarker
{
    /// <summary>What an operator (or an agent reading a refusal) runs to clear a stale marker.</summary>
    public const string AbortInvocation = "pixi run tool-refresh --abort";

    /// <summary>
    /// The refusal text for <paramref name="verb"/> when a marker is present, or <c>null</c> when the
    /// path is clear. Callers turn a non-null result into their own typed refusal — this type never
    /// throws and never decides an exit code.
    /// </summary>
    public static string? RefusalMessage(string verb)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);

        var path = BatonPaths.DrainMarkerFile;
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the Exists check and the read: the refresh finished. Starting is fine.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal(verb, path, $"the marker exists but could not be read ({ex.GetType().Name})");
        }

        Contents? contents;
        try
        {
            contents = JsonSerializer.Deserialize<Contents>(text, SerializerOptions);
        }
        catch (JsonException)
        {
            contents = null;
        }

        var detail = contents is null
            ? "the marker is present but unreadable as JSON (treated as a live drain, fail closed)"
            : Describe(contents);
        return Refusal(verb, path, detail);
    }

    private static string Refusal(string verb, string path, string detail) =>
        $"Refusing to start `baton {verb}`: a tool-refresh drain is in progress — {detail}. "
        + $"The marker is {path}. Wait for the refresh to finish and re-run, or clear a stale marker "
        + $"with `{AbortInvocation}`.";

    private static string Describe(Contents contents)
    {
        var reason = string.IsNullOrWhiteSpace(contents.Reason) ? "no reason recorded" : contents.Reason;
        var since = string.IsNullOrWhiteSpace(contents.Since) ? "an unrecorded time" : contents.Since;
        var pid = contents.Pid is { } p ? $", pid {p}" : string.Empty;
        return $"{reason}, since {since}{pid}";
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The marker's shape. refresh.py's module docstring is the canonical statement of what it writes;
    /// this record is the reader's mirror of it, and every field is optional on purpose — a marker whose
    /// fields this reader cannot make sense of still refuses (see the type remarks), so nothing here is
    /// load-bearing beyond what the refusal line prints.
    /// </summary>
    private sealed record Contents(
        [property: JsonPropertyName("since")] string? Since,
        [property: JsonPropertyName("pid")] int? Pid,
        [property: JsonPropertyName("reason")] string? Reason);
}
