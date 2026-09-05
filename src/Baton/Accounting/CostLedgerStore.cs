using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Status;

namespace Baton.Accounting;

/// <summary>
/// Reads and writes the repository-keyed cost ledger (#1849 phase A) —
/// <c>{BatonPaths.Root}/ledger/&lt;repo-slug&gt;.jsonl</c>, one immutable append-only row per settled
/// execution attempt. Shares the whole append-only JSONL store — <see cref="JsonLinesLedger{TEntry}"/>,
/// and through it <see cref="MutexGuardedFileLock"/> — with <c>QuotaLedgerStore</c> (#1884) rather than
/// introducing a second copy of it or a third concurrency mechanism, under its own lock name prefix so
/// the three files never contend with each other.
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
    /// <summary>
    /// This ledger's shared store — <see cref="JsonLinesLedger{TEntry}"/>, whose own remarks state what
    /// it guarantees and why the prefix handed to it here is not free to rename. <c>baton-cost-ledger</c>
    /// is deliberately unlike <c>QuotaLedgerStore</c>'s and <c>RoomRegistryStore</c>'s, so the three
    /// files never contend.
    /// </summary>
    internal static readonly JsonLinesLedger<CostLedgerEntry> Ledger =
        new("baton-cost-ledger", "cost ledger", entry => entry.Execution);

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
    /// <param name="runwayOverrideReasonByWorker">
    /// #1848: worker name to the runway-override reason recorded on that worker's binding at dispatch,
    /// for overrides that actually bypassed a Hold. Supplied by the settle site (which can read
    /// <c>bindings.json</c>; this layer cannot see <c>Baton.Vendors</c>), null everywhere else —
    /// <see cref="CostLedgerEntry.RunwayOverrideReason"/>'s own doc states what an absent value means
    /// and does not mean.
    /// </param>
    /// <param name="metadataByExecutionId">
    /// Issue/PR and pushed-diff facts collected at settle by the CLI's injected forge/git lookup.
    /// Missing executions and missing members remain absent; this layer never performs network I/O.
    /// </param>
    public static IReadOnlyList<CostLedgerEntry> BuildEntries(
        IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath,
        RepositoryIdentity? repository,
        PriceCatalog? catalog = null,
        PlanFactorTable? planFactors = null,
        IReadOnlyDictionary<string, string>? runwayOverrideReasonByWorker = null,
        IReadOnlyDictionary<string, CostLedgerExecutionMetadata>? metadataByExecutionId = null)
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
            var metadata = metadataByExecutionId is not null
                && metadataByExecutionId.TryGetValue(executionId, out var capturedMetadata)
                    ? capturedMetadata
                    : null;
            var review = TryReadReviewFields(artifactsRootPath, executionId);

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
                Issue: metadata?.Issue,
                PullRequest: metadata?.PullRequest,
                StartedAt: startedAt,
                EndedAt: endedAt,
                TokensIn: usage.TokensIn,
                TokensOut: usage.TokensOut,
                CacheReadTokens: usage.CacheReadTokens,
                CacheCreationTokens: usage.CacheCreationTokens,
                ThinkingTokens: usage.ThinkingTokens,
                Turns: usage.Turns,
                WallClockMs: usage.WallClockMs,
                // #1882: carried through as the projector attributed them -- one execution's row gets
                // both, every other row gets neither. No arithmetic here on purpose.
                VerifyStepMs: usage.VerifyStepMs,
                VerifyResultsBytes: usage.VerifyResultsBytes,
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
                PlanFactorTableVersion: planFactors.Version,
                RunwayOverrideReason: request?.Worker is { } worker && runwayOverrideReasonByWorker is not null
                    && runwayOverrideReasonByWorker.TryGetValue(worker, out var runwayReason)
                        ? runwayReason
                        : null,
                Verdict: review?.Verdict,
                FindingsHigh: review?.FindingsHigh,
                FindingsMedium: review?.FindingsMedium,
                FindingsLow: review?.FindingsLow,
                ReviewedPr: review?.ReviewedPr,
                ReviewedHead: review?.ReviewedHead,
                FilesChanged: metadata?.FilesChanged,
                Additions: metadata?.Additions,
                Deletions: metadata?.Deletions,
                TestFilesChanged: metadata?.TestFilesChanged));
        }

        return result;
    }

    private const string VerdictOutputName = "verdict.json";

    private static readonly Regex ReviewedPrPattern = new(
        @"(?:\bpull/|\bPR\s*#)\s*(?<number>[1-9]\d*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HashReferencePattern = new(
        @"(?<!\w)#(?<number>[1-9]\d*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IssueReferencePattern = new(
        @"\bissues?(?:\s*#|/)\s*[1-9]\d*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabeledHeadPattern = new(
        @"(?:@|\bhead\s*[:=])\s*(?<sha>[0-9a-f]{40})(?=$|[\s,;])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record ReviewFields(
        string Verdict,
        int FindingsHigh,
        int FindingsMedium,
        int FindingsLow,
        int? ReviewedPr,
        string? ReviewedHead);

    /// <summary>
    /// Reads the same schema-checked verdict artifact <c>DispatchCommand</c> stamps after a review.
    /// Invalid, absent or unreadable worker content enriches nothing and never suppresses the cost row.
    /// Only confirmed findings enter the counts: refuted and unverified suspicions remain in the source
    /// verdict as evidence, but do not turn its derived ledger verdict into <c>BLOCK</c>.
    /// </summary>
    private static ReviewFields? TryReadReviewFields(string artifactsRootPath, string executionId)
    {
        var verdictPath = Path.Combine(
            ArtifactManager.ResolveOutputDirectory(artifactsRootPath, new ExecutionId(executionId)),
            VerdictOutputName);
        if (!File.Exists(verdictPath))
        {
            return null;
        }

        ReviewVerdict? verdict;
        try
        {
            if (!ReviewVerdictSchema.TryParse(File.ReadAllBytes(verdictPath), out verdict, out _))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var confirmed = verdict!.Findings
            .Where(finding => finding.Status == ReviewFindingStatus.Confirmed)
            .ToList();
        return new ReviewFields(
            confirmed.Count == 0 ? "APPROVE" : "BLOCK",
            confirmed.Count(finding => finding.Severity == ReviewFindingSeverity.High),
            confirmed.Count(finding => finding.Severity == ReviewFindingSeverity.Medium),
            confirmed.Count(finding => finding.Severity == ReviewFindingSeverity.Low),
            ParseReviewedPr(verdict.ReviewedRef),
            ParseReviewedHead(verdict.ReviewedRef));
    }

    private static int? ParseReviewedPr(string reviewedRef)
    {
        var trimmed = reviewedRef.Trim();
        var match = ReviewedPrPattern.Match(trimmed);
        if (!match.Success)
        {
            if (IssueReferencePattern.IsMatch(trimmed))
            {
                return null;
            }

            match = HashReferencePattern.Match(trimmed);
        }

        return match.Success
            && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }

    private static string? ParseReviewedHead(string reviewedRef)
    {
        var trimmed = reviewedRef.Trim();
        if (IsHexSha(trimmed))
        {
            return trimmed;
        }

        var match = LabeledHeadPattern.Match(trimmed);
        return match.Success ? match.Groups["sha"].Value : null;
    }

    private static bool IsHexSha(string value) =>
        value.Length == 40
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

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
    /// The filter itself, and every mechanical guarantee around it, belongs to
    /// <see cref="JsonLinesLedger{TEntry}.AppendAsync"/>. Throws exactly as
    /// <c>QuotaLedgerStore.AppendAsync</c> documents — the caller logs and swallows.
    /// </summary>
    public static Task AppendAsync(
        IReadOnlyList<CostLedgerEntry> entries, string ledgerFilePath, CancellationToken cancellationToken = default) =>
        Ledger.AppendAsync(entries, ledgerFilePath, cancellationToken);

    /// <summary>
    /// Appends a replacement for the room's last physical row carrying the conductor's
    /// <c>close</c>/<c>reject</c> resolution (#1901 C1). The existing line is never rewritten:
    /// append-only history remains auditable, while <see cref="ReadAllAsync"/> folds repeated execution
    /// ids last-write-wins so accounting views still count the attempt once.
    /// </summary>
    /// <returns><see langword="true"/> when a matching row was found and corrected; otherwise false.</returns>
    public static Task<bool> AppendResolutionAsync(
        string roomDirectoryPath,
        string resolution,
        string resolutionReason,
        string ledgerFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(resolutionReason);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);
        if (resolution is not ("close" or "reject"))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Resolution must be 'close' or 'reject'.");
        }

        var recordedRoom = BatonPaths.RecordKey(roomDirectoryPath);
        return Ledger.RunUnderLockAsync(
            ledgerFilePath,
            () =>
            {
                var last = Ledger.ReadAllUnlocked(ledgerFilePath)
                    .LastOrDefault(row => row.Room is not null
                        && BatonPaths.RecordKeyComparer.Equals(row.Room, recordedRoom));
                if (last is null)
                {
                    return false;
                }

                var correction = last with
                {
                    Resolution = resolution,
                    ResolutionReason = resolutionReason,
                };
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(correction, Ledger.SerializerOptions) + "\n");
                using var stream = new FileStream(
                    ledgerFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: false);
                stream.Write(bytes);
                stream.Flush();
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// This ledger's logical rows, oldest first. Physical correcting rows are folded by execution id,
    /// last-write-wins, at the original row's position; rows without an execution id remain distinct.
    /// </summary>
    public static async Task<IReadOnlyList<CostLedgerEntry>> ReadAllAsync(
        string ledgerFilePath, CancellationToken cancellationToken = default)
    {
        var physical = await Ledger.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var logical = new List<CostLedgerEntry>(physical.Count);
        var indexByExecution = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in physical)
        {
            if (row.Execution is not { Length: > 0 } execution)
            {
                logical.Add(row);
                continue;
            }

            if (indexByExecution.TryGetValue(execution, out var index))
            {
                logical[index] = row;
            }
            else
            {
                indexByExecution[execution] = logical.Count;
                logical.Add(row);
            }
        }

        return logical;
    }
}
