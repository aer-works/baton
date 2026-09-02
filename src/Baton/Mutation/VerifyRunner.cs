using Baton.Core;
using Baton.Domain;

namespace Baton.Mutation;

/// <summary>
/// The result of one <see cref="VerifyRunner.RunProcessAsync"/> call (#1623). <see cref="Passed"/>
/// mirrors the verify command's own exit code; <see cref="FailingMembers"/>/<see cref="Tail"/> are
/// populated only when it fails, and only when the summary line's shape is recognized — never
/// fabricated. <see cref="Kind"/> distinguishes gate breakage from timeouts, cancellations, or engine
/// restarts (F3).
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
    /// How much of the verify command's combined stdout+stderr a failure keeps, tail-first — the same
    /// "bounded tail, never a full log dump" shape <c>OutcomeClassifier.MaxStderrTailInReason</c>
    /// already applies to a worker's own stderr, scaled up because a gate run's own output is
    /// naturally longer than one process's stderr.
    /// </summary>
    private const int MaxTailChars = 4000;

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

        if (exitCode == 0)
        {
            return VerifyOutcome.Pass;
        }

        var failingMembers = ParseFailingMembers(text);
        var tail = text.Length > MaxTailChars ? text[^MaxTailChars..] : text;
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
    internal static async Task<(int ExitCode, string Output)> CaptureAsync(
        string program, IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        // Deliberately no WithClearEnv(): unlike a vendor worker dispatch, this spawns the engine's own
        // trusted tool (`pixi`, which itself needs its host toolchain's PATH/CONDA_PREFIX/etc. to
        // resolve) rather than an adapter-sandboxed process, so it inherits the ambient environment the
        // same way a human running `pixi run gates-quiet` by hand would.
        // Process-level timeout is omitted (F3): buildlock's own loud timeout bounds each lock-competing
        // step instead of an arbitrary overall wall-clock ceiling causing spurious Indeterminate settlements.
        using var task = new BatonTask(program, [.. args])
            .WithCaptureOutput(true);

        if (workingDirectory is not null)
        {
            task.WithCwd(workingDirectory);
        }

        var exitCode = -1;
        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
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
