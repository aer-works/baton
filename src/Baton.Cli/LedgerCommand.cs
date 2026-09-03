using Baton.Status;
using Baton.Store;
using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// <c>baton ledger --rebuild</c> (#1570, quota-design S4b): walks every room this process can find
/// (<see cref="FindLiveRoomDirectoriesAsync"/>), re-derives each one's own executions via
/// <see cref="QuotaLedgerStore.BuildEntries"/>, and hands the flat result to
/// <see cref="QuotaLedgerStore.RebuildAsync"/> — that method's own remarks state the merge rule and why
/// it recovers less than the ledger can hold; spec/baton.md §7 states it again for an operator, not
/// restated a third time here. Not a <see cref="CommandResult"/>/<see cref="FlowStateReporter"/>
/// command — same carve-out as <see cref="RoomDeleteCommand"/>/<see cref="RoomsPruneCommand"/>: there
/// is no workflow pump here, so there is nothing for that shape to report.
/// </summary>
public static class LedgerCommand
{
    public const string Usage = "Usage: baton ledger --rebuild";

    /// <param name="ledgerFilePathOverride">Test seam — production callers always use <see cref="BatonPaths.QuotaLedgerFile"/>.</param>
    /// <param name="registryFilePathOverride">Test seam — production callers always use <see cref="BatonPaths.RoomRegistryFile"/>.</param>
    public static async Task<int> RebuildAsync(
        TextWriter output,
        string? ledgerFilePathOverride = null,
        string? registryFilePathOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var ledgerFilePath = ledgerFilePathOverride ?? BatonPaths.QuotaLedgerFile;
        var registryFilePath = registryFilePathOverride ?? BatonPaths.RoomRegistryFile;

        var roomDirectoryPaths = await FindLiveRoomDirectoriesAsync(registryFilePath, cancellationToken).ConfigureAwait(false);

        var freshEntries = new List<QuotaLedgerEntry>();
        var roomsWalked = 0;
        foreach (var roomDirectoryPath in roomDirectoryPaths)
        {
            var flowLogPath = Path.Combine(roomDirectoryPath, BatonPaths.FlowLogFileName);
            if (!File.Exists(flowLogPath))
            {
                continue;
            }

            var entries = await new FlowEventLogReader(flowLogPath)
                .ReadAllEntriesWithTimestampsAsync(cancellationToken).ConfigureAwait(false);
            freshEntries.AddRange(QuotaLedgerStore.BuildEntries(entries, roomDirectoryPath));
            roomsWalked++;
        }

        var result = await QuotaLedgerStore.RebuildAsync(freshEntries, ledgerFilePath, cancellationToken).ConfigureAwait(false);

        output.WriteLine(
            $"Ledger rebuild: walked {roomsWalked} live room(s), recovered {result.RecoveredCount} execution(s) not " +
            $"already in the ledger. The ledger now holds {result.TotalCount} execution(s) total (was {result.PreviousCount}).");
        output.WriteLine(
            "A rebuild recovers LESS than the ledger can hold: it only re-derives from rooms still on disk. " +
            "RoomRetentionSweep moves execution directories out of a live room's reach on its own schedule -- an " +
            "execution the ledger already recorded survives regardless (this merges into the existing ledger, it " +
            "never regenerates from the walk alone), but a lane killed before it ever settled never wrote a line " +
            "here to recover in the first place. See spec/baton.md §7.");

        return 0;
    }

    private static async Task<IReadOnlyList<string>> FindLiveRoomDirectoriesAsync(
        string registryFilePath, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(BatonPaths.RecordKeyComparer);

        if (Directory.Exists(BatonPaths.Rooms))
        {
            foreach (var directory in Directory.GetDirectories(BatonPaths.Rooms))
            {
                paths.Add(BatonPaths.RecordKey(directory));
            }
        }

        var registryEntries = await RoomRegistryStore.ReadDistinctByRoomAsync(registryFilePath, cancellationToken).ConfigureAwait(false);
        foreach (var entry in registryEntries)
        {
            paths.Add(entry.RoomPath);
        }

        return paths.Where(Directory.Exists).ToList();
    }
}
