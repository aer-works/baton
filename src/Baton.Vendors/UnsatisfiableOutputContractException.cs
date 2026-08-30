using Baton.Flow;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when an entry's contract declares outputs
/// that its <see cref="PermissionGrant"/> gives it no way to write (#629).
/// </summary>
/// <remarks>
/// <para>
/// A vendor worker satisfies <see cref="Baton.Flow.Domain.WorkerContract.ProducedOutputs"/> in exactly
/// one way: by writing the named artifact into <c>BATON_OUTPUT_DIR</c>. A grant with
/// <see cref="PermissionGrant.WriteFiles"/> withheld removes the tools that do it, so the combination
/// cannot succeed — with one exception, which is why the shell refusal runs first: a granted shell
/// writes anyway, and a gemini worker with writes withheld but shell and network granted was measured
/// producing its output and succeeding. <see cref="IncoherentPermissionGrantException"/> refuses that
/// combination before it reaches here, so anything arriving with writes withheld has no shell either. Before this refusal AER dispatched it anyway — the
/// worker ran to completion, exited 0, produced nothing, and the contract check failed after the run
/// had been paid for in full. One measured case cost a nine-minute frontier-model reviewer that
/// returned nothing.
/// </para>
/// <para>
/// Every declared output is required: <c>ContractValidator</c> checks <c>File.Exists</c> for each
/// one, and an <see cref="Baton.Flow.Domain.OutputCondition"/> only adds a stricter check on top of
/// existence rather than excusing it: a declared-but-absent output is a failure.
/// That is what makes refusing early safe rather than over-strict — and #650 is the
/// counterpart: a contract that declares an output nobody requires is the other way to get this
/// wrong, and the answer there was to stop declaring it, not to relax the rule.
/// </para>
/// <para>
/// Distinct from <see cref="IncoherentPermissionGrantException"/> on purpose. That one means a
/// granted shell reaches a category the operator withheld; this one means the contract cannot be met.
/// Different faults with different remedies. Where a grant carries both, the shell one is reported:
/// it names the mistake the operator actually made.
/// </para>
/// <para>
/// The remedy today is to grant <see cref="PermissionGrant.WriteFiles"/>, which also grants the
/// workspace. That <c>WriteFiles</c> cannot separate "write my report" from "modify the repo" is
/// #649, and it is why every reviewing template in <c>tools/baton-agy-loop</c> grants write.
/// </para>
/// </remarks>
public sealed class UnsatisfiableOutputContractException : BatonFlowException
{
    public string WorkerName { get; }

    /// <summary>The declared outputs this grant gives the worker no way to write.</summary>
    public IReadOnlyList<string> UnwritableOutputs { get; }

    public UnsatisfiableOutputContractException(string workerName, IReadOnlyList<string> unwritableOutputs)
        : base(
            $"Worker-binding config entry for '{workerName}' withholds WriteFiles while its contract " +
            $"declares output(s) {string.Join(", ", unwritableOutputs)}. A worker satisfies its " +
            "contract only by writing the artifact into BATON_OUTPUT_DIR, so this combination cannot " +
            "succeed and the run would spend its full budget before failing the contract check " +
            "(#629). Grant WriteFiles, or remove the declared output(s).")
    {
        WorkerName = workerName;
        UnwritableOutputs = unwritableOutputs;
    }
}
