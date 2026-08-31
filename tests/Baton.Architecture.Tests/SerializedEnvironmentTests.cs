using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1491: a test class that calls <c>Environment.SetEnvironmentVariable</c> flips a value the whole
/// process shares, and a handful of production call sites (<c>BatonPaths.Root</c>, the lookups behind
/// the shipped worker-role and workflow-template catalogs) go back to that shared value fresh on every
/// call instead of caching it once. Left to xUnit's default parallel pool, a mutator's edit can land in
/// the middle of one of those lookups running in an unrelated class, or have its own cleanup overtaken
/// by a sibling's edit — the #1480 flake family. Enrolling every such class in the per-assembly
/// <c>SerializedEnvironmentCollection</c> closes it structurally; this test is the build-time guard
/// that a class added later can't skip that enrollment and quietly reopen the same failure mode.
/// </summary>
public class SerializedEnvironmentTests
{
    [Fact]
    public void Every_env_mutating_test_class_is_enrolled_in_SerializedEnvironmentCollection()
    {
        var testsDir = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        var mutates = new Regex(@"Environment\.SetEnvironmentVariable", RegexOptions.Singleline);
        var enrolled = new Regex(@"SerializedEnvironmentCollection\.Name", RegexOptions.Singleline);

        // The one file allowed to mutate without enrollment: the module initializer that redirects
        // BATON_HOME once at assembly load, before any test runs and so before any collection could
        // matter.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shared/BatonHomeRedirect.cs",
        };

        foreach (var filePath in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(testsDir, filePath).Replace('\\', '/');
            if (allowed.Contains(relativePath))
            {
                continue;
            }

            // Comments stripped first so a doc comment merely describing the mechanism (e.g. this
            // file's own, or SerializedEnvironmentCollection.cs's) can't false-positive as an
            // unenrolled mutator or a satisfied enrollment — both patterns must match real code.
            var codeOnly = string.Join(
                '\n',
                File.ReadLines(filePath).Select(line =>
                {
                    var commentStart = line.IndexOf("//", StringComparison.Ordinal);
                    return commentStart < 0 ? line : line[..commentStart];
                }));

            if (mutates.IsMatch(codeOnly) && !enrolled.IsMatch(codeOnly))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found Environment.SetEnvironmentVariable call(s) with no SerializedEnvironmentCollection " +
            $"enrollment in: {string.Join(", ", offenders)}. Add " +
            "[Collection(SerializedEnvironmentCollection.Name)] to each offending class (#1491) — an " +
            "unenrolled env mutator can race a production reader that re-reads the same variable on " +
            "every access, which is the #1480 flake family.");
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
