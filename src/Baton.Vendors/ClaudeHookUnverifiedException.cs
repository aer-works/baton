using Baton;

namespace Baton.Vendors;

/// <summary>
/// #532: raised by <see cref="ClaudeWorkerAdapter.Resolve"/> when a resolve-time liveness probe
/// (<see cref="IClaudeHookLivenessProbe"/>) cannot confirm the mandatory <c>PreToolUse</c> hook
/// (decision 0029) actually executes and denies a synthetic <c>Write</c> call. Thrown fail-closed,
/// before any worker is dispatched: since #649 that hook is the SOLE bound on where a write-family
/// tool call lands, so a hook that exists on disk but cannot run leaves every write ungated with
/// nothing to say so.
/// </summary>
public sealed class ClaudeHookUnverifiedException : BatonFlowException
{
    public string HookAssemblyPath { get; }

    public ClaudeHookUnverifiedException(string hookAssemblyPath, string detail)
        : base(
            $"claude's PreToolUse hook ('{hookAssemblyPath}') is the sole bound on where a " +
            "write-family tool call lands (decision 0029; #649), and a resolve-time liveness probe " +
            $"could not confirm it denies a synthetic Write call: {detail}. A hook that exists but " +
            "cannot execute fails open silently (gate.broken-hook-fails-open), so dispatch is refused " +
            "rather than running a worker whose writes are unbounded.")
    {
        HookAssemblyPath = hookAssemblyPath;
    }
}
