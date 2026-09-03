using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Domain;

namespace Baton.Status;

/// <summary>
/// One execution's own share of a fleet-level burn ledger (issue #1570, quota-design S4b — full design
/// in the 2026-09-01 proposal comment on #802, section "Where usage is harvested, where the ledger
/// lives, and re-derivability"). Every field independently nullable and omitted
/// (never emitted as <c>null</c>, never fabricated as zero) when the writer had nothing to report for
/// it — the same doctrine <see cref="WorkerUsage"/> and <see cref="ExecutionUsageView"/> already keep,
/// extended to this type rather than re-derived. <see cref="Room"/> and <see cref="Execution"/> are
/// what makes an entry checkable against its source while that source survives (spec/baton.md §7):
/// re-derivability is in-principle, not in-practice, because <c>RoomRetentionSweep</c>
/// moves execution directories out of reach of a rebuild.
/// </summary>
public sealed record QuotaLedgerEntry(
    [property: JsonPropertyName("at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTime? At = null,
    [property: JsonPropertyName("room")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Room = null,
    [property: JsonPropertyName("execution")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Execution = null,
    [property: JsonPropertyName("adapter")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Adapter = null,
    [property: JsonPropertyName("model")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null,
    [property: JsonPropertyName("tokensIn")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensIn = null,
    [property: JsonPropertyName("tokensOut")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? TokensOut = null,
    [property: JsonPropertyName("cacheRead")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheReadTokens = null,
    [property: JsonPropertyName("cacheCreation")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CacheCreationTokens = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? ThinkingTokens = null,
    [property: JsonPropertyName("turns")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Turns = null,
    [property: JsonPropertyName("wallClockMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? WallClockMs = null,
    // The closed token set: FailureClassification's four member names verbatim (Retryable, Permanent,
    // ExhaustedUntil, ToolDenied), or one of Succeeded/Failed/Cancelled/Indeterminate/Arrested for an
    // execution whose terminal event carries no classification -- see QuotaLedgerStore.BuildEntries.
    // Display/grouping only, like WorkflowStatusStepView.FailureKind; nothing parses it back.
    [property: JsonPropertyName("outcome")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null);

/// <summary>
/// Reads and writes <see cref="BatonPaths.QuotaLedgerFile"/> — the spec/baton.md §7 fleet-level burn
/// ledger. Precedent, not a design question (issue #1570): same append-only JSONL shape, same
/// <see cref="MutexGuardedFileLock"/> mechanism, and the same fail-open contract
/// <see cref="RoomRegistryStore"/> already established for <c>room-registry.jsonl</c> — this type
/// shares the mechanism rather than copying it.
/// </summary>
/// <remarks>
/// <b>Fails open, never gates.</b> Like <see cref="RoomRegistryStore"/>, this store only ever adds
/// accounting coverage — it must never be the reason a dispatch, resolve, or any other mutation
/// reports as failed. <see cref="AppendAsync"/> itself still throws
/// (<see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>/<see cref="WaitHandleCannotBeOpenedException"/>)
/// rather than swallowing internally — the caller (<c>Program.cs</c>'s settle-time site) is where the
/// swallow-and-report-on-stderr happens, the same split <see cref="RoomRegistryStore.AppendAsync"/>'s
/// own remarks document and <c>RunCommand.RegisterRoomAsync</c> already performs for the registry. This
/// is the registry's own sanctioned exception to the repo's no-silent-swallow rule: logged on stderr,
/// additive only, and must never gate work that already completed.
/// </remarks>
public static class QuotaLedgerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Same generous timeout <see cref="RoomRegistryStore"/> uses, for the same reason: every
    /// critical section here is one small append or one whole-file read/rewrite, never long-running.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private const string LockNamePrefix = "baton-quota-ledger";

    /// <summary>
    /// Builds one <see cref="QuotaLedgerEntry"/> per execution in <paramref name="entries"/> that has
    /// both a recorded start and exit — the same population
    /// <see cref="ExecutionUsageProjector.BuildByExecutionId"/> yields, reused rather than re-derived
    /// (Architecture Rule 2: no second vendor-envelope reader). An execution missing either lifecycle
    /// event (still running, or Flow crashed before Core recorded one) is entirely absent, same as
    /// there: the accepted loss spec/baton.md §7 documents — "a lane that dies before settling" — is
    /// this same gap, not a second one.
    /// </summary>
    public static IReadOnlyList<QuotaLedgerEntry> BuildEntries(IReadOnlyList<LogEntry> entries, string roomDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(entries, artifactsRootPath, roomDirectoryPath: roomDirectoryPath);

        var adapterByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var modelByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var outcomeByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var exitedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry { Event: CoreEvent.ExecutionExited exited, WriterUtcTimestamp: { } exitedAt })
            {
                exitedAtByExecutionId[exited.ExecutionId.Value] = exitedAt;
            }

            if (entry is not LogEntry.FlowLogEntry flowEntry)
            {
                continue;
            }

            switch (flowEntry.Event)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    var acceptedExecutionId = accepted.Request.ExecutionId.Value;
                    if (accepted.Request.Adapter is { Length: > 0 } adapter)
                    {
                        adapterByExecutionId[acceptedExecutionId] = adapter;
                    }

                    if (accepted.Request.Model is { Length: > 0 } model)
                    {
                        modelByExecutionId[acceptedExecutionId] = model;
                    }

                    break;

                // #1583: a rebound resubmission overrides the frozen ExecutionRequest's recorded
                // adapter/model -- the same divergence ExecutionUsageProjector's own
                // recordedAdapterByExecutionId handles, mirrored here for the same reason.
                case FlowEvent.StepRebound rebound:
                    if (rebound.NewAdapter is { Length: > 0 } newAdapter)
                    {
                        adapterByExecutionId[rebound.ForExecutionId.Value] = newAdapter;
                    }
                    else
                    {
                        adapterByExecutionId.Remove(rebound.ForExecutionId.Value);
                    }

                    if (rebound.NewModel is { Length: > 0 } newModel)
                    {
                        modelByExecutionId[rebound.ForExecutionId.Value] = newModel;
                    }
                    else
                    {
                        modelByExecutionId.Remove(rebound.ForExecutionId.Value);
                    }

                    break;

                case FlowEvent.ExecutionSucceeded succeeded:
                    outcomeByExecutionId[succeeded.ExecutionId.Value] = "Succeeded";
                    break;

                case FlowEvent.ExecutionFailed failed:
                    outcomeByExecutionId[failed.ExecutionId.Value] = failed.FailureClassification?.ToString() ?? "Failed";
                    break;

                case FlowEvent.ExecutionCancelled cancelled:
                    outcomeByExecutionId[cancelled.ExecutionId.Value] = "Cancelled";
                    break;

                case FlowEvent.ExecutionIndeterminate indeterminate:
                    outcomeByExecutionId[indeterminate.ExecutionId.Value] = "Indeterminate";
                    break;

                case FlowEvent.ExecutionArrested arrested:
                    outcomeByExecutionId[arrested.ExecutionId.Value] = "Arrested";
                    break;
            }
        }

        var recordedRoomPath = BatonPaths.RecordKey(roomDirectoryPath);
        var result = new List<QuotaLedgerEntry>(usageByExecutionId.Count);
        foreach (var (executionId, usage) in usageByExecutionId)
        {
            adapterByExecutionId.TryGetValue(executionId, out var resolvedAdapter);
            modelByExecutionId.TryGetValue(executionId, out var resolvedModel);
            outcomeByExecutionId.TryGetValue(executionId, out var outcome);
            var at = exitedAtByExecutionId.TryGetValue(executionId, out var exitedAt) ? exitedAt : (DateTime?)null;

            result.Add(new QuotaLedgerEntry(
                At: at,
                Room: recordedRoomPath,
                Execution: executionId,
                Adapter: resolvedAdapter,
                Model: resolvedModel,
                TokensIn: usage.TokensIn,
                TokensOut: usage.TokensOut,
                CacheReadTokens: usage.CacheReadTokens,
                CacheCreationTokens: usage.CacheCreationTokens,
                ThinkingTokens: usage.ThinkingTokens,
                Turns: usage.Turns,
                WallClockMs: usage.WallClockMs,
                Outcome: outcome));
        }

        return result;
    }

    /// <summary>
    /// Appends one line per <paramref name="entries"/> to <paramref name="ledgerFilePath"/>, creating
    /// the file and its parent directory if neither exists yet, under one acquisition of the
    /// <see cref="MutexGuardedFileLock"/> keyed on this file. A no-op for an empty list — never opens
    /// or creates the file for nothing to write. Throws exactly as
    /// <see cref="RoomRegistryStore.AppendAsync"/> documents: the caller's job to log and swallow, not
    /// this method's.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<QuotaLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        var directory = Path.GetDirectoryName(ledgerFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(JsonSerializer.Serialize(entry, SerializerOptions)).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, () =>
            {
                using var stream = new FileStream(
                    ledgerFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
                stream.Write(bytes);
                stream.Flush();
            }),
            cancellationToken);
    }

    /// <summary>
    /// Reads every parseable, well-formed line in <paramref name="ledgerFilePath"/>, in file (= write)
    /// order. A missing file resolves to an empty list; a malformed line is skipped rather than failing
    /// the whole read, same tolerance <see cref="RoomRegistryStore.ReadDistinctByRoomAsync"/> already
    /// documents. Never throws — a lock-acquire timeout or an I/O failure resolves to an empty list,
    /// same as a missing file.
    /// </summary>
    public static Task<IReadOnlyList<QuotaLedgerEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        if (!File.Exists(ledgerFilePath))
        {
            return Task.FromResult<IReadOnlyList<QuotaLedgerEntry>>([]);
        }

        return Task.Run(
            () =>
            {
                string text;
                try
                {
                    text = MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, () =>
                    {
                        using var stream = new FileStream(ledgerFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return reader.ReadToEnd();
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    Console.Error.WriteLine($"Could not read the quota ledger at '{ledgerFilePath}': {ex.Message}.");
                    return (IReadOnlyList<QuotaLedgerEntry>)[];
                }

                var result = new List<QuotaLedgerEntry>();
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    QuotaLedgerEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<QuotaLedgerEntry>(line, SerializerOptions);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (entry is not null)
                    {
                        result.Add(entry);
                    }
                }

                return (IReadOnlyList<QuotaLedgerEntry>)result;
            },
            cancellationToken);
    }

    /// <summary>
    /// <see cref="ReadAllAsync"/>, folded to the last line written for each distinct
    /// <see cref="QuotaLedgerEntry.Execution"/> (append order is write order, so the last occurrence in
    /// the file is the last-writer-wins value) — the same read-time fold
    /// <see cref="RoomRegistryStore.ReadDistinctByRoomAsync"/> applies for <see cref="QuotaLedgerEntry.Room"/>.
    /// An entry with no <see cref="QuotaLedgerEntry.Execution"/> at all cannot be deduplicated or
    /// merged by anything and is dropped — the doctrine every field is independently absent means this
    /// is reachable, not just defensive.
    /// </summary>
    public static async Task<IReadOnlyList<QuotaLedgerEntry>> ReadDistinctByExecutionAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var byExecution = new Dictionary<string, QuotaLedgerEntry>(StringComparer.Ordinal);
        foreach (var entry in all)
        {
            if (entry.Execution is { Length: > 0 } executionId)
            {
                byExecution[executionId] = entry;
            }
        }

        return byExecution.Values.ToList();
    }

    /// <summary>(Entries the ledger already held, total after the merge, how many were newly recovered by the walk.)</summary>
    public sealed record RebuildResult(int PreviousCount, int TotalCount, int RecoveredCount);

    /// <summary>
    /// Merges <paramref name="freshlyWalkedEntries"/> (a caller's fresh re-derivation from every still-
    /// live room's own <c>flow.jsonl</c>/<c>.stdout.log</c>) into whatever <paramref name="ledgerFilePath"/>
    /// already holds, by <see cref="QuotaLedgerEntry.Execution"/> id — <b>never sums</b>. Starts from
    /// the ledger's own <see cref="ReadDistinctByExecutionAsync"/> content, not from the walk alone: an
    /// execution the ledger already recorded but whose room <c>RoomRetentionSweep</c> has
    /// since pruned is not in the walk, and dropping it would make a rebuild destroy exactly the
    /// past-retention coverage the ledger exists to hold (spec/baton.md §7). A freshly-walked entry for
    /// an execution the ledger already had overwrites that entry — freshly re-derived data from the
    /// still-live source beats whatever was durable before. Rewrites the whole file once, under the
    /// same <see cref="MutexGuardedFileLock"/> every other access takes, so running this twice against
    /// an unchanged fleet produces byte-identical totals both times.
    /// </summary>
    public static async Task<RebuildResult> RebuildAsync(
        IReadOnlyList<QuotaLedgerEntry> freshlyWalkedEntries, string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshlyWalkedEntries);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        var existing = await ReadDistinctByExecutionAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var merged = new Dictionary<string, QuotaLedgerEntry>(StringComparer.Ordinal);
        foreach (var entry in existing)
        {
            if (entry.Execution is { Length: > 0 } executionId)
            {
                merged[executionId] = entry;
            }
        }

        var recoveredCount = 0;
        foreach (var entry in freshlyWalkedEntries)
        {
            if (entry.Execution is not { Length: > 0 } executionId)
            {
                continue;
            }

            if (!merged.ContainsKey(executionId))
            {
                recoveredCount++;
            }

            merged[executionId] = entry;
        }

        await WriteAllAsync(merged.Values.ToList(), ledgerFilePath, cancellationToken).ConfigureAwait(false);
        return new RebuildResult(existing.Count, merged.Count, recoveredCount);
    }

    /// <summary>
    /// Replaces <paramref name="ledgerFilePath"/>'s entire contents with one JSON line per
    /// <paramref name="entries"/>, via a temp-file-then-move so a concurrent reader under the same
    /// <see cref="MutexGuardedFileLock"/> never observes a truncated file — the same atomic-replace
    /// discipline <see cref="RoomRegistryStore"/>'s own compaction uses.
    /// </summary>
    private static Task WriteAllAsync(
        IReadOnlyList<QuotaLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken) =>
        Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, () =>
            {
                var directory = Path.GetDirectoryName(ledgerFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = $"{ledgerFilePath}.{Guid.NewGuid():N}.tmp";
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    builder.Append(JsonSerializer.Serialize(entry, SerializerOptions)).Append('\n');
                }

                File.WriteAllText(tempPath, builder.ToString(), Encoding.UTF8);
                File.Move(tempPath, ledgerFilePath, overwrite: true);
            }),
            cancellationToken);
}
