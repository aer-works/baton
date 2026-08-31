using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// Whether a room directory has a REAL ledger yet (#1374 F1's own follow-up finding). A bare
/// <c>File.Exists(flow.jsonl)</c> is not enough: <c>FlowEventLogWriter</c>'s constructor opens with
/// <see cref="FileMode.Append"/>, which creates the file the instant it opens — before
/// <c>MutationInterface.StartWorkflowAsync</c>'s own <c>ConcurrencyGuard.Acquire</c> can throw
/// <see cref="Baton.Concurrency.WorkflowLockedException"/>. So a room refused for being locked (the
/// RoomHeld exit code) can be left with a zero-byte <c>flow.jsonl</c> despite nothing ever having been
/// recorded — and a bare existence check would then treat a LATER genuine validation failure against
/// that same room as "already ledgered", skipping the pre-ledger sentinel write and leaving the room
/// stuck "Running / no ledger yet" forever with no terminal record at all, the exact fate #1356 exists
/// to prevent. Shared by <c>Program</c>'s pre-ledger sentinel guard and <c>StatusCommand</c>'s
/// pre-ledger status branch so the two can't disagree about which rooms count as pre-ledger.
/// </summary>
internal static class RoomLedgerProbe
{
    public static bool HasLedger(string roomDirectoryPath)
    {
        var info = new FileInfo(Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName));
        return info.Exists && info.Length > 0;
    }
}
