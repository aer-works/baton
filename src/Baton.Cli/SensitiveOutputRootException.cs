namespace Baton.Cli;

/// <summary>
/// Raised by <see cref="RunCommand"/> when a worker binding's adapter names a
/// <see cref="Vendors.IWorkerAdapter.SensitiveOutputRoot"/> and the room directory this run would
/// write that worker's output under falls inside it (#599).
/// </summary>
/// <remarks>
/// Refused here, before <c>WorktreeWorkspaces.Provision</c> or any dispatch — the same "before the run
/// is paid for" posture <c>UnsatisfiableOutputContractException</c> (#629) takes for a grant that can
/// never write the contract. This is a different fault: the grant is fine and the write tool is
/// pre-approved, but the vendor's own CLI refuses the target path regardless, because it sits inside
/// the vendor's own configuration root rather than because AER withheld anything.
/// </remarks>
public sealed class SensitiveOutputRootException : BatonFlowException
{
    public string WorkerName { get; }
    public string RoomDirectoryPath { get; }
    public string SensitiveRoot { get; }

    public SensitiveOutputRootException(string workerName, string roomDirectoryPath, string sensitiveRoot)
        : base(
            $"Room directory '{roomDirectoryPath}' resolves inside '{sensitiveRoot}', which worker " +
            $"'{workerName}' 's adapter treats as its vendor's own sensitive configuration root. That " +
            "vendor refuses to write BATON_OUTPUT_DIR there regardless of the grant AER hands it (#599) " +
            "-- the worker would run to completion, exit 0, and produce none of its declared outputs. " +
            "Pass a --room-dir outside that root.")
    {
        WorkerName = workerName;
        RoomDirectoryPath = roomDirectoryPath;
        SensitiveRoot = sensitiveRoot;
    }
}
