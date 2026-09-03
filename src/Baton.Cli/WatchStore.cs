using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

namespace Baton.Cli;

/// <summary>
/// One <c>baton watch</c> registration (#1488): which room to watch, where to send the
/// notification, when it was registered, and (once fired) when. Serialized as its own file —
/// <c>{watchId}.json</c> under <see cref="BatonPaths.Watches"/> — rather than a shared JSONL
/// registry line, since a watch is claimed and mutated in place (<see cref="FiredAt"/>) by whichever
/// of the registering process or the daemon sweep gets there first; one file per watch makes that a
/// single-file lock instead of a whole-registry one.
/// </summary>
public sealed record WatchRecord(
    [property: JsonPropertyName("watchId")] string WatchId,
    [property: JsonPropertyName("roomDirectoryPath")] string RoomDirectoryPath,
    [property: JsonPropertyName("notifyTarget")] string NotifyTarget,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("firedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? FiredAt = null);

/// <summary>
/// Reads and writes <see cref="BatonPaths.Watches"/>'s per-watch files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every access to one watch's file is serialized by a named <see cref="Mutex"/> keyed on that
/// file's path</b> — the same idiom <see cref="Baton.Vendors.RoomRegistryStore"/>'s private
/// <c>RunUnderLock</c> uses (that method is private, so this is a local copy, not a shared call),
/// and for the identical reason: <see cref="TryClaimAsync"/> is a read-modify-write (read
/// <see cref="WatchRecord.FiredAt"/>, then write it), and two processes — a <c>baton watch</c>
/// registration checking "is this room already terminal?" and a concurrent daemon
/// <c>WatchSweep</c> iteration — can race to claim the exact same already-terminal watch. Without a
/// lock spanning the whole read-then-write, both could observe <c>FiredAt == null</c> and both fire,
/// which is precisely the double-fire spec/baton.md §2 rules out. Acquire, read/write, and release
/// all happen synchronously on one thread inside <c>Task.Run</c> — never an <c>await</c> between
/// <see cref="Mutex.WaitOne()"/> and <see cref="Mutex.ReleaseMutex"/> — because <see cref="Mutex"/>
/// ownership is thread-affine on Windows and an <c>await</c> resuming on a different pool thread
/// makes <see cref="Mutex.ReleaseMutex"/> throw (<see cref="Baton.Vendors.RoomRegistryStore"/>'s own
/// remarks record this being caught in review there).
/// </para>
/// <para>
/// <b>Exactly-once, never double, per spec/baton.md §2.</b> <see cref="TryClaimAsync"/> is the only
/// way <see cref="WatchRecord.FiredAt"/> is ever set: it returns <c>true</c> (the caller may now
/// notify) only when the watch was unclaimed at the moment it acquired the lock, and atomically
/// marks it claimed in the same critical section. A caller that loses the race gets <c>false</c> and
/// must not notify. Dying between that mark landing on disk and the actual notify going out drops
/// the one notification on the floor instead of ever risking two — nothing revisits an already-
/// claimed file.
/// </para>
/// </remarks>
public static class WatchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public static string FilePath(string watchesDirectoryPath, string watchId) =>
        Path.Combine(watchesDirectoryPath, $"{watchId}.json");

    /// <summary>Writes a freshly-registered watch (never carries a <see cref="WatchRecord.FiredAt"/>).</summary>
    public static Task WriteAsync(WatchRecord record, string watchesDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(watchesDirectoryPath);

        Directory.CreateDirectory(watchesDirectoryPath);
        var path = FilePath(watchesDirectoryPath, record.WatchId);
        return Task.Run(() => RunUnderLock(path, () => WriteFile(path, record)), cancellationToken);
    }

    /// <summary><c>null</c> for a missing, malformed, or momentarily-unreadable watch file — the same
    /// tolerant read <see cref="TerminalSentinelWriter.TryReadAsync"/> uses, never a caller-visible
    /// crash over one bad or in-flight-written file.</summary>
    public static Task<WatchRecord?> TryReadAsync(
        string watchesDirectoryPath, string watchId, CancellationToken cancellationToken = default)
    {
        var path = FilePath(watchesDirectoryPath, watchId);
        return Task.Run(() => RunUnderLock(path, () => TryReadFile(path)), cancellationToken);
    }

    /// <summary>Every watch file under <paramref name="watchesDirectoryPath"/>, pending and fired alike.
    /// A missing directory reads as no watches; a malformed file is skipped, never failing the whole
    /// listing.</summary>
    public static Task<IReadOnlyList<WatchRecord>> ListAsync(
        string watchesDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(watchesDirectoryPath))
        {
            return Task.FromResult<IReadOnlyList<WatchRecord>>([]);
        }

        return Task.Run(
            () =>
            {
                var records = new List<WatchRecord>();
                foreach (var file in Directory.GetFiles(watchesDirectoryPath, "*.json"))
                {
                    var record = RunUnderLock(file, () => TryReadFile(file));
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }

                return (IReadOnlyList<WatchRecord>)records;
            },
            cancellationToken);
    }

    /// <summary>
    /// Attempts to claim <paramref name="watchId"/> for firing: <c>true</c> only when the watch's file
    /// exists, parses, and had no <see cref="WatchRecord.FiredAt"/> at the moment this call acquired
    /// the per-file lock — in which case it is atomically rewritten with
    /// <paramref name="firedAtUtc"/> before returning. <c>false</c> for a missing/unreadable watch or
    /// one another caller already claimed; the caller must not notify on <c>false</c>.
    /// </summary>
    public static Task<bool> TryClaimAsync(
        string watchesDirectoryPath, string watchId, DateTime firedAtUtc, CancellationToken cancellationToken = default)
    {
        var path = FilePath(watchesDirectoryPath, watchId);
        return Task.Run(
            () => RunUnderLock(path, () =>
            {
                var record = TryReadFile(path);
                if (record is null || record.FiredAt is not null)
                {
                    return false;
                }

                WriteFile(path, record with { FiredAt = firedAtUtc });
                return true;
            }),
            cancellationToken);
    }

    /// <summary><c>baton watch --clear-fired</c>: deletes every watch file whose
    /// <see cref="WatchRecord.FiredAt"/> is set. Returns the count removed.</summary>
    public static Task<int> RemoveFiredAsync(string watchesDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(watchesDirectoryPath))
        {
            return Task.FromResult(0);
        }

        return Task.Run(
            () =>
            {
                var removed = 0;
                foreach (var file in Directory.GetFiles(watchesDirectoryPath, "*.json"))
                {
                    var removedThis = RunUnderLock(file, () =>
                    {
                        var record = TryReadFile(file);
                        if (record?.FiredAt is null)
                        {
                            return false;
                        }

                        File.Delete(file);
                        return true;
                    });

                    if (removedThis)
                    {
                        removed++;
                    }
                }

                return removed;
            },
            cancellationToken);
    }

    private static WatchRecord? TryReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WatchRecord>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Temp-file-then-<see cref="File.Move(string,string,bool)"/>, the same atomic replace
    /// <see cref="TerminalSentinelWriter.WriteAsync"/> uses, so a concurrent reader under this type's
    /// lock never observes a truncated file.</summary>
    private static void WriteFile(string path, WatchRecord record)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static T RunUnderLock<T>(string filePath, Func<T> action)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(BatonPaths.RecordKey(filePath).ToUpperInvariant())));
        using var mutex = new Mutex(initiallyOwned: false, name: $"Global\\baton-watch-{digest}");

        bool owned;
        try
        {
            owned = mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A prior holder crashed mid-access; ownership still transfers to us (Mutex's own
            // contract). Whatever partial state it left is handled by TryReadFile's tolerant read.
            owned = true;
        }

        if (!owned)
        {
            throw new IOException($"Timed out after {LockTimeout} waiting for the watch lock on '{filePath}'.");
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void RunUnderLock(string filePath, Action action) =>
        RunUnderLock<object?>(filePath, () =>
        {
            action();
            return null;
        });
}
