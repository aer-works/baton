using Baton;

namespace Baton.Vendors;

/// <summary>
/// #532: raised by <see cref="ClaudeWorkerAdapter.Resolve"/> when <see cref="IClaudeHookLivenessProbe"/>
/// cannot confirm the mandatory <c>PreToolUse</c> hook (decision 0029) actually executes and denies a
/// synthetic call -- see that interface's own doc comment for why this refuses fail-closed rather
/// than warning and continuing.
/// </summary>
public sealed class ClaudeHookUnverifiedException : BatonFlowException
{
    public string HookAssemblyPath { get; }

    public ClaudeHookUnverifiedException(string hookAssemblyPath, string detail)
        : base(
            $"claude's PreToolUse hook ('{hookAssemblyPath}') could not be confirmed live by a " +
            $"resolve-time probe: {detail}. Dispatch is refused rather than running a worker against " +
            "an unverified gate.")
    {
        HookAssemblyPath = hookAssemblyPath;
    }
}
