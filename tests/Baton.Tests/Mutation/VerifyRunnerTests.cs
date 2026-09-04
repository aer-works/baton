using Baton.Mutation;
using Xunit;

namespace Baton.Tests.Mutation;

/// <summary>
/// Coverage for <see cref="VerifyRunner"/> (#1623) against a fake command, via its internal
/// program/args seam — never a real, minutes-long <c>pixi run gates-quiet</c>.
/// </summary>
public sealed class VerifyRunnerTests
{
    [Fact]
    public async Task An_exit_zero_command_reports_Passed()
    {
        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", "exit 0"], workingDirectory: null, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Null(outcome.FailingMembers);
        Assert.Null(outcome.Tail);
    }

    [Fact]
    public async Task A_nonzero_exit_reports_Failed_with_the_captured_output_as_the_tail()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo something went wrong & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("something went wrong", outcome.Tail);
    }

    [Fact]
    public async Task A_GATES_FAIL_summary_line_yields_the_named_failing_members()
    {
        // The exact shape tools/gates/gates.py's summarise() emits.
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: FAIL 2 of 25 -- fmt-check, lint & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.FailingMembers);
        Assert.Equal(["fmt-check", "lint"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_failure_with_no_recognizable_summary_line_leaves_FailingMembers_null_not_fabricated()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo unstructured failure output & exit 1"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.Null(outcome.FailingMembers);
    }

    [Fact]
    public async Task A_long_failure_tail_is_bounded_not_a_full_log_dump()
    {
        // "echo" on cmd.exe fits comfortably under the 4000-char tail bound in one line, so build a
        // multi-line failure via several chained echoes instead.
        var chunk = new string('x', 500);
        var command = string.Join(" & ", Enumerable.Repeat($"echo {chunk}", 12)) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.NotNull(outcome.Tail);
        Assert.True(outcome.Tail!.Length <= 4000);
    }

    [Fact]
    public async Task The_tail_is_the_failing_members_own_block_not_a_blind_whole_stream_tail()
    {
        // Mirrors tools/gates/gates.py's own per-member marker line shape: "  pass  name  (exit 0)"
        // / "  FAIL  name  (exit 1)", one after each member's own output. gate-b's own diagnostic
        // text ("GATE_B_UNIQUE_FAILURE_TEXT") sits well before a long run of trailing pass markers
        // and padding -- a blind tail of the whole stream (the pre-#1701 behavior) would have cut
        // it, since the padding after gate-b's marker line alone exceeds the 4000-char tail bound.
        var padding = new string('p', 200);
        var trailingPadding = string.Join(" & ", Enumerable.Repeat($"echo {padding}", 25));
        var command = string.Join(" & ", new[]
        {
            "echo   pass  gate-a  (exit 0)",
            "echo GATE_B_UNIQUE_FAILURE_TEXT",
            "echo   FAIL  gate-b  (exit 1)",
            trailingPadding,
            "echo   pass  gate-c  (exit 0)",
            "echo GATES: FAIL 1 of 3 -- gate-b",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-b"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("GATE_B_UNIQUE_FAILURE_TEXT", outcome.Tail);
        Assert.DoesNotContain(padding, outcome.Tail);
    }

    [Fact]
    public async Task Two_failing_members_own_blocks_are_both_present_and_the_joined_total_still_bounded()
    {
        // #1701 review: a naive per-member cap (each independently allowed the full MaxTailChars)
        // would let N failing members yield N times the intended bound. Two large failing blocks must
        // still fit inside one MaxTailChars-sized joined result, and each contributes its OWN tail
        // text, not just whichever member happened to be captured last.
        var bigBlock = new string('x', 3000);
        var command = string.Join(" & ", new[]
        {
            $"echo {bigBlock}",
            "echo GATE_A_TEXT",
            "echo   FAIL  gate-a  (exit 1)",
            $"echo {bigBlock}",
            "echo GATE_B_TEXT",
            "echo   FAIL  gate-b  (exit 1)",
            "echo GATES: FAIL 2 of 2 -- gate-a, gate-b",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-a", "gate-b"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.True(outcome.Tail!.Length <= 4000, $"joined tail was {outcome.Tail.Length} chars, want <= 4000");
        Assert.Contains("GATE_A_TEXT", outcome.Tail);
        Assert.Contains("GATE_B_TEXT", outcome.Tail);
    }

    [Fact]
    public async Task A_marker_line_present_with_no_matching_FAIL_entry_falls_back_to_the_whole_stream_tail()
    {
        // Shape drift: the summary line named a failing member, but no per-member marker line for
        // that name is anywhere in the output (e.g. gates.py changed its own summary vocabulary).
        // Must degrade to the pre-#1701 whole-stream tail, never silently return an empty Tail.
        var command = "echo   pass  gate-a  (exit 0) & echo GATES: FAIL 1 of 2 -- gate-missing & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(["gate-missing"], outcome.FailingMembers);
        Assert.NotNull(outcome.Tail);
        Assert.Contains("GATES: FAIL 1 of 2 -- gate-missing", outcome.Tail);
    }

    [Fact]
    public async Task A_GATES_BLOCKED_summary_line_yields_BuildLockBusy_and_the_blocked_members()
    {
        // #1796: gates.py's summarise() shape for an all-blocked run.
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: BLOCKED 1 of 25 -- lint & exit 3"], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(["lint"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_mixed_BLOCKED_and_FAIL_run_reports_GatesFailed_naming_only_the_real_failures()
    {
        // gates.py's own precedence: a real failure alongside a blocked member still headlines
        // "GATES: FAIL ..." naming only the real failure, never the blocked one -- the mixed case
        // must settle VerifyFailed, not VerifyNotRun/BuildLockBusy.
        var command = string.Join(" & ", new[]
        {
            "echo   BLOCKED  lint  (exit 75)",
            "echo   FAIL  fmt-check  (exit 2)",
            "echo GATES: FAIL 1 of 25 -- fmt-check",
        }) + " & exit 1";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.GatesFailed, outcome.Kind);
        Assert.Equal(["fmt-check"], outcome.FailingMembers);
    }

    [Fact]
    public async Task A_BLOCKED_member_s_own_buildlock_line_yields_the_NotRunReason_text()
    {
        // #1796: what that member actually prints to stdout, verbatim.
        var command = string.Join(" & ", new[]
        {
            "echo buildlock: BLOCKED after 1800s waiting for the build lock held by PID 1234 (dotnet build) since 2026-09-04 01:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  lint  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- lint",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.NotNull(outcome.NotRunReason);
        Assert.Contains("build lock busy for 1800s", outcome.NotRunReason);
        Assert.Contains("PID 1234 (dotnet build) since 2026-09-04 01:00:00", outcome.NotRunReason);
    }

    [Fact]
    public async Task A_holder_whose_own_command_line_carries_double_dash_flags_is_named_whole_not_truncated()
    {
        // #1813 review: the holder text embeds the wrapped command verbatim, and vendor-check's is the
        // worst case -- a standalone `--` token AND a `--check` flag. The capture must run to
        // buildlock.py's trailing sentinel, never stop at the first ` --` inside the command.
        var command = string.Join(" & ", new[]
        {
            "echo buildlock: BLOCKED after 1800s waiting for the build lock held by PID 4242 (dotnet run --project tools/Baton.VendorProbe -- --check) since 2026-09-04 02:00:00 -- raise BATON_BUILDLOCK_TIMEOUT_S or find out why the holder is stuck",
            "echo   BLOCKED  vendor-check  (exit 75)",
            "echo GATES: BLOCKED 1 of 25 -- vendor-check",
        }) + " & exit 3";

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", command], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Equal(
            "build lock busy for 1800s (holder: PID 4242 (dotnet run --project tools/Baton.VendorProbe -- --check) since 2026-09-04 02:00:00)",
            outcome.NotRunReason);
    }

    [Fact]
    public async Task A_BLOCKED_verdict_with_no_recognizable_buildlock_line_leaves_NotRunReason_null_not_fabricated()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "echo GATES: BLOCKED 1 of 25 -- lint & exit 3"], workingDirectory: null, CancellationToken.None);

        Assert.Equal(Baton.Domain.VerifyFailedKind.BuildLockBusy, outcome.Kind);
        Assert.Null(outcome.NotRunReason);
    }

    [Fact]
    public async Task An_unspawnable_program_reports_Failed_rather_than_throwing()
    {
        var outcome = await VerifyRunner.RunProcessAsync(
            "this-program-does-not-exist-anywhere", [], workingDirectory: null, CancellationToken.None);

        Assert.False(outcome.Passed);
        Assert.NotNull(outcome.Tail);
    }

    [Fact]
    public async Task Cancellation_during_verify_reports_Cancelled_kind()
    {
        // Found while fixing #1706 (its own issue, fixed here per CLAUDE.md's found-while-fixing rule):
        // this raced `cmd /c exit 0` against an already-cancelled token and asserted the token won.
        // On an idle machine it does; inside `pixi run gates-quiet`, with five test assemblies and a
        // build competing for cores, the child sometimes exited 0 first and the run reported Passed --
        // a flake that fails the whole gate for a reason unrelated to whatever change is being gated.
        // A command that cannot finish first removes the race rather than widening a timing window:
        // the outcome is now decided by the cancellation alone, which is the only thing this test is
        // about. Windows-only shape, matching every other `cmd` fixture in this file and CI's
        // Windows-only posture (#1405).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await VerifyRunner.RunProcessAsync(
            "cmd", ["/c", "ping -n 30 127.0.0.1 >nul"], workingDirectory: null, cts.Token);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
    }

    [Fact]
    public async Task A_pre_cancelled_token_never_launches_the_process()
    {
        // #1722: the actual production race was a FAST child (`cmd /c exit 0`) exiting before the
        // cancellation was observed, so a marker file written by that same child is not a reliable
        // instrument here -- even on unpatched code the child is USUALLY killed fast enough that the
        // marker is never written, even though the process still launched. A wall-clock threshold is
        // not the instrument either: this file already records one load flake of that shape (see the
        // comment on Cancellation_during_verify_reports_Cancelled_kind). The pre-spawn guard is the only
        // arm that can produce this exact Tail, and it textually precedes RunProcessAsync's sole
        // CaptureAsync call, so pinning the Tail pins "never launched" by construction, deterministically:
        // on unpatched code the outcome is either Pass (Tail null) or the BatonCancelException arm's
        // "Verify command cancelled: ..." text, never this string.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await VerifyRunner.RunProcessAsync("cmd", ["/c", "exit 0"], workingDirectory: null, cts.Token);

        Assert.False(outcome.Passed);
        Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
        Assert.Equal("Verify command cancelled before it was launched.", outcome.Tail);
    }

    [Fact]
    public async Task Cancellation_during_a_still_running_child_reports_Cancelled_and_kills_the_tree()
    {
        // #1722: cancel WHILE a long-running child is mid-flight (rather than pre-cancelling), and
        // prove the tree is actually killed rather than orphaned -- a second command chained after
        // the long-running one would create a marker file if the child were left to finish, which
        // must never happen once the token fires.
        var markerPath = Path.Combine(Path.GetTempPath(), $"baton-1722-{Guid.NewGuid():N}.marker");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));

            var outcome = await VerifyRunner.RunProcessAsync(
                "cmd",
                ["/c", $"ping -n 10 127.0.0.1 >nul & echo marker > \"{markerPath}\" & exit 0"],
                workingDirectory: null,
                cts.Token);

            Assert.False(outcome.Passed);
            Assert.Equal(Baton.Domain.VerifyFailedKind.Cancelled, outcome.Kind);
            Assert.False(File.Exists(markerPath), "the marker file exists, so the child tree kept running (and exited 0) past cancellation instead of being killed");
        }
        finally
        {
            Baton.Tests.Shared.FileCleanup.Delete(markerPath);
        }
    }
}
