using System.Xml.Linq;

namespace Baton.Architecture.Tests;

/// <summary>
/// Every test project must use xUnit <b>v3</b>, never v2. This is the guard #159 ("migrate all test
/// projects to xunit v3") never had — and its absence is exactly why the migration regressed twice:
/// <c>Baton.Workers.Dialogue.Tests</c> predated #159's close and was missed, and <c>Baton.Mcp.Tests</c>
/// was created on v2 ten days <em>after</em> #159 closed, by copying an old template that nothing
/// stopped. A one-time migration with no enforcing check is prose; this makes it a build failure.
/// <para>
/// Uniformity also keeps test execution to a single runner and analyzer set: a stray v2 project
/// reintroduces a second execution regime — its own parallelism defaults and its own analyzer
/// behaviour — so which framework discovered a test would start to determine how it runs.
/// </para>
/// </summary>
public sealed class TestFrameworkVersionTests
{
    // The v2 packages. `xunit.v3` is the allowed one. `xunit.runner.visualstudio` and
    // `xunit.analyzers` are shared by both major versions, so they are NOT markers of v2 and must not
    // be flagged — the discriminator is the v2 metapackage `xunit` and its split assemblies.
    private static readonly string[] ForbiddenV2Packages =
        ["xunit", "xunit.core", "xunit.execution", "xunit.assert"];

    [Fact]
    public void No_test_project_references_xunit_v2()
    {
        var testsRoot = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        // Scan project files AND the imported props/targets they inherit — not just *.csproj. A v2
        // reference centralized in `tests/Directory.Build.props` (auto-imported by every test project
        // via MSBuild's nearest-ancestor rule) is live in every test build but invisible to a
        // csproj-only scan, so a guard that stopped at csproj would false-PASS the exact regression
        // it exists to catch. Skip bin/obj: the restore-generated *.g.props there list every resolved
        // package (xunit v2 transitively, in a mixed tree) and would false-flag.
        var buildFiles = new[] { "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern => Directory.EnumerateFiles(testsRoot, pattern, SearchOption.AllDirectories))
            .Where(path => !IsUnderBuildOutput(path));

        foreach (var buildFile in buildFiles)
        {
            var v2Hits = XDocument.Load(buildFile)
                .Descendants("PackageReference")
                // Both `Include=` (a new reference) and `Update=` (override a centrally-defined one) —
                // `Update=` is the standard form for pinning a version in a shared props file, and a
                // scan that read only `Include=` would miss a v2 pin expressed that way.
                .SelectMany(element => new[] { (string?)element.Attribute("Include"), (string?)element.Attribute("Update") })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => ForbiddenV2Packages.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (v2Hits.Count > 0)
            {
                offenders.Add($"{Path.GetRelativePath(testsRoot, buildFile)} references v2 package(s) [{string.Join(", ", v2Hits!)}]");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Test projects must reference xunit.v3, not the xunit v2 packages (finishing #159 with the "
            + "guard it lacked). Migrate the offender(s) to xunit.v3 and pass TestContext.Current.CancellationToken "
            + "where xUnit1051 flags:\n  " + string.Join("\n  ", offenders));
    }

    // A build-output path has a `bin` or `obj` segment. Enumerating *.props under tests/ would
    // otherwise pull in restore-generated files (obj/*.nuget.g.props, obj/*.g.props) that list every
    // resolved package — including xunit v2 transitively in a mixed tree — and false-flag them.
    private static bool IsUnderBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                         || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AerFlow.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (AerFlow.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
