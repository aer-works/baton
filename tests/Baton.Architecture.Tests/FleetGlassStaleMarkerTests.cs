using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1656 / #1549: <c>glass.html</c>'s per-room age line used to key the stale marker (⚠) on
/// journal-event age alone, false-firing on a healthy long-running lane. Full measurement and
/// rationale: spec/baton.md §6, "The false Running ⚠".
/// <para>
/// There is no JS test runner in this repo for <c>tools/fleet-glass/glass.html</c> (a pre-existing
/// gap, not one this fix introduces) -- these are source-shape checks in the same style
/// <see cref="FleetGlassReadOnlyTests"/> already uses, not a behavioral execution of the script.
/// </para>
/// </summary>
public class FleetGlassStaleMarkerTests
{
    private static string GlassSource()
    {
        var root = RepoRoot();
        var glassPath = Path.Combine(root, "tools", "fleet-glass", "glass.html");
        Assert.True(File.Exists(glassPath), "glass.html must exist at tools/fleet-glass/glass.html");
        return File.ReadAllText(glassPath);
    }

    [Fact]
    public void AgeLine_keys_the_stale_marker_on_live_activity_before_journal_age()
    {
        var html = GlassSource();

        var isStaleMatch = Regex.Match(
            html,
            @"const\s+isStale\s*=\s*room\.state\s*===\s*""Running""\s*&&\s*stalenessBasis\s*&&\s*\(Date\.now\(\)\s*-\s*Date\.parse\(stalenessBasis\)\)\s*>\s*15\s*\*\s*60000;");
        Assert.True(isStaleMatch.Success,
            "glass.html's ageLine must gate the ⚠ on a `stalenessBasis` variable compared against a 15-minute threshold.");

        var basisMatch = Regex.Match(
            html,
            @"const\s+liveActivityAt\s*=\s*room\.live\s*&&\s*room\.live\.lastActivityAt;\s*\n\s*const\s+stalenessBasis\s*=\s*liveActivityAt\s*\|\|\s*t;");
        Assert.True(basisMatch.Success,
            "glass.html must derive `stalenessBasis` from `room.live.lastActivityAt` first, falling back to the journal-event age (`t`) only when `live` is absent.");
    }

    [Fact]
    public void AgeLine_no_longer_keys_the_stale_marker_on_journal_age_alone()
    {
        var html = GlassSource();

        // The pre-#1656 shape: `t && (Date.now()-Date.parse(t)) > 15*60000` directly inside the
        // ageLine ternary, with no live-activity fallback at all.
        var preFixShape = Regex.IsMatch(
            html,
            @"room\.state\s*===\s*""Running""\s*&&\s*t\s*&&\s*\(Date\.now\(\)-Date\.parse\(t\)\)\s*>\s*15\*60000\s*\?\s*""\s*⚠""");
        Assert.False(preFixShape,
            "glass.html must not have regressed to keying the ⚠ on journal-event age (`t`) alone.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
