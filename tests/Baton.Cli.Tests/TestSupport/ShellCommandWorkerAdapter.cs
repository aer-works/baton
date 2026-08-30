using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// The "shell-stub adapter (deterministic, CI-safe)" M11 Phase 3's plan calls for: resolves a
/// <see cref="WorkerInvocation"/> by running its <see cref="WorkerInvocation.PromptTemplate"/>
/// directly as a shell command line, the same <c>sh -c</c>/<c>cmd /c</c> wrapping
/// <c>ClaudeWorkerAdapter</c> and <c>Baton.Tests</c>' shell-stub workers already use — so a
/// worker-binding config entry's prompt template is, for this adapter only, the literal command to
/// run (e.g. <c>echo hi &gt; $BATON_OUTPUT_DIR/plan</c>), letting <c>baton run</c> be driven end to end
/// through the real <see cref="IWorkerAdapter"/>/bindings-config seam without a live LLM.
/// </summary>
internal sealed class ShellCommandWorkerAdapter : IWorkerAdapter
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract) => OperatingSystem.IsWindows()
        ? new CoreDispatchTarget("cmd", ["/c", invocation.PromptTemplate], invocation.WorkingDirectory)
        : new CoreDispatchTarget("sh", ["-c", invocation.PromptTemplate], invocation.WorkingDirectory);
}
