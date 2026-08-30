using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #438 / #295: test files must route raw recursive deletes through
/// <see cref="Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively"/> to prevent Windows transient file lock flakes.
/// </summary>
public class DirectoryDeleteHygieneTests
{
    [Fact]
    public void All_test_file_recursive_deletes_route_through_DirectoryCleanup()
    {
        var testsDir = Path.Combine(RepoRoot(), "tests");
        var offenders = new List<string>();

        // Keys on the `recursive: true` NAMED form, which this repo uses uniformly. A positional
        // `Directory.Delete(x, true)` would evade it — but matching `, true)` positionally would also
        // false-flag a nested `Foo(a, true)` argument inside a legitimate `recursive: false` call, so
        // the named form is the precise discriminator here. Keyed on `true`, so the `recursive: false`
        // junction-delete in MemoryProposalApplierTests is correctly NOT flagged.
        var regex = new Regex(@"Directory\.Delete\s*\([^;]*recursive\s*:\s*true", RegexOptions.Singleline);

        foreach (var filePath in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(testsDir, filePath).Replace('\\', '/');
            if (string.Equals(relativePath, "Shared/DirectoryCleanup.cs", StringComparison.OrdinalIgnoreCase))
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
            $"Found raw recursive Directory.Delete call(s) in test file(s): {string.Join(", ", offenders)}. " +
            "All raw recursive deletes in tests must route through Baton.Tests.Shared.DirectoryCleanup.DeleteRecursively (#438), " +
            "because a raw recursive delete in a test flakes on Windows when Defender/the indexer holds a transient handle (#295).");
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
