using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Aer.Daemon;

public sealed record PendingGateEntry(
    string RoomDir,
    string OutputDir,
    string ExecutionId,
    string AskFilePath);

public static class PendingGateRegistry
{
    private static readonly ConcurrentDictionary<string, PendingGateEntry> Entries = new(StringComparer.Ordinal);

    public static void Register(string permissionRequestId, PendingGateEntry entry)
    {
        Entries[permissionRequestId] = entry;
    }

    public static bool TryGet(string permissionRequestId, [NotNullWhen(true)] out PendingGateEntry? entry)
    {
        return Entries.TryGetValue(permissionRequestId, out entry);
    }

    public static bool TryRemove(string permissionRequestId, [NotNullWhen(true)] out PendingGateEntry? entry)
    {
        return Entries.TryRemove(permissionRequestId, out entry);
    }

    public static void Clear()
    {
        Entries.Clear();
    }
}
