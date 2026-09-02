using Baton.Core;

namespace Baton.Mutation;

/// <summary>
/// The result of one <see cref="VerifyRunner.RunAsync"/> call (#1623). <see cref="Passed"/> mirrors the
/// verify command's own exit code; <see cref="FailingMembers"/>/<see cref="Tail"/> are populated only
/// when it fails, and only when the summary line's shape is recognized — never fabricated.
/// </summary>
public sealed record VerifyOutcome(bool Passed, IReadOnlyList<string>? FailingMembers = null, string? Tail = null)
{
    public static readonly VerifyOutcome Pass = new(true);
}

/// <summary>
/// The engine-run verify step's own primitive (#1623; contract: <c>spec/baton.md</c> §3):
/// spawns <c>pixi run &lt;task&gt;</c> once, under <paramref name="workingDirectory"/>, and
/// reports pass/fail plus a bounded tail. Never invoked from inside a worker's own turn — the entire
/// point of this issue is that the ENGINE runs this, not the model.
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
    /// A fixed ceiling rather than a per-role configurable (out of this issue's scope, per the ruling's
    /// own wording: <c>--token-budget</c> is the one operator-facing override this work adds).
    /// <c>gates-quiet</c> runs the full test suite; generous headroom over an uncontended run matters
    /// more here than a tight bound, since a slow verify still settles Indeterminate rather than
    /// silently retrying.
    /// </summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(30);

    public static Task<VerifyOutcome> RunAsync(
        string pixiTask, string? workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pixiTask);
        return RunProcessAsync("pixi", ["run", pixiTask], workingDirectory, cancellationToken);
    }

    /// <summary>
    /// The program/args-injecting form of <see cref="RunAsync"/>. Internal so a test can point this at
    /// a fake command (a shell one-liner that exits non-zero, or prints a synthetic
    /// <c>GATES: FAIL ...</c> line) rather than a real, minutes-long <c>pixi run gates-quiet</c> — the
    /// same seam <c>WorkerBindingConfigWriter</c>'s own budget-injecting internal overload exists for.
    /// Production always calls the public overload, which always names <c>pixi</c>.
    /// </summary>
    internal static async Task<VerifyOutcome> RunProcessAsync(
        string program, IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        // Deliberately no WithClearEnv(): unlike a vendor worker dispatch, this spawns the engine's own
        // trusted tool (`pixi`, which itself needs its host toolchain's PATH/CONDA_PREFIX/etc. to
        // resolve) rather than an adapter-sandboxed process, so it inherits the ambient environment the
        // same way a human running `pixi run gates-quiet` by hand would.
        using var task = new BatonTask(program, [.. args])
            .WithTimeout(VerifyTimeout)
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

        try
        {
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BatonException ex)
        {
            // A timeout, a cancellation, or the OS refusing to spawn `pixi` at all -- none of these
            // are the worker's fault, but the honest outcome is still "verify did not confirm this
            // execution", never a silent pass. Settles Indeterminate the same as an ordinary failure;
            // the conductor sees why in the tail.
            return new VerifyOutcome(false, FailingMembers: null, Tail: $"Verify command failed to complete: {ex.Message}");
        }

        if (exitCode == 0)
        {
            return VerifyOutcome.Pass;
        }

        var text = output.ToString();
        var failingMembers = ParseFailingMembers(text);
        var tail = text.Length > MaxTailChars ? text[^MaxTailChars..] : text;
        return new VerifyOutcome(false, failingMembers, tail);
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
