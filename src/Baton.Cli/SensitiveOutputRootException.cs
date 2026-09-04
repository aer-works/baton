namespace Baton.Cli;

/// <summary>
/// Raised by <see cref="RunCommand"/> when a worker binding's adapter reports
/// (<see cref="Vendors.IWorkerAdapter.HasSensitiveOutputPathComponent"/>) that the room directory this
/// run would write that worker's output under contains a path component its vendor CLI treats as
/// sensitive (#599, corrected to a component match by #1834).
/// </summary>
/// <remarks>
/// Refused here, before <c>WorktreeWorkspaces.Provision</c> or any dispatch — the same "before the run
/// is paid for" posture <c>UnsatisfiableOutputContractException</c> (#629) takes for a grant that can
/// never write the contract. This is a different fault: the grant is fine and the write tool is
/// pre-approved, but the vendor's own CLI refuses the target path regardless, because it contains a
/// path component the vendor treats as sensitive rather than because AER withheld anything.
/// </remarks>
public sealed class SensitiveOutputRootException : BatonFlowException
{
    public string WorkerName { get; }
    public string RoomDirectoryPath { get; }
    public string OffendingComponent { get; }

    public SensitiveOutputRootException(string workerName, string roomDirectoryPath, string offendingComponent)
        : base(
            $"Room directory '{roomDirectoryPath}' contains a path component named '{offendingComponent}', " +
            $"which worker '{workerName}' 's adapter treats as its vendor's own sensitive path component " +
            "(#1834). That vendor refuses to write BATON_OUTPUT_DIR under any path containing that " +
            "component, regardless of CLAUDE_CONFIG_DIR's value or the grant AER hands it -- the worker " +
            "would run to completion and produce none of its declared outputs. Pass a --room-dir whose " +
            "path has no such component.")
    {
        WorkerName = workerName;
        RoomDirectoryPath = roomDirectoryPath;
        OffendingComponent = offendingComponent;
    }
}
