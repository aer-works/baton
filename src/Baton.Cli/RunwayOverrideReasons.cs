using Baton.Vendors;

namespace Baton.Cli;

/// <summary>
/// Reads back the runway-override audit record <c>DispatchCommand</c> wrote onto a room's
/// <c>bindings.json</c> (#1848), so the settle site can stamp
/// <c>CostLedgerEntry.RunwayOverrideReason</c> on that room's ledger rows (#1849). Lives here rather
/// than in <c>Baton.Accounting</c> because the binding record is a <c>Baton.Vendors</c> type and the
/// engine layer does not reference that project — the ledger takes the resolved reasons as an argument
/// instead of learning a second copy of the bindings schema.
/// </summary>
public static class RunwayOverrideReasons
{
    /// <summary>
    /// Worker name to the reason recorded for it — only for overrides that actually bypassed a Hold
    /// (<see cref="RunwayOverride.Used"/>), because a flag that bypassed nothing is not an override of
    /// this row's spend. Empty when the room has no bindings file, it cannot be read, or no binding
    /// carries a record: <b>fail open</b>, the same posture every other accounting read at the settle
    /// site takes — a missing audit stamp must never be the reason a settled run reports as failed.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadForRoomAsync(
        string roomDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var bindingsFilePath = Path.Combine(roomDirectoryPath, "bindings.json");
        if (!File.Exists(bindingsFilePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings;
        try
        {
            bindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatonFlowException)
        {
            Console.Error.WriteLine(
                $"Could not read '{bindingsFilePath}' for runway-override attribution: {ex.Message} "
                + "The cost ledger rows for this room carry no runwayOverrideReason.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (worker, entry) in bindings)
        {
            if (entry.RunwayOverride is { Used: true, Reason: { Length: > 0 } reason })
            {
                reasons[worker] = reason;
            }
        }

        return reasons;
    }
}
