using Baton.Status;

namespace Baton.Cli.Tests;

/// <summary>
/// #1645 item 2: <see cref="InstalledVersionDrift"/> is the one evaluator both <c>baton dispatch</c>
/// and <c>baton status</c> call before printing their (non-fatal) drift WARN. These pin the verdict
/// table directly, independent of either command's own stderr wiring.
/// </summary>
public sealed class InstalledVersionDriftTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"drift-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            DirectoryCleanup.DeleteRecursively(_tempRoot);
        }
    }

    private string WriteRepoWithVersion(string version)
    {
        var propsDir = Path.Combine(_tempRoot, "src", "Baton.Cli");
        Directory.CreateDirectory(propsDir);
        File.WriteAllText(
            Path.Combine(propsDir, "Directory.Build.props"),
            $"<Project>\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n");
        return _tempRoot;
    }

    [Fact]
    public void No_repo_argument_and_no_env_override_reports_NoRepoDiscoverable()
    {
        using var scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank);

        var result = InstalledVersionDrift.Evaluate(repoPath: null, installedVersion: "1.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.NoRepoDiscoverable, result.Verdict);
        Assert.Null(result.WarnLine());
    }

    [Fact]
    public void An_older_installed_version_is_Behind_and_warns_with_both_versions()
    {
        var repo = WriteRepoWithVersion("2.0.0");

        var result = InstalledVersionDrift.Evaluate(repo, "1.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.Behind, result.Verdict);
        var warning = result.WarnLine();
        Assert.NotNull(warning);
        Assert.Contains("1.0.0", warning, StringComparison.Ordinal);
        Assert.Contains("2.0.0", warning, StringComparison.Ordinal);
        Assert.Contains("tool-refresh", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_installed_version_is_Current_and_never_warns()
    {
        var repo = WriteRepoWithVersion("2.0.0");

        var result = InstalledVersionDrift.Evaluate(repo, "2.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.Current, result.Verdict);
        Assert.Null(result.WarnLine());
    }

    [Fact]
    public void A_newer_installed_version_is_Ahead_and_never_warns()
    {
        // A dev build of `baton` run against an older checkout is not the drift this exists to catch.
        var repo = WriteRepoWithVersion("1.0.0");

        var result = InstalledVersionDrift.Evaluate(repo, "2.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.Ahead, result.Verdict);
        Assert.Null(result.WarnLine());
    }

    [Fact]
    public void A_repo_path_with_no_Directory_Build_props_is_Unreadable_and_never_warns()
    {
        Directory.CreateDirectory(_tempRoot);

        var result = InstalledVersionDrift.Evaluate(_tempRoot, "1.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.Unreadable, result.Verdict);
        Assert.Null(result.WarnLine());
    }

    [Fact]
    public void The_BATON_REPO_env_override_is_used_when_no_explicit_repo_path_is_passed()
    {
        var repo = WriteRepoWithVersion("3.0.0");
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RepoOverride = repo });

        var result = InstalledVersionDrift.Evaluate(repoPath: null, installedVersion: "1.0.0");

        Assert.Equal(InstalledVersionDrift.Verdict.Behind, result.Verdict);
        Assert.Equal("3.0.0", result.RepoVersion);
    }

    [Fact]
    public void An_explicit_repo_path_wins_over_the_env_override()
    {
        var explicitRepo = WriteRepoWithVersion("5.0.0");
        using var scope = BatonEnvironmentSnapshot.BeginScope(
            BatonEnvironmentSnapshot.Blank with { RepoOverride = "some-other-path-that-is-never-read" });

        var result = InstalledVersionDrift.Evaluate(explicitRepo, "1.0.0");

        Assert.Equal("5.0.0", result.RepoVersion);
    }
}
