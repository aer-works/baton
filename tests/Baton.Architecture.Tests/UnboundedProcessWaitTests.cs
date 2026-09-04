using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1804 — see <see cref="Baton.Tests.Shared.BoundedProcessWait"/> for the mechanism and rationale.
/// Every <c>WaitForExit()</c>/<c>WaitForExitAsync()</c> call in <c>Baton.Cli.Tests</c> must carry
/// either a timeout argument or a <see cref="CancellationToken"/>.
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
    [Fact]
    public void All_Baton_Cli_Tests_process_waits_carry_a_timeout_or_cancellation_token()
    {
        var cliTestsDir = Path.Combine(RepoRoot(), "tests", "Baton.Cli.Tests");
        var regex = new Regex(@"\bWaitForExit(Async)?\s*\(\s*\)", RegexOptions.Singleline);
        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(cliTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            if (regex.IsMatch(content))
            {
                offenders.Add(Path.GetRelativePath(cliTestsDir, filePath).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found raw, argument-less WaitForExit()/WaitForExitAsync() call(s) in Baton.Cli.Tests file(s): {string.Join(", ", offenders)}. " +
            "Every process wait in a Baton.Cli.Tests file must pass a timeout (WaitForExit(TimeSpan)) or a CancellationToken " +
            "(WaitForExitAsync(CancellationToken)) — see Baton.Tests.Shared.BoundedProcessWait (#1804).");
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
