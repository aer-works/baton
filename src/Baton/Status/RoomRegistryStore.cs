using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;

// Lives in Baton, not Baton.Vendors: fleet_status (Baton.Cli's `baton mcp`, #1458: ex-Baton.Mcp.Host)
// needs to read this registry
// and deliberately has no Baton.Vendors project reference. The namespace stays Baton.Vendors so it
// sits next to DaemonSettingsStore and the rest of AER's per-machine storage stores it is written from.
namespace Baton.Vendors;

/// <summary>
/// One registration of a room into the machine-local, multi-project registry (spec/baton.md §8):
/// the room's own directory, the project root it was dispatched for, and when the registration was
/// written. <see cref="RoomPath"/> is what <c>fleet_status</c> scans; <see cref="ProjectRoot"/> is
/// what lets it group rooms by project without a caller having to enumerate every project directory
/// as a <c>roots</c> entry.
/// </summary>
public sealed record RoomRegistryEntry(
    [property: JsonPropertyName("roomPath")] string RoomPath,
    [property: JsonPropertyName("projectRoot")] string ProjectRoot,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

/// <summary>
/// Reads and writes <see cref="BatonPaths.RoomRegistryFile"/> — the spec/baton.md §8 multi-project room registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, not a rewritten JSON map.</b> Registrations come from separate, potentially
/// concurrent <c>baton</c> processes (the very situation the registry exists for — spec/baton.md §8
/// carries the design rationale); appending sidesteps the cross-process read-modify-write a
/// rewritten map would force onto every registration.
/// </para>
/// <para>
/// <b>Every access is serialized by a named, machine-wide <see cref="Mutex"/>.</b> <c>FileMode.Append</c>
/// does <em>not</em> give atomic, non-interleaving writes across separate .NET processes on Windows —
/// spec/baton.md §8 records the measurement (unlocked concurrent appenders losing ~1/5 of their
/// lines, some corrupted into unparseable concatenations). <see cref="AppendAsync"/> itself opens with the narrower
/// <c>FileShare.Read</c> (an exclusive write lock, the same choice <see cref="Baton.Store.FlowEventLogWriter"/>
/// makes) — that alone stops the byte-level interleaving above, but it does not stop losses: without
/// the <see cref="Mutex"/>, a second concurrent writer would get a sharing-violation
/// <see cref="IOException"/> instead, which this type's fail-open contract requires swallowing (see
/// below) — a dropped registration rather than corrupted bytes, but still a room <c>fleet_status</c>
/// never learns about. A named <see cref="Mutex"/> keyed on <paramref name="registryFilePath"/> (via
/// the private <c>RunUnderLock</c>) makes at most one process touch the file at a time, for both reads
/// and writes, so a concurrent writer waits and then succeeds instead of losing its registration to a
/// sharing violation — which is what actually delivers "last-writer-wins per room, folded on read"
/// (<see cref="ReadDistinctByRoomAsync"/>) without a single registration lost.
/// The no-lost-entries guarantee is pinned by a many-writer test in <c>RoomRegistryStoreTests</c>.
/// </para>
/// <para>
/// <b>Why every critical section is synchronous, wrapped in one <c>Task.Run</c>, rather than async all
/// the way down.</b> <see cref="Mutex"/> ownership is thread-affine on Windows: the OS thread that
/// calls <see cref="Mutex.WaitOne()"/> is the only one allowed to call <see cref="Mutex.ReleaseMutex"/>.
/// An <c>await</c> between acquiring and releasing can resume on a different thread-pool thread with no
/// synchronization context to pin it — <c>Task.Run</c> still moved the initial call to a worker thread,
/// but that was caught in review: awaiting the
/// stream I/O in between made <c>ReleaseMutex</c> throw <c>"Object synchronization method was called
/// from an unsynchronized block of code"</c> under real concurrency. Keeping acquire, I/O, and release
/// in one synchronous delegate — no <c>await</c> inside it — guarantees they all run on the exact same
/// thread.
/// </para>
/// <para>
/// <b>Fails open, never gates.</b> The registry only ever <em>adds</em> coverage to
/// <c>fleet_status</c>'s existing directory scan (spec/baton.md §8) — it must never be the reason a
/// dispatch fails or a room goes unreported. A write failure, including a lock-acquire timeout, is the
/// caller's concern to log and swallow, not this type's to throw past; a malformed or missing file on
/// read resolves to whatever valid lines could still be parsed (or none), never an exception.
/// </para>
/// </remarks>
public static class RoomRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// How long a caller waits for another process to finish its own registry access before giving up.
    /// Generous on purpose: every critical section under the lock is one small file append or one
    /// whole-file read, never a long-running operation, so contention this long means something else
    /// is genuinely wrong (not a normal fleet-wide race) and the caller's own fail-open handling
    /// (an <see cref="IOException"/>) is the right outcome.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Appends one registration line to <paramref name="registryFilePath"/>, creating the file and its
    /// parent directory if neither exists yet. <paramref name="roomPath"/> and
    /// <paramref name="projectRoot"/> are normalized through <see cref="BatonPaths.RecordKey"/> so every
    /// reader compares registry entries against directory-scan results the same way every other
    /// per-directory record in AER already does.
    /// </summary>
    /// <exception cref="IOException">
    /// Another process held the registry lock for longer than <see cref="LockTimeout"/>. Callers (see
    /// <c>RunCommand.RegisterRoomAsync</c>) treat this the same as any other registry write failure:
    /// log and swallow, never fail the run.
    /// </exception>
    /// <exception cref="WaitHandleCannotBeOpenedException">
    /// A non-mutex kernel object already holds the lock's name — vanishingly unlikely (the name is a
    /// SHA-256 digest) but not impossible. Callers treat this exactly like <see cref="IOException"/>
    /// above: log and swallow.
    /// </exception>
    public static Task AppendAsync(
        string roomPath, string projectRoot, string registryFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomPath);
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);

        var directory = Path.GetDirectoryName(registryFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new RoomRegistryEntry(
            BatonPaths.RecordKey(roomPath), BatonPaths.RecordKey(projectRoot), DateTime.UtcNow);
        var line = JsonSerializer.Serialize(entry, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        return Task.Run(
            () =>
            {
                RunUnderLock(registryFilePath, () =>
                {
                    using var stream = new FileStream(
                        registryFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
                    stream.Write(bytes);
                    stream.Flush();
                });
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads every entry in <paramref name="registryFilePath"/>, folded down to the last entry written
    /// for each distinct <see cref="RoomRegistryEntry.RoomPath"/> (append order is write order, so the
    /// last occurrence in the file is the last-writer-wins value). A missing file resolves to an empty
    /// list; a malformed or empty line is skipped rather than failing the whole read — one bad line
    /// must never hide every well-formed registration around it. Never throws: a lock-acquire timeout,
    /// an I/O failure, or a lock-name collision (see <see cref="WaitHandleCannotBeOpenedException"/> on
    /// <see cref="AppendAsync"/>) all resolve to an empty list, same as a missing file — the caller
    /// (<c>FleetStatusTool</c>) must never fail the whole call because the registry, which only ever
    /// adds coverage, could not be read.
    /// </summary>
    public static Task<IReadOnlyList<RoomRegistryEntry>> ReadDistinctByRoomAsync(
        string registryFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(registryFilePath);

        if (!File.Exists(registryFilePath))
        {
            return Task.FromResult<IReadOnlyList<RoomRegistryEntry>>([]);
        }

        return Task.Run(
            () =>
            {
                string text;
                try
                {
                    text = RunUnderLock(registryFilePath, () =>
                    {
                        using var stream = new FileStream(registryFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return reader.ReadToEnd();
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    Console.Error.WriteLine($"Could not read the room registry at '{registryFilePath}': {ex.Message}.");
                    return (IReadOnlyList<RoomRegistryEntry>)[];
                }

                var byRoom = new Dictionary<string, RoomRegistryEntry>(BatonPaths.RecordKeyComparer);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    RoomRegistryEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<RoomRegistryEntry>(line, SerializerOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (entry is null || string.IsNullOrWhiteSpace(entry.RoomPath) || string.IsNullOrWhiteSpace(entry.ProjectRoot))
                    {
                        continue;
                    }

                    byRoom[entry.RoomPath] = entry;
                }

                return (IReadOnlyList<RoomRegistryEntry>)byRoom.Values.ToList();
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="action"/> holding a named <see cref="Mutex"/> keyed on
    /// <paramref name="registryFilePath"/>, so every process touching the same registry file — reader
    /// or writer — serializes against every other one. Acquire, <paramref name="action"/>, and release
    /// all happen synchronously on the calling thread — see the type's remarks on why an
    /// <c>await</c> inside this cannot be allowed. The path is hashed rather than used verbatim because
    /// a raw path is neither a valid nor a safely short Windows kernel-object name (backslashes,
    /// length limits); the digest just needs to collide only when the paths do — which is exactly why
    /// it is hashed through <see cref="BatonPaths.RecordKey"/> first, not the raw string: two spellings of
    /// the same file (forward vs. backward slashes, a different <c>BATON_HOME</c> casing) must still hash
    /// to the one mutex name, or two processes pointed at the same file take out two different locks
    /// and the registry corruption this mutex exists to prevent is right back.
    /// </summary>
    private static T RunUnderLock<T>(string registryFilePath, Func<T> action)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(BatonPaths.RecordKey(registryFilePath).ToUpperInvariant())));
        using var mutex = new Mutex(initiallyOwned: false, name: $"Global\\baton-room-registry-{digest}");

        bool owned;
        try
        {
            owned = mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A prior holder crashed mid-access. Per Mutex's own contract, ownership still transfers
            // to us when this is thrown -- whatever partial state it left behind is already handled by
            // the tolerant, skip-malformed-lines read path above, not something to react to here.
            owned = true;
        }

        if (!owned)
        {
            throw new IOException($"Timed out after {LockTimeout} waiting for the room registry lock on '{registryFilePath}'.");
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

    private static void RunUnderLock(string registryFilePath, Action action) =>
        RunUnderLock<object?>(registryFilePath, () =>
        {
            action();
            return null;
        });
}
