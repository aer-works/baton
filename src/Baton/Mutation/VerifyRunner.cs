using System.Text.RegularExpressions;
using Baton.Core;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// The result of one <see cref="VerifyRunner.RunProcessAsync"/> call (#1623). <see cref="Passed"/>
/// mirrors the verify command's own exit code EXCEPT when the caller's token is cancelled, which
/// always wins regardless of what the child's exit code happened to be (#1722) — a cancellation
/// observed after a fast child's natural exit is otherwise a fail-open race, not a flaky test.
/// <see cref="FailingMembers"/>/<see cref="Tail"/> are populated only when it fails, and only when the
/// summary line's shape is recognized — never fabricated. <see cref="Tail"/> is each failing member's
/// OWN output (#1701), not a blind tail of the whole combined stream — see
/// <see cref="VerifyRunner.BuildTail"/>.
/// <see cref="Kind"/> distinguishes gate breakage from timeouts, cancellations, or engine restarts (F3).
/// </summary>
public sealed record VerifyOutcome(
    bool Passed,
    IReadOnlyList<string>? FailingMembers = null,
    string? Tail = null,
    VerifyFailedKind? Kind = null)
{
    public static readonly VerifyOutcome Pass = new(true);
}

/// <summary>
/// The engine-run verify step's own primitive (#1623; contract: <c>spec/baton.md</c> §3):
/// spawns the resolved verify command (<see cref="Mutation.VerifyCommandResolver"/> since #1702 —
/// <c>pixi run &lt;task&gt;</c> for a role default, or the platform shell for a repo-declared/overridden
/// command line) once, under <paramref name="workingDirectory"/>, and reports pass/fail plus a bounded
/// tail. Never invoked from inside a worker's own turn — the entire point of this issue is that the
/// ENGINE runs this, not the model.
/// </summary>
/// <remarks>
/// <c>tools/gates/gates.py</c> is the one place a gate-run's overall
/// verdict text is assembled — its <c>summarise()</c> emits a deterministic
/// <c>"GATES: FAIL {n} of {m} -- name1, name2"</c> line on failure, which this class parses rather than
/// re-implementing gate aggregation. If that line's shape ever changes, <see cref="FailingMembers"/>
/// degrades to empty (never a fabricated guess) while <see cref="Passed"/> still reflects the real exit
/// code, so a shape drift downgrades diagnostic detail rather than the pass/fail verdict itself.
/// Registered in <c>tests/Baton.Architecture.Tests/VendorSpawnGateTests.cs</c>'s approved-spawn-sites
/// list, per that test's own requirement that every <see cref="BatonTask"/> construction site in
/// <c>src/</c> be named there.
/// </remarks>
public static class VerifyRunner
{
    private const string FailMarker = "GATES: FAIL";
    private const string FailingMembersSeparator = " -- ";

    /// <summary>
    /// The TOTAL bound on <see cref="VerifyOutcome.Tail"/> — the same "bounded tail, never a full log
    /// dump" shape <c>OutcomeClassifier.MaxStderrTailInReason</c> already applies to a worker's own
    /// stderr, scaled up because a gate run's own output is naturally longer than one process's
    /// stderr. #1701 changed WHICH bytes count toward it (each failing member's own block, keyed off
    /// its marker line, rather than a blind cut of the whole combined stream that could drop a failing
    /// member's diagnostic text once other members' one-line pass markers followed it) but not the
    /// total: <see cref="BuildTail"/> splits this budget evenly across however many members failed and
    /// clamps the joined result, so N failing members never yield N times this many characters.
    /// </summary>
    private const int MaxTailChars = 4000;

    /// <summary>
    /// The per-member summary line <c>tools/gates/gates.py</c>'s <c>run_gates</c>/<c>join_gates</c>
    /// print after EVERY member, pass or fail: <c>"  pass  name  (exit 0)"</c> / <c>"  FAIL  name
    /// (exit 1)"</c>. Both status words are exactly 4 characters, so the <c>{status,&gt;4}</c>
    /// right-alignment in gates.py never adds padding — the shape is fixed two-space-delimited
    /// fields. This is what lets #1701 key a failing member's own block out of the combined stream
    /// instead of guessing from position.
    /// Known narrow gap (#1701 review): <c>gates-selftest</c> is itself an OVERLAP member whose own
    /// selftest fabricates lines in exactly this shape (<c>tools/gates/gates.py</c>'s own
    /// <c>run_gates</c>/<c>join_gates</c> control-arm fixtures) to prove <c>join_gates</c> discriminates
    /// a failing gate. If <c>gates-selftest</c> itself fails under <c>gates-quiet</c>, those fabricated
    /// lines segment inside its own block, so this member's tail can start after its last fabricated
    /// marker rather than at the top of its real output. Affects only that one member's own diagnostic
    /// completeness, never <see cref="ParseFailingMembers"/>'s verdict; no clean fix without changing
    /// gates.py's fixture shape, so left as a known gap rather than a redesign.
    /// </summary>
    private static readonly Regex MemberMarkerLine = new(
        @"^  (?<status>pass|FAIL)  (?<name>\S+)  \(exit (?<code>-?\d+)\) *\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The program/args-injecting form #1702 made the ONLY production entry point (MutationInterface
    /// spawns whatever <see cref="Mutation.VerifyCommandResolver.Resolve"/> resolved — <c>pixi</c> for a
    /// role default, <c>cmd.exe</c> for a repo-declared/overridden line — never a hardcoded <c>pixi</c>
    /// call). Internal so a test can also point this at a fake command (a shell one-liner that exits
    /// non-zero, or prints a synthetic <c>GATES: FAIL ...</c> line) rather than a real, minutes-long
    /// <c>pixi run gates-quiet</c> — the same seam <c>WorkerBindingConfigWriter</c>'s own
    /// budget-injecting internal overload exists for.
    /// </summary>
    internal static async Task<VerifyOutcome> RunProcessAsync(
        string program, IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        // #1722: an already-cancelled token never launches at all -- a fast child could otherwise
        // exit before this method observes the cancellation (see the post-capture check below), and
        // there is no reason to spawn a process whose result is already decided.
        if (cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command cancelled before it was launched.", Kind: VerifyFailedKind.Cancelled);
        }

        int exitCode;
        string text;
        try
        {
            (exitCode, text) = await CaptureAsync(program, args, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (BatonCancelException ex)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command cancelled: {ex.Message}", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonTimeoutException ex)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command timed out: {ex.Message}", Kind: VerifyFailedKind.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command was cancelled.", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command cancelled: {ex.Message}", Kind: VerifyFailedKind.Cancelled);
        }
        catch (BatonException ex) when (ex.ErrorCode == BatonErrorCode.TimedOut)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command timed out: {ex.Message}", Kind: VerifyFailedKind.TimedOut);
        }
        catch (BatonException ex)
        {
            // The OS refusing to spawn `pixi` at all -- not the worker's fault, but the honest outcome
            // is still "verify did not confirm this execution", never a silent pass. Settles Indeterminate.
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command failed to complete: {ex.Message}", Kind: VerifyFailedKind.GatesFailed);
        }

        // #1722: cancellation takes precedence over whatever exit code the child happened to produce.
        // BatonTask can observe a cancellation registered against an already-finished job as a no-op
        // (docs on BatonTask.RunAsync's cancellationToken param) -- a fast child that exits 0 in the
        // gap between the cancel firing and the kill landing must still report Cancelled, not Passed.
        if (cancellationToken.IsCancellationRequested)
        {
            return new VerifyOutcome(false, FailingMembers: null, Tail: "Verify command cancelled.", Kind: VerifyFailedKind.Cancelled);
        }

        if (exitCode == 0)
        {
            return VerifyOutcome.Pass;
        }

        var failingMembers = ParseFailingMembers(text);
        var tail = BuildTail(text, failingMembers);
        return new VerifyOutcome(false, failingMembers, tail, Kind: VerifyFailedKind.GatesFailed);
    }

    /// <summary>
    /// #1702: the bare spawn-and-capture primitive <see cref="RunProcessAsync"/> wraps with verify's
    /// pass/fail semantics — factored out so <see cref="VerifyCommandResolver"/>'s pre-flight
    /// runnability probe (<c>pixi task list</c>) can reuse the identical <see cref="BatonTask"/>
    /// plumbing without inheriting a verify-specific interpretation of the exit code, which a probe has
    /// no use for. Exceptions propagate to the caller rather than degrading to a <see cref="VerifyOutcome"/>
    /// here — the probe's own caller decides what an unspawnable <c>pixi</c> means (not runnable, never
    /// a silent pass), which is a different mapping than <see cref="RunProcessAsync"/>'s.
    /// </summary>
    /// <param name="stdoutOnly">
    /// #1708 L3: drop stderr instead of interleaving it into the returned output. Off by default — the
    /// verify run and the <c>pixi task list</c> probe both WANT the combined stream, because for them the
    /// output is diagnostic text. On for the declaration read, whose output is PARSED; spec/baton.md §3
    /// states what an interleaved warning would cost there.
    /// </param>
    /// <param name="environmentAllowList">
    /// #1708 L3: when non-null, the child inherits ONLY these ambient variables (plus
    /// <paramref name="environmentOverrides"/>) instead of the whole environment. Null keeps the
    /// inherit-everything default described below.
    /// </param>
    /// <param name="environmentOverrides">Variables set explicitly on the child, whatever the allowlist says.</param>
    internal static async Task<(int ExitCode, string Output)> CaptureAsync(
        string program,
        IReadOnlyList<string> args,
        string? workingDirectory,
        CancellationToken cancellationToken,
        bool stdoutOnly = false,
        IReadOnlyList<string>? environmentAllowList = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var output = new System.Text.StringBuilder();
        // No WithClearEnv() by default: unlike a vendor worker dispatch, the default caller spawns the
        // engine's own trusted tool (`pixi`, which itself needs its host toolchain's PATH/CONDA_PREFIX/etc.
        // to resolve) rather than an adapter-sandboxed process, so it inherits the ambient environment the
        // same way a human running `pixi run gates-quiet` by hand would. A caller whose child's OUTPUT is
        // a trust boundary rather than a hint passes an allowlist instead (#1708 L3 --
        // VerifyCommandResolver's git spawns).
        // Process-level timeout is omitted (F3): buildlock's own loud timeout bounds each lock-competing
        // step instead of an arbitrary overall wall-clock ceiling causing spurious Indeterminate settlements.
        using var task = new BatonTask(program, [.. args])
            .WithCaptureOutput(true);

        if (environmentAllowList is not null)
        {
            task.WithClearEnv(true);
            foreach (var name in environmentAllowList)
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                {
                    task.WithEnv(name, value);
                }
            }
        }

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                task.WithEnv(name, value);
            }
        }

        if (workingDirectory is not null)
        {
            task.WithCwd(workingDirectory);
        }

        var exitCode = -1;
        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case BatonTaskEventKind.StderrChunk when stdoutOnly:
                    break;
                case BatonTaskEventKind.StdoutChunk or BatonTaskEventKind.StderrChunk when e.Data is { } data:
                    output.Append(System.Text.Encoding.UTF8.GetString(data));
                    break;
                case BatonTaskEventKind.Exited:
                    exitCode = e.ExitCode;
                    break;
            }
        };

        await task.RunAsync(cancellationToken).ConfigureAwait(false);
        return (exitCode, output.ToString());
    }

    /// <summary>
    /// The failing member(s)' own output, not a blind tail of the whole combined stream (#1701).
    /// Segments <paramref name="output"/> on <see cref="MemberMarkerLine"/> — each segment is one
    /// member's own captured output followed by its summary line — and returns the segment(s) for
    /// members named in <paramref name="failingMembers"/> with a FAIL marker. The JOINED result stays
    /// bounded to <see cref="MaxTailChars"/> overall (never one-member-worth-of-bound times N members)
    /// by splitting that budget evenly across however many members failed, each kept tail-first.
    /// Falls back to a whole-stream tail (the pre-#1701 behavior) when no marker line is recognized at
    /// all, matching <see cref="ParseFailingMembers"/>'s own shape-drift fallback: degrade detail,
    /// never fabricate structure that is not there.
    /// </summary>
    private static string BuildTail(string output, IReadOnlyList<string>? failingMembers)
    {
        if (failingMembers is not { Count: > 0 })
        {
            return WholeStreamTail(output);
        }

        var matches = MemberMarkerLine.Matches(output);
        if (matches.Count == 0)
        {
            return WholeStreamTail(output);
        }

        var wanted = new HashSet<string>(failingMembers, StringComparer.Ordinal);
        var blocks = new List<string>();
        var blockStart = 0;
        foreach (Match match in matches)
        {
            var blockEnd = match.Index + match.Length;
            if (match.Groups["status"].Value == "FAIL" && wanted.Contains(match.Groups["name"].Value))
            {
                blocks.Add(output[blockStart..blockEnd]);
            }

            blockStart = blockEnd;
        }

        if (blocks.Count == 0)
        {
            return WholeStreamTail(output);
        }

        var perMemberBudget = Math.Max(1, MaxTailChars / blocks.Count);
        var sections = blocks.Select(block => block.Length > perMemberBudget ? block[^perMemberBudget..] : block);
        var joined = string.Join("\n", sections);
        // Rounding (MaxTailChars / blocks.Count truncates) plus the join separators themselves can
        // still push the total slightly over budget -- one final whole-result clamp closes that gap
        // rather than leaving the per-section cap as an approximation of the real bound.
        return joined.Length > MaxTailChars ? joined[^MaxTailChars..] : joined;
    }

    private static string WholeStreamTail(string output) =>
        output.Length > MaxTailChars ? output[^MaxTailChars..] : output;

    private static IReadOnlyList<string>? ParseFailingMembers(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var markerIndex = line.IndexOf(FailMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(FailingMembersSeparator, markerIndex, StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                continue;
            }

            var names = line[(separatorIndex + FailingMembersSeparator.Length)..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return names.Length > 0 ? names : null;
        }

        return null;
    }
}
