using Baton;

namespace Baton.Vendors;

/// <summary>
/// #1680: raised by <see cref="AgyWorkerAdapter.Resolve"/> when a grant whose only narrowing is the
/// <c>PreToolUse</c> hook (<see cref="AgyWorkerAdapter.RequiresHookAsSoleNarrowing"/>) cannot have
/// that hook's liveness confirmed by <see cref="IAgyHookLivenessProbe"/>. Thrown fail-closed, before
/// any worker is dispatched: an absent or malformed hook response is read as an ALLOW on this vendor
/// (<c>agy.hook-malformed-stdout-fails-open</c>), so refusing here is the alternative to running an
/// unverified worker with network and unbounded writes under <c>--dangerously-skip-permissions</c>.
/// </summary>
public sealed class AgyHookUnverifiedException : BatonFlowException
{
    public string HookAssemblyPath { get; }

    public AgyHookUnverifiedException(string hookAssemblyPath, string detail)
        : base(
            $"agy's PreToolUse hook ('{hookAssemblyPath}') is this grant's only narrowing " +
            "(decision 0029; #1680), and a resolve-time liveness probe could not confirm it denies a " +
            $"synthetic tool call: {detail}. On this vendor an absent or malformed hook response is " +
            "read as an ALLOW (agy.hook-malformed-stdout-fails-open), so dispatch is refused rather " +
            "than running an unverified worker with network access and unbounded writes.")
    {
        HookAssemblyPath = hookAssemblyPath;
    }
}
