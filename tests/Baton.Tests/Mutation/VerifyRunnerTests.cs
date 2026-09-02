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
}
