using System.Text;
using Baton.Domain;
using Baton.Mutation;
using Baton.Tests.Shared;

namespace Baton.Tests.Mutation;

/// <summary>
/// <c>verify-results.md</c>'s exact bytes (#1882). The file is a contract with a person — the first
/// thing a reviewer reads — so it is pinned in full rather than probed with <c>Contains</c>: a
/// <c>Contains</c> assertion passes just as happily when a section header, an exit code line or the
/// timeout caveat silently disappears. <see cref="VerifyStepReport.Render"/> is a pure function of
/// already-collected results precisely so this fixture can be deterministic.
/// </summary>
public class VerifyStepReportTests
{
    private const string Preamble =
        """
        # Verify results

        The engine ran these commands before the reviewer's first turn, sequentially, with the review
        worktree as their working directory and each wrapped in `python tools/buildlock.py`. No model
        read their output: the exit codes and tails below are the engine's own capture.

        A non-zero exit does not abort the review — it is what the reviewer reads first. A command
        reported as timed out has no exit code at all: its process tree was killed at the step's
        wall-clock bound, which can mean a slow command OR a long wait for the shared build lock
        (`tools/buildlock.py` waits up to `BATON_BUILDLOCK_TIMEOUT_S`, 1800s by default, before it
        reports contention itself). Do not read a timeout as a failing build.

        Two other lines below are not command failures either, and say so where they appear: a
        command reported as blocked on the build lock never ran (the wrapper gave up waiting), and
        a command reported as not run never started at all.


        """;

    [Fact]
    public void The_rendered_document_matches_byte_for_byte()
    {
        var results = new List<VerifyCommandResult>
        {
            new("dotnet build -warnaserror", ExitCode: 0, WallClockMs: 34_300, TimedOut: false, Tail: "Build succeeded.\n    0 Warning(s)"),
            new("dotnet test --minimum-expected-tests 1", ExitCode: 1, WallClockMs: 91_002, TimedOut: false, Tail: "Failed! - Failed: 1"),
            new("python tools/gates/gates.py --selftest", ExitCode: null, WallClockMs: 600_000, TimedOut: true, Tail: ""),
        };

        var expected = Preamble
            + "## 1. `dotnet build -warnaserror`\n\n"
            + "- exit code: 0\n"
            + "- wall clock: 34300 ms\n\n"
            + "Last 200 lines of combined stdout and stderr:\n\n"
            + "```text\nBuild succeeded.\n    0 Warning(s)\n```\n\n"
            + "## 2. `dotnet test --minimum-expected-tests 1`\n\n"
            + "- exit code: 1\n"
            + "- wall clock: 91002 ms\n\n"
            + "Last 200 lines of combined stdout and stderr:\n\n"
            + "```text\nFailed! - Failed: 1\n```\n\n"
            + "## 3. `python tools/gates/gates.py --selftest`\n\n"
            + "- exit code: none (timed out; process tree killed)\n"
            + "- wall clock: 600000 ms\n\n"
            + "No output captured.\n\n";

        Assert.Equal(expected.Replace("\r\n", "\n"), VerifyStepReport.Render(results));
    }

    [Fact]
    public void An_empty_command_list_says_so_rather_than_rendering_an_empty_document()
    {
        var expected = Preamble + "No verify commands were requested for this review.\n";

        Assert.Equal(expected.Replace("\r\n", "\n"), VerifyStepReport.Render([]));
    }

    [Fact]
    public async Task Running_the_step_writes_both_the_results_file_and_the_engine_only_sidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), $"verify-step-{Guid.NewGuid():N}");
        // A real workspace carrying the wrapper: the runner refuses to launch anything without it, so
        // a fake path here would silently turn this into the refusal arm and pin the wrong bytes.
        var workspace = Path.Combine(Path.GetTempPath(), $"verify-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, "tools"));
        File.WriteAllText(Path.Combine(workspace, VerifyStepRunner.BuildLockScriptPath), "# stand-in");
        try
        {
            Assert.True(VerifyStepCommandParser.TryParse("dotnet build", out var command, out _));
            VerifyProcessLauncher launcher = (_, _, _, _, _) => Task.FromResult<(int?, string, bool)>((7, "boom", false));

            var outcome = await VerifyStep.RunAndRecordAsync(
                [command!], workspace, root, TimeSpan.FromMinutes(1), CancellationToken.None, launcher);

            var resultsPath = Path.Combine(root, VerifyStepReport.ResultsFileName);
            Assert.True(File.Exists(resultsPath));
            Assert.Equal(resultsPath, outcome.ResultsFilePath);

            // The byte count the ledger carries is the file's real size, not an estimate.
            Assert.Equal(new FileInfo(resultsPath).Length, outcome.ResultsBytes);
            Assert.Equal(Encoding.UTF8.GetByteCount(VerifyStepReport.Render(outcome.Results)), outcome.ResultsBytes);

            var sidecar = VerifyStepReport.TryReadSidecar(root);
            Assert.NotNull(sidecar);
            Assert.Equal(outcome.ResultsBytes, sidecar!.ResultsBytes);
            Assert.Equal(outcome.TotalWallClockMs, sidecar.TotalWallClockMs);
            var instrument = Assert.Single(sidecar.Commands);
            Assert.Equal("dotnet build", instrument.Command);
            Assert.Equal(7, instrument.ExitCode);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
            DirectoryCleanup.DeleteRecursively(workspace);
        }
    }

    [Fact]
    public void A_build_lock_block_and_a_command_that_never_ran_are_not_rendered_as_failures()
    {
        // The two readings a bare "- exit code: N" line loses. Both are pinned here rather than probed
        // with Contains, for the same reason the whole-document test above is: a Contains assertion
        // passes just as happily when the qualifying clause disappears and the number stays.
        var results = new List<VerifyCommandResult>
        {
            new(
                "dotnet build -warnaserror",
                ExitCode: VerifyStepReport.BuildLockBlockedExitCode,
                WallClockMs: 1_800_000,
                TimedOut: false,
                Tail: "buildlock: BLOCKED"),
            new(
                "dotnet test",
                ExitCode: null,
                WallClockMs: 0,
                TimedOut: false,
                Tail: VerifyStepRunner.MissingBuildLockReason(@"C:\other-repo")),
        };

        var expected = Preamble
            + "## 1. `dotnet build -warnaserror`\n\n"
            + "- exit code: 75 (blocked on the build lock: `tools/buildlock.py` gave up waiting for another "
            + "build and never ran this command — contention, not a failing command)\n"
            + "- wall clock: 1800000 ms\n\n"
            + "Last 200 lines of combined stdout and stderr:\n\n"
            + "```text\nbuildlock: BLOCKED\n```\n\n"
            + "## 2. `dotnet test`\n\n"
            + "- exit code: none (the command was not run)\n"
            + "- wall clock: 0 ms\n\n"
            // Not "Last 200 lines of ... stdout and stderr": nothing ran, so there is no output to
            // bound, and labelling the engine's own reason as captured output would be a fabrication.
            + "Why:\n\n"
            + "```text\n" + VerifyStepRunner.MissingBuildLockReason(@"C:\other-repo") + "\n```\n\n";

        Assert.Equal(expected.Replace("\r\n", "\n"), VerifyStepReport.Render(results));
    }

    [Fact]
    public void An_ordinary_non_zero_exit_keeps_its_bare_line_and_its_output_heading()
    {
        // The control for the test above: same renderer, an exit code that is NOT 75 and a non-null
        // one, so a pass there cannot come from a renderer that qualifies every line it prints.
        var rendered = VerifyStepReport.Render(
            [new("dotnet test", ExitCode: 74, WallClockMs: 10, TimedOut: false, Tail: "Failed!")]);

        Assert.Contains("- exit code: 74\n", rendered, StringComparison.Ordinal);
        // The preamble names the blocked case for every document, so the discriminating check is on
        // the exit-code LINE, not on the document.
        Assert.DoesNotContain("- exit code: 74 (", rendered, StringComparison.Ordinal);
        Assert.Contains("Last 200 lines of combined stdout and stderr:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_malformed_sidecar_reads_as_no_verify_step_rather_than_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"verify-step-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            Assert.Null(VerifyStepReport.TryReadSidecar(root));

            File.WriteAllText(Path.Combine(root, VerifyStepReport.SidecarFileName), "{not json");
            Assert.Null(VerifyStepReport.TryReadSidecar(root));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(root);
        }
    }

    [Fact]
    public void An_instrument_carries_the_command_exit_code_and_wall_clock_and_no_output()
    {
        // The narrowing is the contract: a verdict records what ran, not a second copy of the log.
        var instrument = new VerifyInstrument("dotnet build", 0, 34_300);

        var json = System.Text.Json.JsonSerializer.Serialize(instrument);

        Assert.Equal("""{"command":"dotnet build","exitCode":0,"wallClockMs":34300}""", json);
    }
}
