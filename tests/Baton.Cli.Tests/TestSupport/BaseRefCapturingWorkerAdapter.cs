using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A CI-safe stand-in for the engine-run capture worker: it records the base ref it was dispatched with
/// — the value the run entrypoint injected into the binding's <see cref="WorkerInvocation.PromptTemplate"/>
/// — so a test can assert the injection happened, then writes its declared output so the capture step's
/// contract is satisfied and the workflow proceeds. The real <see cref="CaptureWorkerAdapter"/> would
/// spawn <c>git diff</c>; this stands in for it so the dispatch-side wiring (compose → detect the capture
/// binding → inject HEAD → the adapter receives it) is what the test exercises, not git itself.
/// </summary>
internal sealed class BaseRefCapturingWorkerAdapter : IWorkerAdapter
{
    private readonly Lock _gate = new();
    private readonly List<string> _observedBaseRefs = [];
    private readonly List<string?> _observedWorkingDirectories = [];

    public IReadOnlyList<string> ObservedBaseRefs
    {
        get { lock (_gate) { return [.. _observedBaseRefs]; } }
    }

    /// <summary>The working directory each capture invocation carried — the workspace its git diff would run in.</summary>
    public IReadOnlyList<string?> ObservedWorkingDirectories
    {
        get { lock (_gate) { return [.. _observedWorkingDirectories]; } }
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        lock (_gate)
        {
            _observedBaseRefs.Add(invocation.PromptTemplate);
            _observedWorkingDirectories.Add(invocation.WorkingDirectory);
        }

        var script = contract.ProducedOutputs.Count > 0
            ? string.Join(
                OperatingSystem.IsWindows() ? " & " : " && ",
                contract.ProducedOutputs.Select(o => WriteCommand(o.Name)))
            : "exit 0";

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", script], invocation.WorkingDirectory)
            : new CoreDispatchTarget("sh", ["-c", script], invocation.WorkingDirectory);
    }

    private static string WriteCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"echo x>%BATON_OUTPUT_DIR%\\{outputName}"
        : $"echo x > \"$BATON_OUTPUT_DIR/{outputName}\"";
}
