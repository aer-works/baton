using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #918 / #295: test files must route raw single-file deletes through
/// <see cref="Baton.Tests.Shared.FileCleanup"/> — the file counterpart of
/// <see cref="Baton.Tests.Shared.DirectoryCleanup"/> — so a bare delete racing a transient Windows
/// share-lock (Defender / the search indexer briefly holding a just-written file) can't flake the
/// suite. The sibling <see cref="DirectoryDeleteHygieneTests"/> enforces the same for recursive
/// directory deletes.
/// </summary>
public class FileDeleteHygieneTests
{
    [Fact]
    public void All_test_file_single_file_deletes_route_through_FileCleanup()
    {
        var testsDir = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        // Matches a static delete on the framework File type. The negative lookbehind (?<!\w) keeps it
        // from firing on an instance method whose receiver merely ends in "File" (e.g. a variable named
        // logFile), while still catching a fully-qualified System.IO.File form (the '.' before File is
        // not a word char). A FileCleanup.Delete call never matches — it carries no such substring.
        var regex = new Regex(@"(?<!\w)File\.Delete\s*\(", RegexOptions.Singleline);

        // The two files where the raw form is legitimate: the wrapper itself, and the wrapper's own test
        // (whose control arm shows a bare delete throwing under a lock the wrapper survives).
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shared/FileCleanup.cs",
            "Baton.Tests/CleanupHelpersTests.cs",
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

            var content = File.ReadAllText(filePath);
            if (regex.IsMatch(content))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found raw single-file delete call(s) in test file(s): {string.Join(", ", offenders)}. " +
            "All single-file deletes in tests must route through Baton.Tests.Shared.FileCleanup " +
            "(.Delete for finally-block teardown, .EnsureDeleted for setup) (#918) — a raw delete in a " +
            "test flakes on Windows when Defender/the indexer holds a transient handle (#295).");
    }

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
