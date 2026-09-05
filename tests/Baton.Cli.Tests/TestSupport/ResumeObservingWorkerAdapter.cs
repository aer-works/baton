using Baton.Vendors;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Cli.Tests.TestSupport;

/// <summary>
/// A CI-safe stand-in for a real vendor adapter (issue #1359's <c>baton resume</c> tests): records
/// every <see cref="WorkerInvocation"/> it is asked to resolve — in particular
/// <see cref="WorkerInvocation.PromptTemplate"/>, <see cref="WorkerInvocation.ResumeSession"/>, and
/// <see cref="WorkerInvocation.SessionId"/> — so a test can assert the resume-shaped override
/// (<c>ResumeSession: true</c>, the operator's message as <c>PromptTemplate</c>, the recorded
/// <c>SessionId</c>) actually reached the adapter, the same seam <c>ClaudeWorkerAdapter</c>'s
/// <c>--resume</c>/<c>--session-id</c> branch reads. Always writes every declared output so the
/// contract is satisfied and the workflow proceeds.
/// </summary>
internal sealed class ResumeObservingWorkerAdapter : IWorkerAdapter
{
    private readonly Lock _gate = new();
    private readonly List<WorkerInvocation> _observedInvocations = [];

    public IReadOnlyList<WorkerInvocation> ObservedInvocations
    {
        get { lock (_gate) { return [.. _observedInvocations]; } }
    }

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        lock (_gate)
        {
            _observedInvocations.Add(invocation);
        }

        var writeCommands = contract.ProducedOutputs.Count > 0
            ? string.Join(" & ", contract.ProducedOutputs.Select(o => WriteCommand(o.Name)))
            : "exit 0";

        // A resume runs measurably longer than the original dispatch (#1360 F2's review finding): the
        // resume-linkage test needs the two executions' wall-clock figures to actually differ so its
        // assertion can fail when the code swaps which execution's usage lands in which field, rather
        // than the two near-instant scripts coincidentally producing indistinguishable millisecond
        // deltas.
        var script = invocation.ResumeSession
            ? string.Join(" & ", [SleepCommand(), writeCommands])
            : writeCommands;

        return new CoreDispatchTarget("cmd", ["/c", script], invocation.WorkingDirectory);
    }

    private static string SleepCommand() => "ping -n 3 127.0.0.1>nul";

    private static string WriteCommand(string outputName) =>
        $"echo x>%BATON_OUTPUT_DIR%\\{outputName}";
}
