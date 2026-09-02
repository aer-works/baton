using System.Text.RegularExpressions;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// #1645 item 2: the front-door half of the tool-refresh ask. <c>tools/tool-refresh</c> (item 1) fixes
/// drift once a human runs it; this is what makes drift visible without anyone having to think to
/// check — a loud one-line WARN from <c>baton dispatch</c>/<c>baton status</c> themselves, the moment a
/// repo checkout is discoverable and its packaged version has moved past what is actually installed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thinner than <see cref="Baton.VendorProbe.DriftGrace"/>'s grace-window/bookkeeping
/// shape (<c>tools/Baton.VendorProbe/DriftGrace.cs</c>): that machinery exists because a vendor CLI can
/// self-update behind the operator's back, so a fresh drift needs a clock before it can fairly become
/// fatal. A repo checkout does not move on its own — the same running process reads it fresh on every
/// invocation — so there is nothing to time out and no bookkeeping file to keep. The verdict shape this
/// borrows instead is <c>Baton.VendorProbe.Staleness.Check</c>'s: current / drifted / not-inspectable,
/// read fresh every time.
/// </para>
/// <para>
/// Never touches an exit code. A stale installed tool is real friction (the 2026-09-01 conductor ran
/// 0.25.0 all afternoon while five PRs merged — see the issue's "Measured friction"), but it is not a
/// reason to refuse to dispatch or report status; the WARN is the whole intervention.
/// </para>
/// </remarks>
public static class InstalledVersionDrift
{
    /// <summary>
    /// The env var <see cref="BatonEnvironmentSnapshot.RepoOverride"/> is captured from — the other half
    /// of "discoverable via env var or <c>--repo</c>" from the issue's ask. Named on this side (not
    /// <c>Baton.Status</c>) because that project sits upstream of this one; see
    /// <see cref="BatonEnvironmentSnapshot"/>'s own remarks on why its capture duplicates this literal
    /// rather than referencing it.
    /// </summary>
    public const string RepoEnvironmentVariable = "BATON_REPO";

    private static readonly Regex VersionElement = new(
        @"<Version>\s*(?<version>[^<\s]+)\s*</Version>", RegexOptions.Compiled);

    /// <summary>Where the repo's release version is recorded — the same value MSBuild stamps into the
    /// assembly <c>baton --version</c> reports, per <see cref="VersionInfo"/>'s own doc comment.</summary>
    private const string VersionPropsRelativePath = "src/Baton.Cli/Directory.Build.props";

    public enum Verdict
    {
        /// <summary>No checkout path was supplied and none is discoverable via the env var. Not a check.</summary>
        NoRepoDiscoverable,

        /// <summary>A checkout path is known, but its version file could not be read or parsed. Not a check.</summary>
        Unreadable,

        /// <summary>Installed matches the checkout's current release version.</summary>
        Current,

        /// <summary>Installed is older than the checkout's current release version — the WARN case.</summary>
        Behind,

        /// <summary>Installed is newer than the checkout (an unreleased/dev build). Not a problem this warns about.</summary>
        Ahead,
    }

    public sealed record Result(Verdict Verdict, string? RepoVersion, string InstalledVersion)
    {
        public string? WarnLine() => Verdict != Verdict.Behind
            ? null
            : $"WARN: installed baton {InstalledVersion} is behind this checkout's {RepoVersion} — "
              + "run `pixi run tool-refresh` (or, by hand: `pixi run pack` then "
              + "`dotnet tool install --global --add-source bin/pack baton` after uninstalling the old one).";
    }

    /// <param name="repoPath">
    /// <c>--repo</c>'s value, or null to fall back to <see cref="BatonEnvironmentSnapshot.RepoOverride"/>
    /// (<c>BATON_REPO</c>). Neither present is the ordinary case for an operator who dispatches against
    /// some other project's checkout — this is not a check that can run everywhere.
    /// </param>
    /// <param name="installedVersion">
    /// What is actually running — <see cref="VersionInfo.GetVersion"/> against this process's own
    /// assembly in production; a fixed string in a test.
    /// </param>
    public static Result Evaluate(string? repoPath, string installedVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(installedVersion);

        var resolvedRepo = repoPath is { Length: > 0 } ? repoPath : BatonEnvironmentSnapshot.Current.RepoOverride;
        if (string.IsNullOrWhiteSpace(resolvedRepo))
        {
            return new Result(Verdict.NoRepoDiscoverable, null, installedVersion);
        }

        var propsPath = Path.Combine(resolvedRepo, VersionPropsRelativePath);
        string? repoVersion;
        try
        {
            repoVersion = ReadVersion(propsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(Verdict.Unreadable, null, installedVersion);
        }

        if (repoVersion is null)
        {
            return new Result(Verdict.Unreadable, null, installedVersion);
        }

        if (string.Equals(repoVersion, installedVersion, StringComparison.Ordinal))
        {
            return new Result(Verdict.Current, repoVersion, installedVersion);
        }

        if (!Version.TryParse(repoVersion, out var repoV) || !Version.TryParse(installedVersion, out var installedV))
        {
            // Neither a clean semver-ish string nor an exact match -- report it, but not as "Behind":
            // warning on a comparison this method cannot actually make would be worse than staying quiet.
            return new Result(Verdict.Unreadable, repoVersion, installedVersion);
        }

        return new Result(installedV < repoV ? Verdict.Behind : Verdict.Ahead, repoVersion, installedVersion);
    }

    private static string? ReadVersion(string propsPath)
    {
        if (!File.Exists(propsPath))
        {
            return null;
        }

        var match = VersionElement.Match(File.ReadAllText(propsPath));
        return match.Success ? match.Groups["version"].Value : null;
    }
}
