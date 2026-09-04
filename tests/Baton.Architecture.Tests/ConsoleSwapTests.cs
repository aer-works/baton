using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1783: a test class that calls <c>Console.SetOut</c>/<c>Console.SetError</c> swaps a
/// process-global stream every other test in the same assembly shares. Left to xUnit's default
/// parallel pool, two such classes interleave — one test's swap lands between another's capture and
/// restore, and each reads the other's output (#967, #1607) — the same flake family
/// <see cref="SerializedEnvironmentTests"/> closes for <c>Environment.SetEnvironmentVariable</c>.
/// #1785 enrolled the classes known at the time into a <c>DisableParallelization</c> collection
/// (<c>ConsoleErrorCaptureCollection</c>, or an existing collection documented to cover the same
/// swap, e.g. <c>SerializedEnvironmentCollection</c> in <c>Baton.Cli.Tests</c>); nothing stopped a
/// later class from swapping a console stream without enrolling anywhere. This is the build-time
/// guard: it discovers every <c>[CollectionDefinition(..., DisableParallelization = true)]</c>
/// collection declared under <c>tests/</c>, then fails on any <c>Console.SetOut(</c>/
/// <c>Console.SetError(</c> call in a class not enrolled in one of them — enrollment in *any* such
/// collection satisfies the guard, since a class can be serialized via a collection named for an
/// unrelated purpose (the <c>Baton.Cli.Tests</c> dispatch classes are serialized through
/// <c>SerializedEnvironmentCollection</c>, not a console-specific one).
/// </summary>
public class ConsoleSwapTests
{
    [Fact]
    public void Every_console_swapping_test_class_is_enrolled_in_a_DisableParallelization_collection()
    {
        var testsDir = Path.Combine(RepoRoot(), "tests");
        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var segments = f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Any(s =>
                    s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("obj", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        // Comments stripped first so a doc comment merely describing the mechanism (e.g. this file's
        // own) can't false-positive as an unenrolled swapper or a satisfied enrollment — both
        // patterns must match real code. Read once per file since both the collection-name discovery
        // pass and the offender-detection pass need the comment-free source.
        var codeByFile = files.ToDictionary(
            f => f,
            f => string.Join('\n', File.ReadLines(f).Select(StripLineComment)));

        var definesDisableParallelizationCollection = new Regex(
            @"\[CollectionDefinition\([^\]]*DisableParallelization\s*=\s*true[^\]]*\)\]\s*public\s+(?:sealed\s+)?class\s+(\w+)",
            RegexOptions.Singleline);

        var collectionNames = codeByFile.Values
            .SelectMany(code => definesDisableParallelizationCollection.Matches(code).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.True(
            collectionNames.Count > 0,
            "Found no [CollectionDefinition(..., DisableParallelization = true)] collection under " +
            "tests/ at all -- either the scan above is broken, or every such collection (e.g. " +
            "ConsoleErrorCaptureCollection, SerializedEnvironmentCollection) was removed.");

        var enrolled = new Regex(
            @"Collection\((?:" + string.Join('|', collectionNames.Select(Regex.Escape)) + @")\.Name\)",
            RegexOptions.Singleline);
        var swaps = new Regex(@"Console\.(?:SetOut|SetError)\(", RegexOptions.Singleline);

        var offenders = new List<string>();

        foreach (var (filePath, codeOnly) in codeByFile)
        {
            if (swaps.IsMatch(codeOnly) && !enrolled.IsMatch(codeOnly))
            {
                offenders.Add(Path.GetRelativePath(testsDir, filePath).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found Console.SetOut/Console.SetError call(s) with no DisableParallelization collection " +
            $"enrollment in: {string.Join(", ", offenders)}. Add [Collection(XCollection.Name)] for a " +
            "DisableParallelization collection in the same assembly (e.g. ConsoleErrorCaptureCollection " +
            "for Console.Error, ConsoleOutCaptureCollection for Console.Out -- create one if the " +
            "assembly has neither) (#1783). An unenrolled console swapper can race a sibling class's " +
            "capture/restore of the same stream, which is the #967/#1607 flake family.");
    }

    /// <summary>
    /// Truncates a line at its <c>//</c> comment, ignoring <c>//</c> that sits inside a string
    /// literal -- mirrors <see cref="SerializedEnvironmentTests.StripLineComment"/> exactly (PR #1498
    /// review finding, re-applied here rather than shared, since the two tripwires are otherwise
    /// independent and this keeps each self-contained).
    /// </summary>
    private static string StripLineComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c == '\\' && inString)
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString && c == '/' && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
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
