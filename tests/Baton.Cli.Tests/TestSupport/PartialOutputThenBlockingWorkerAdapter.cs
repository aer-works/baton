using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A worker that writes its declared output and then blocks until something kills it — the mid-flight
/// shape #1708 M1 needs pinned: a report that exists on disk but was written by an execution that never
/// reached a natural exit (operator cancel, budget arrest, timeout). Everything the report says about
/// what the worker did may be half-true; that is the point, and spec/baton.md §3 states why it is
/// delivered anyway.
/// <para>
/// Deliberately NOT a variant of <see cref="ContractOutputWorkerAdapter"/>: the blocking tail is the
/// whole fixture, and a flag on the shared fake would let an unrelated test inherit a 60-second hang.
/// </para>
/// </summary>
internal sealed class PartialOutputThenBlockingWorkerAdapter : IWorkerAdapter, IPermissionGrantTranslator
{
    public bool WithheldWritesReachTheOutbox => true;

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        var outputName = contract.ProducedOutputs[0].Name;
        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget(
                "cmd",
                ["/c", $"echo half-written>%BATON_OUTPUT_DIR%\\{outputName} & ping -n 120 127.0.0.1 >nul"],
                invocation.WorkingDirectory)
            : new CoreDispatchTarget(
                "sh",
                ["-c", $"echo half-written > \"$BATON_OUTPUT_DIR/{outputName}\"; sleep 120"],
                invocation.WorkingDirectory);
    }

    public bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason)
    {
        ArgumentNullException.ThrowIfNull(grant);
        resolvedValue = "(fake-translated)";
        gapReason = null;
        return true;
    }
}
