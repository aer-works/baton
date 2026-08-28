using System.Text.RegularExpressions;

namespace Aer.Architecture.Tests;

/// <summary>
/// #466 / #1016: every spawn site in <c>src/</c> that redirects a child stream must pin its decode
/// to UTF-8. The mechanism and its cp437 derivation are recorded once, at the spawn site that was
/// actually measured — <c>ProcessVendorTurnClient</c>'s comment and the #466 diagnosis. This
/// source-level check is the deterministic regression net: the behavioral round-trip test
/// (<c>ProcessVendorTurnClientEncodingTests</c>) can only go red where the ambient console code
/// page is not already UTF-8, and the test host runs windowless where the decode falls back to
/// UTF-8 regardless — so a source scan is the instrument that fails everywhere, every time.
/// </summary>
public class RedirectedProcessEncodingTests
{
    [Fact]
    public void Every_redirected_stream_in_src_pins_its_decode_to_UTF8()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();

        // Counted, not merely present-per-file: a file with two spawn sites where only one pins the
        // encoding would pass a contains() check while still carrying the defect. Known limits of
        // the raw-text approach, stated so nobody mistakes them for guarantees (this change's
        // reviewer enumerated all three): a redirect assigned a non-literal (`= someFlag`) is not
        // counted at all; counts are per-file, so a second spawn site could in principle balance a
        // missing pin against a redundant one elsewhere in the same file; and a match inside a
        // string or comment inflates only the REQUIRED count — the false direction is a noisy
        // failure, never a silently missed defect. Every current site assigns literal `true`, one
        // site per file.
        var redirectOut = new Regex(@"RedirectStandardOutput\s*=\s*true");
        var redirectErr = new Regex(@"RedirectStandardError\s*=\s*true");
        var encodingOut = new Regex(@"StandardOutputEncoding\s*=");
        var encodingErr = new Regex(@"StandardErrorEncoding\s*=");

        foreach (var filePath in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            var relativePath = Path.GetRelativePath(srcDir, filePath).Replace('\\', '/');

            if (encodingOut.Matches(content).Count < redirectOut.Matches(content).Count)
            {
                offenders.Add($"{relativePath} (stdout)");
            }

            if (encodingErr.Matches(content).Count < redirectErr.Matches(content).Count)
            {
                offenders.Add($"{relativePath} (stderr)");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Redirected stream(s) without an explicit StandardOutputEncoding/StandardErrorEncoding in: {string.Join(", ", offenders)}. " +
            "On Windows the null-encoding default decodes the pipe with the console code page (OEM cp437 under a default console), " +
            "mangling every non-ASCII character the child emits (#466). Set both encodings to Encoding.UTF8 at the spawn site.");
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
