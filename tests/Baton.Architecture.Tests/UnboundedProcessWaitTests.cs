using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1804 — see <see cref="Baton.Tests.Shared.BoundedProcessWait"/> for the mechanism and rationale.
/// Every child-process exit wait in <c>Baton.Cli.Tests</c> must be bounded by an elapsed-time
/// timeout, not merely by external run cancellation.
/// <para>
/// Widened past the original argument-less <c>WaitForExit()</c>/<c>WaitForExitAsync()</c> check
/// (#1828 review finding): a call passing only <c>TestContext.Current.CancellationToken</c> reads
/// as bounded but is not — that token fires solely on external test-run cancellation (Ctrl-C,
/// harness teardown), never on a per-test elapsed bound, so a hung child under load hangs exactly
/// like the bare-argument-less case this check originally caught. Every genuinely bounded wait goes
/// through <see cref="Baton.Tests.Shared.BoundedProcessWait.RunToExitAsync"/> instead, or arms its
/// own local <see cref="CancellationTokenSource"/> with <c>CancelAfter</c> (no site currently does
/// this, so the allow-list below is empty).
/// </para>
/// <para>
/// Scoped to <c>Baton.Cli.Tests</c>, not every test file: that is the assembly #1804 measured
/// hanging and the one this PR's fix actually covers (claim-scope). <c>Baton.Tests</c> and
/// <c>Baton.Vendors.Tests</c> carry the same bare-<c>WaitForExit()</c> shape in several places but
/// were never observed to hang and are out of scope here — widening this check to them is future
/// work, not a claim this PR makes.
/// </para>
/// </summary>
public class UnboundedProcessWaitTests
{
    // Files allowed to contain a flagged pattern despite it, each entry justified inline. Currently
    // empty: LiveCancelRequestChannelEndToEndTests.cs's one raw WaitForExitAsync call passes a
    // CancellationTokenSource armed with its own local CancelAfter, not
    // TestContext.Current.CancellationToken directly, so it never matches either regex below and
    // needs no entry here.
    private static readonly IReadOnlyDictionary<string, string> AllowedOffenders =
        new Dictionary<string, string>();

    [Fact]
    public void All_Baton_Cli_Tests_process_waits_carry_an_elapsed_time_bound()
    {
        var cliTestsDir = Path.Combine(RepoRoot(), "tests", "Baton.Cli.Tests");
        var argumentLessRegex = new Regex(@"\bWaitForExit(Async)?\s*\(\s*\)", RegexOptions.Singleline);
        var tokenOnlyRegex = new Regex(
            @"\bWaitForExitAsync\s*\(\s*TestContext\.Current\.CancellationToken\s*\)", RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(cliTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(cliTestsDir, filePath).Replace('\\', '/');
            if (AllowedOffenders.ContainsKey(relativePath))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            if (argumentLessRegex.IsMatch(content) || tokenOnlyRegex.IsMatch(content))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found child-process exit wait(s) with no elapsed-time bound in Baton.Cli.Tests file(s): {string.Join(", ", offenders)}. " +
            "Every process wait in a Baton.Cli.Tests file must be bounded by elapsed time — route it through " +
            "Baton.Tests.Shared.BoundedProcessWait.RunToExitAsync, or arm a local CancellationTokenSource with " +
            "CancelAfter and add an entry to AllowedOffenders with a justification (#1804, #1828).");
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
