using System.Text;
using System.Text.Json;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Status;

namespace Baton.Accounting;

/// <summary>
/// Reads and writes the repository-keyed cost ledger (#1849 phase A) —
/// <c>{BatonPaths.Root}/ledger/&lt;repo-slug&gt;.jsonl</c>, one immutable append-only row per settled
/// execution attempt. Shares <see cref="MutexGuardedFileLock"/> with <c>QuotaLedgerStore</c> and
/// <c>RoomRegistryStore</c> rather than introducing a third concurrency mechanism, under its own lock
/// name prefix so the three files never contend with each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consumes <c>quota-ledger.jsonl</c>'s source; never replaces it.</b> Token dimensions come from
/// <see cref="ExecutionUsageProjector.BuildByExecutionId"/> and adapter/model from
/// <see cref="ExecutionBindingResolver.Resolve"/> — the same two primitives <c>QuotaLedgerStore</c>
/// reads, so there is exactly one vendor-envelope reader in the tree (Architecture Rule 2) and the two
/// ledgers can never disagree about what an execution spent. What this ledger adds is durability past
/// a room's retention, a repository-level key, and versioned price provenance.
/// </para>
/// <para>
/// <b>Fails open, never gates</b> — identical posture to <c>QuotaLedgerStore</c>: this store only ever
/// adds accounting coverage, so <see cref="AppendAsync"/> throwing is the settle-site caller's to log
/// on stderr and swallow, never a reason a run that already reached Terminal reports as failed.
/// </para>
/// </remarks>
public static class CostLedgerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>Same generous timeout the other two stores use, for the same reason: every critical section here is one small append or one whole-file read.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Distinct from <c>QuotaLedgerStore</c>'s and <c>RoomRegistryStore</c>'s prefixes, per
    /// <see cref="MutexGuardedFileLock"/>'s own remarks. Changing this string renames the lock and lets
    /// an older and a newer <c>baton</c> build write the same file under two different mutexes.
    /// </summary>
    private const string LockNamePrefix = "baton-cost-ledger";

    /// <summary><c>estimateReason</c> when the tokens are a sum across more than one model, so no single rate applies to them.</summary>
    internal const string MultiModelUsageReason = "multi-model-usage";

    /// <summary><c>estimateReason</c> when the breakdown names one model and the binding asked for a different one.</summary>
    internal const string ModelMismatchReason = "model-mismatch";

    /// <summary>
    /// Builds one <see cref="CostLedgerEntry"/> per execution in <paramref name="entries"/> that has
    /// both a recorded start and exit — the same population <c>QuotaLedgerStore.BuildEntries</c> yields,
    /// for the same stated reason: an execution missing a lifecycle event has no wall-clock to derive
    /// and is absent rather than reported as zero (spec/baton.md §7's accepted loss, not a second one).
    /// <b>A retry or redispatch is a separate row</b>, with no extra machinery: every dispatch mints a
    /// fresh <c>ExecutionId</c>, so two attempts of one step are two executions here.
    /// </summary>
    /// <param name="repository">
    /// The canonical repository identity this room's work belongs to. Never derived from the room path:
    /// worktrees of one repository must share one ledger (<see cref="RepositoryIdentity"/>).
    /// </param>
    /// <param name="catalog">Defaults to <see cref="PriceCatalog.Default"/>. Its id/version is stamped on every row it prices.</param>
    /// <param name="planFactors">Defaults to <see cref="PlanFactorTable.Default"/>. Same stamping rule.</param>
    public static IReadOnlyList<CostLedgerEntry> BuildEntries(
        IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath,
        RepositoryIdentity? repository,
        PriceCatalog? catalog = null,
        PlanFactorTable? planFactors = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        catalog ??= PriceCatalog.Default;
        planFactors ??= PlanFactorTable.Default;

        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(entries, artifactsRootPath, roomDirectoryPath: roomDirectoryPath);
        var resolvedBindings = ExecutionBindingResolver.Resolve(entries);

        var outcomeByExecutionId = new Dictionary<string, string>(StringComparer.Ordinal);
        var startedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var exitedAtByExecutionId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var requestByExecutionId = new Dictionary<string, ExecutionRequest>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry is LogEntry.CoreLogEntry { WriterUtcTimestamp: { } timestamp } coreEntry)
            {
                switch (coreEntry.Event)
                {
                    case CoreEvent.ExecutionStarted started:
                        startedAtByExecutionId[started.ExecutionId.Value] = timestamp;
                        break;
                    case CoreEvent.ExecutionExited exited:
                        exitedAtByExecutionId[exited.ExecutionId.Value] = timestamp;
                        break;
                }
            }

            if (entry is not LogEntry.FlowLogEntry flowEntry)
            {
                continue;
            }

            switch (flowEntry.Event)
            {
                case FlowEvent.ExecutionRequestAccepted accepted:
                    requestByExecutionId[accepted.Request.ExecutionId.Value] = accepted.Request;
                    break;

                // The same closed outcome token set QuotaLedgerEntry.Outcome documents -- one
                // vocabulary across both ledgers, so a filter written against one works on the other.
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
        var result = new List<CostLedgerEntry>(usageByExecutionId.Count);

        foreach (var (executionId, usage) in usageByExecutionId)
        {
            resolvedBindings.TryGetValue(executionId, out var binding);
            outcomeByExecutionId.TryGetValue(executionId, out var outcome);
            requestByExecutionId.TryGetValue(executionId, out var request);
            var startedAt = startedAtByExecutionId.TryGetValue(executionId, out var s) ? s : (DateTime?)null;
            var endedAt = exitedAtByExecutionId.TryGetValue(executionId, out var e) ? e : (DateTime?)null;

            // Priced as of when the attempt ENDED -- the instant the work is attributed to, so a
            // catalog range that opened mid-attempt does not silently reprice it on the next read.
            var pricedAt = endedAt ?? startedAt ?? DateTime.UtcNow;

            var tokens = new TokenDimensions(
                Input: usage.TokensIn,
                Output: usage.TokensOut,
                CacheRead: usage.CacheReadTokens,
                CacheCreation: usage.CacheCreationTokens,
                Thinking: usage.ThinkingTokens);

            var (apiUsd, apiStatus, planUsd, planStatus, estimateReason) =
                Estimate(catalog, planFactors, binding.Adapter, binding.Model, tokens, usage.ModelsObserved, pricedAt);

            var unavailableReason = usage.BilledReconciliationUnavailable;
            var completeness = ResolveCompleteness(unavailableReason, usage.BilledTokens);

            result.Add(new CostLedgerEntry(
                SourceKind: CostSourceKind.BatonExecution,
                Repository: repository?.Value,
                Room: recordedRoomPath,
                Workflow: request?.WorkflowId.Value,
                Step: request?.StepId?.Value,
                Execution: executionId,
                Role: request?.Worker,
                Adapter: binding.Adapter,
                Model: binding.Model,
                ModelsObserved: usage.ModelsObserved,
                Outcome: outcome,
                StartedAt: startedAt,
                EndedAt: endedAt,
                TokensIn: usage.TokensIn,
                TokensOut: usage.TokensOut,
                CacheReadTokens: usage.CacheReadTokens,
                CacheCreationTokens: usage.CacheCreationTokens,
                ThinkingTokens: usage.ThinkingTokens,
                Turns: usage.Turns,
                WallClockMs: usage.WallClockMs,
                BilledTokens: usage.BilledTokens,
                LiveBilledTokens: usage.LiveBilledTokens,
                BilledUnderReadTokens: usage.BilledUnderReadTokens,
                PeakBilledInWindow: usage.PeakBilledInWindow,
                Completeness: completeness,
                CompletenessReason: unavailableReason,
                ApiEquivalentUsd: apiUsd,
                EstimateStatus: apiStatus,
                PlanMeterEstimateUsd: planUsd,
                PlanMeterEstimateStatus: planStatus,
                EstimateReason: estimateReason,
                PriceCatalogId: catalog.Id,
                PriceCatalogVersion: catalog.Version,
                PlanFactorTableId: planFactors.Id,
                PlanFactorTableVersion: planFactors.Version));
        }

        return result;
    }

    /// <summary>
    /// How much of an attempt a row accounts for, from the two things the stream reader reports about
    /// it (#1883 review F2). Three states, and these two arguments decide between them exhaustively:
    /// <c>ExecutionUsageView</c>'s reconciliation triple is all-present-or-none, so a null
    /// <paramref name="unavailableReason"/> holds in exactly two cases and
    /// <paramref name="billedTokens"/> separates them.
    /// <list type="bullet">
    /// <item>A reason — ANY of <c>ExecutionUsageView.KnownUnavailableReasons</c> — is
    /// <see cref="CostCompleteness.Partial"/>. That deliberately includes the two that are not about
    /// truncation at all: <c>ExecutionUsageView.NoTerminalBilledFigureReason</c> (whose own doc has the
    /// two cases it conflates) and <c>ExecutionUsageView.NoLiveBilledFigureReason</c>, where the
    /// terminal line parsed but the replay over the same bytes read no usage line. Neither is provably
    /// whole, and #1849's own doctrine is that an undecidable case reads as the weaker claim. Mapping
    /// every reason rather than an enumerated subset is also what stops a reason added to the producer
    /// from silently landing here as <see cref="CostCompleteness.Complete"/>.</item>
    /// <item>No reason and a billed figure means reconciled — a terminal line parsed AND the replay
    /// completed — which is the only <see cref="CostCompleteness.Complete"/>.</item>
    /// <item>No reason and no billed figure means the usage was never read at all: no parser registered
    /// for the adapter, or no captured <c>.stdout.log</c>. <see langword="null"/>, i.e. the field is
    /// ABSENT on the row. Labelling that <c>complete</c> put an attempt carrying no dimensions into the
    /// same trustworthy set as a fully-captured one, which is the defect this replaces.</item>
    /// </list>
    /// </summary>
    internal static CostCompleteness? ResolveCompleteness(string? unavailableReason, long? billedTokens) =>
        unavailableReason is not null
            ? CostCompleteness.Partial
            : billedTokens is not null
                ? CostCompleteness.Complete
                : null;

    /// <summary>
    /// The two labelled estimates and their statuses. The plan-meter half resolves its FACTOR status
    /// first, so an unmeasured vendor reads <c>unmeasured</c> and a live discount window of unknown
    /// size reads <c>unknown</c> — both of which say more than the <c>unpriced</c> an empty catalog
    /// would otherwise flatten them into. There is no 1.0 fallback anywhere in this method: an
    /// unresolvable factor yields no number at all.
    /// <para>
    /// <b>#1883 review F1: nothing is priced unless <paramref name="tokens"/> is attributable to
    /// <paramref name="model"/>.</b> spec/baton.md §7 carries the ruling and what it costs; the
    /// mechanics are that <paramref name="modelsObserved"/> is the vendor's own breakdown of the very
    /// figures in <paramref name="tokens"/> (see <see cref="Domain.WorkerUsage.ModelsObserved"/>) while
    /// <paramref name="model"/> is <see cref="ExecutionBindingResolver"/>'s, i.e. what was ASKED FOR —
    /// so anything other than "one model, and it is that one" leaves both estimates absent with a
    /// reason. A <see langword="null"/> <paramref name="modelsObserved"/> is not a refusal: it is the
    /// no-breakdown-reported case this ledger has always priced.
    /// </para>
    /// </summary>
    private static (decimal? ApiUsd, EstimateStatus ApiStatus, decimal? PlanUsd, EstimateStatus PlanStatus, string? Reason) Estimate(
        PriceCatalog catalog,
        PlanFactorTable planFactors,
        string? adapter,
        string? model,
        TokenDimensions tokens,
        IReadOnlyList<string>? modelsObserved,
        DateTime at)
    {
        if (modelsObserved is { Count: > 0 } observed)
        {
            if (observed.Count > 1)
            {
                return (null, EstimateStatus.Unpriced, null, EstimateStatus.Unpriced, MultiModelUsageReason);
            }

            if (model is not { Length: > 0 } || !string.Equals(observed[0], model, StringComparison.OrdinalIgnoreCase))
            {
                return (null, EstimateStatus.Unpriced, null, EstimateStatus.Unpriced, ModelMismatchReason);
            }
        }

        var apiUsd = catalog.TryEstimateUsd(adapter, model, tokens, at);
        var apiStatus = apiUsd is null ? EstimateStatus.Unpriced : EstimateStatus.Estimated;

        var resolution = planFactors.Resolve(adapter, model, at);
        switch (resolution.Status)
        {
            case PlanFactorStatus.Unmeasured:
                return (apiUsd, apiStatus, null, EstimateStatus.Unmeasured, null);
            case PlanFactorStatus.Unknown:
                return (apiUsd, apiStatus, null, EstimateStatus.Unknown, null);
        }

        decimal weighted = 0m;
        var priced = false;
        foreach (var (dimension, count) in tokens.Present())
        {
            if (catalog.TryRate(adapter, model, dimension, at) is not { } rate)
            {
                return (apiUsd, apiStatus, null, EstimateStatus.Unpriced, null);
            }

            var weight = resolution.Weights.TryGetValue(dimension, out var w) ? w : 1m;
            weighted += rate * weight * count / 1_000_000m;
            priced = true;
        }

        return priced
            ? (apiUsd, apiStatus, weighted * resolution.DiscountMultiplier, EstimateStatus.Estimated, null)
            : (apiUsd, apiStatus, null, EstimateStatus.Unpriced, null);
    }

    /// <summary>
    /// Appends the subset of <paramref name="entries"/> whose <see cref="CostLedgerEntry.Execution"/>
    /// is not already present in <paramref name="ledgerFilePath"/>, in one read-check-then-append
    /// critical section.
    /// <b>Why the skip exists, and what it is not.</b> <c>Program.cs</c>'s settle-time call site fires
    /// on every command that carries a room to Terminal — including a re-run of an already-terminal
    /// room, <c>supply</c>, and the <c>resolve --reject</c> → re-Terminal path — and each of those
    /// re-derives <see cref="BuildEntries"/> over the WHOLE room rather than only what changed. Without
    /// this check a room settling twice writes every one of its executions twice, and an append-only
    /// accounting ledger that double-counts is worse than one that is missing rows: the totals every
    /// consumer (#1391's drill-down, #1848's enforcement) reads would silently inflate. It does NOT
    /// collapse retries: a retry is a different <c>ExecutionId</c> and therefore a different row.
    /// An entry with no <see cref="CostLedgerEntry.Execution"/> cannot be deduplicated against
    /// anything and is always appended. Creates the file and its parent directory if neither exists; a
    /// no-op when nothing survives the filter, never opening the file to write zero bytes. Throws
    /// exactly as <c>QuotaLedgerStore.AppendAsync</c> documents — the caller logs and swallows.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<CostLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default)
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

        return Task.Run(
            () => MutexGuardedFileLock.RunUnderLock(ledgerFilePath, LockNamePrefix, LockTimeout, () =>
            {
                var alreadyRecorded = ReadAllUnlocked(ledgerFilePath)
                    .Where(e => e.Execution is { Length: > 0 })
                    .Select(e => e.Execution!)
                    .ToHashSet(StringComparer.Ordinal);

                var toAppend = entries
                    .Where(e => e.Execution is not { Length: > 0 } id || !alreadyRecorded.Contains(id))
                    .ToList();
                if (toAppend.Count == 0)
                {
                    return;
                }

                var builder = new StringBuilder();
                foreach (var entry in toAppend)
                {
                    builder.Append(JsonSerializer.Serialize(entry, SerializerOptions)).Append('\n');
                }

                var bytes = Encoding.UTF8.GetBytes(builder.ToString());
                using var stream = new FileStream(
                    ledgerFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
                stream.Write(bytes);
                stream.Flush();
            }),
            cancellationToken);
    }

    /// <summary>
    /// Reads every parseable line in <paramref name="ledgerFilePath"/>, in file (= write) order, under
    /// the <see cref="MutexGuardedFileLock"/> keyed on this file. A missing file resolves to an empty
    /// list; a malformed line is skipped rather than failing the whole read. Never throws — a
    /// lock-acquire timeout or an I/O failure resolves to an empty list, same as a missing file.
    /// </summary>
    public static Task<IReadOnlyList<CostLedgerEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);

        return Task.Run(
            () =>
            {
                try
                {
                    return MutexGuardedFileLock.RunUnderLock(
                        ledgerFilePath, LockNamePrefix, LockTimeout, () => ReadAllUnlocked(ledgerFilePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    Console.Error.WriteLine($"Could not read the cost ledger at '{ledgerFilePath}': {ex.Message}.");
                    return (IReadOnlyList<CostLedgerEntry>)[];
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// The read half, factored out so <see cref="AppendAsync"/> can read-then-write inside ONE lock
    /// acquisition — two acquisitions would let a concurrent writer land in the gap. Callers must
    /// already hold the lock; this method takes none of its own.
    /// </summary>
    private static IReadOnlyList<CostLedgerEntry> ReadAllUnlocked(string ledgerFilePath)
    {
        if (!File.Exists(ledgerFilePath))
        {
            return [];
        }

        string text;
        using (var stream = new FileStream(ledgerFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        var result = new List<CostLedgerEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            CostLedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CostLedgerEntry>(line, SerializerOptions);
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

        return result;
    }
}
