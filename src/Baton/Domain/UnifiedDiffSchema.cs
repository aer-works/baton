using System.Text;
using System.Text.RegularExpressions;

namespace Baton.Domain;

/// <summary>
/// The parse half of the unified diff schema contract (#881): turns bytes on disk into a validated
/// unified diff string or one sentence saying why they are not one.
/// <para>
/// An empty (or whitespace-only) file is valid and means "no change proposed." A reviewer or patch
/// worker that finds nothing must not have to fail its contract or fabricate a hunk, and an empty
/// patch is a clean no-op to <c>git apply</c>.
/// </para>
/// <para>
/// This validator is parse-only (decision 0043, Architecture Rule 1): it checks that non-empty
/// content matches the unified diff format (file-header pair <c>--- </c>/<c>+++ </c> followed by at
/// least one hunk header <c>@@ -n[,n] +n[,n] @@</c>). It does NOT prove that the patch applies
/// against any given tree; only <c>git apply --check</c> proves that, which is deliberately out of
/// scope here. Combined (merge) diffs, whose headers are <c>@@@</c>, are not accepted — no worker
/// produces one, and accepting a shape nothing writes would widen the floor for nothing. Neither is
/// a hunk-less diff, which is what <c>git diff -M</c> emits for a pure rename or a mode change: a
/// file whose only content is headers is also what a worker writing prose about a patch produces,
/// and this floor keeps the discrimination. A worker proposing a rename includes a hunk; a worker
/// proposing nothing writes the empty file above.
/// </para>
/// <para>
/// <b>A file header is an ADJACENT <c>--- </c>/<c>+++ </c> pair, not either line alone</b>, because a
/// deleted line is written with a leading <c>-</c>: removing the SQL/Lua/Haskell comment
/// <c>-- note</c> produces the body line <c>--- note</c>, which is indistinguishable from a header
/// on its own. Matching single lines rejected valid diffs whose later hunks followed such a
/// deletion. The residual gap is a deletion of <c>-- x</c> immediately followed by an addition of
/// <c>++ y</c>, which reads as a header pair; that is a false ACCEPT of something already shaped
/// like a diff, and this floor is not the thing that proves a patch applies.
/// </para>
/// </summary>
public static class UnifiedDiffSchema
{
    private static readonly Regex HunkHeaderRegex = new(@"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>
    /// True with non-null <paramref name="diff"/> when <paramref name="bytes"/> parse and pass the
    /// unified diff parse-only floor (or are empty/whitespace-only); false with a human-readable
    /// <paramref name="error"/> sentence otherwise.
    /// Never throws on bad content — worker-written content must land as a classified failure,
    /// not an escaped exception.
    /// </summary>
    public static bool TryParse(byte[] bytes, out string? diff, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        diff = null;

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = "The diff document is not valid UTF-8 text.";
            return false;
        }

        // A worker on Windows can write its artifact with a BOM (#466's family), which would leave
        // U+FEFF glued to the first file header and fail every prefix test below.
        text = text.TrimStart('﻿');

        if (string.IsNullOrWhiteSpace(text))
        {
            diff = text;
            error = null;
            return true;
        }

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var hasFileHeaderPair = false;
        var hunkCount = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                && i + 1 < lines.Length
                && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                hasFileHeaderPair = true;
                i++;
                continue;
            }

            if (HunkHeaderRegex.IsMatch(line))
            {
                if (!hasFileHeaderPair)
                {
                    error = "Found a hunk header without a preceding '--- '/'+++ ' file-header pair.";
                    return false;
                }

                hunkCount++;
            }
        }

        if (hunkCount == 0)
        {
            error = "No hunk header (@@ -n,n +n,n @@) found. A rename- or mode-only diff is not "
                + "accepted; include at least one hunk, or write an empty file to propose no change.";
            return false;
        }

        diff = text;
        error = null;
        return true;
    }
}
