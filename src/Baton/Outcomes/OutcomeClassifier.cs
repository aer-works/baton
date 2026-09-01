using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Outcomes;

/// <summary>The three terminal outcomes a completed dispatch is classified into.</summary>
public enum OutcomeVerdict
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// The classified result of a completed dispatch — the input to whichever
/// <see cref="Domain.FlowEvent"/> terminal case the <c>MutationInterface</c> appends to the log.
/// </summary>
/// <param name="Reason">
/// A human-readable diagnostic for a <see cref="OutcomeVerdict.Failed"/> verdict — why exit code,
/// exit reason, and contract state add up to a failure, computed once here from data available at
/// classification time. Distinct from <paramref name="FailureClassification"/>, which is the
/// worker's own self-reported retry hint, not a diagnostic Flow derives. Every failure path
/// <i>in this class</i> sets it, and it is null for
/// <see cref="OutcomeVerdict.Succeeded"/> and <see cref="OutcomeVerdict.Cancelled"/>.
/// <para>
/// That is deliberately a claim about this class and not about stored events. An earlier version of
/// this comment inferred that a null <c>Reason</c> on a persisted
/// <see cref="Domain.FlowEvent.ExecutionFailed"/> therefore means "written before this field
/// existed" — which nothing enforces, since <c>Reason</c> is an optional parameter any call site may
/// omit in silence, and several test fixtures already write real <c>flow.jsonl</c> lines that do.
/// Treat a null on a stored event as "no reason recorded", never as evidence of when it was written.
/// </para>
/// </param>
/// <param name="CapturedResponseFile">
/// #1594, conductor-writes shape (owner ruling, 2026-09-01, on #1606): carries
/// <see cref="OutputMaterializer.CapturedResponse.FileName"/> (see that record's own doc comment for
/// what the pairing with <paramref name="UnsatisfiedOutputNames"/> means and why it's non-null only on
/// a capture) onto the classification. Verdict-independent by construction — this field lives on the
/// classification itself rather than being tied to one <see cref="OutcomeVerdict"/> case, the way the
/// pre-ruling <c>MaterializedOutputs</c> field was tied to <see cref="OutcomeVerdict.Succeeded"/> alone
/// and so went unrecorded whenever an unrelated later gate flipped the verdict.
/// </param>
/// <param name="UnsatisfiedOutputNames">
/// <see cref="OutputMaterializer.CapturedResponse.UnsatisfiedOutputNames"/>, carried the same hop.
/// </param>
public sealed record OutcomeClassification(
    OutcomeVerdict Verdict,
    FailureClassification? FailureClassification = null,
    string? Reason = null,
    DateTimeOffset? RetryNotBefore = null,
    string? CapturedResponseFile = null,
    IReadOnlyList<string>? UnsatisfiedOutputNames = null);

/// <summary>
/// Maps a <see cref="CoreDispatchResult"/> plus a step's <see cref="WorkerContract"/> into one of
/// the three terminal classifications. Flow alone interprets Core's purely
/// mechanical report (exit code + reason) — Core itself has no notion of "success" beyond that.
/// </summary>
public static class OutcomeClassifier
{
    private const int MaxReasonLength = 500;

    /// <summary>
    /// How many unsatisfied outputs a reason names before summarising the rest as "(+N more)".
    /// A contract with more failures than this has a problem the count communicates better than
    /// the list would.
    /// </summary>
    private const int MaxListedOutputs = 8;

    /// <summary>
    /// How much of <see cref="CoreDispatchResult.StderrTail"/> a reason renders (#563).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must stay strictly below <see cref="CoreDispatcher.MaxRetainedStderrLength"/>, and that is
    /// load-bearing rather than incidental. Two caps sit in series — the dispatcher's buffer cap and
    /// this one — but only this one emits a marker. Keeping this the tighter of the two means any
    /// stderr long enough to hit the *silent* cap is necessarily long enough to hit the *marked* one
    /// as well, so an operator is never shown a tail that had content dropped without an ellipsis
    /// saying so. Raise this above that constant and truncation becomes invisible again.
    /// Asserted by <c>OutcomeClassifierTests</c> rather than left to this comment.
    /// </para>
    /// <para>
    /// That argument requires both caps to count the <i>same</i> characters, which is why
    /// <see cref="StderrTailBuffer"/> collapses whitespace at capture time rather than this class
    /// doing it on the way out. While the collapse sat between the two caps the comparison was
    /// between different units and the guarantee was simply false — mostly-whitespace stderr could
    /// lose thousands of characters silently and still fit under this cap unmarked. Moving a
    /// collapse back downstream of the retention cap reintroduces that, whatever the two numbers say.
    /// </para>
    /// </remarks>
    internal const int MaxStderrTailInReason = 350;

    /// <summary>
    /// Classifies <paramref name="result"/> per this table:
    /// <c>NaturalExit + code 0 + all ProducedOutputs satisfied</c> → Succeeded;
    /// <c>NaturalExit</c> otherwise, or <c>TimedOut</c> → Failed;
    /// <c>CancelRequested</c> → Cancelled.
    /// </summary>
    public static OutcomeClassification Classify(
        CoreDispatchResult result,
        WorkerContract contract,
        string outputDirectory,
        IFailureClassifier? failureClassifier = null,
        TimeProvider? timeProvider = null,
        GrantAuditMode grantAuditMode = GrantAuditMode.Enforced,
        string? worktreePath = null,
        IWorkerResponseParser? responseParser = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        if (result.Reason == CoreExitReason.CancelRequested)
        {
            // A cancellation is never classified as a failure, and is never retried.
            return new OutcomeClassification(OutcomeVerdict.Cancelled);
        }

        if (result.Reason == CoreExitReason.TimedOut)
        {
            // #1089: a worker can finish its declared work and then hang at process teardown (agy holds a
            // scratch handle and never exits), which WithTimeout kills and reads as TimedOut. A timeout
            // otherwise fails regardless of outputs -- deliberately, because a bare timeout
            // cannot tell "finished then hung" from "killed mid-write with a half-written output". The
            // worker's own terminal success marker (CoreDispatchResult.TerminalSuccessObserved) IS that
            // discriminator: when it was observed AND every declared output is present, the contract is
            // genuinely satisfied and a from-scratch retry (RetryPolicy.MaxAttempts) only rebuilds work that
            // already exists. Absent the marker -- no stream, killed mid-work, or crash-recovery -- this
            // falls through to today's behaviour, so the guard fails safe.
            if (result.TerminalSuccessObserved && ContractValidator.IsSatisfied(contract, outputDirectory))
            {
                return new OutcomeClassification(OutcomeVerdict.Succeeded);
            }

            var (classification, retryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                classification,
                WithStderr("Execution timed out.", result.StderrTail),
                retryNotBefore);
        }

        // Only CoreExitReason.Natural remains.
        if (result.ExitCode != 0)
        {
            var (classification, retryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                classification,
                WithStderr($"Worker exited with non-zero code {result.ExitCode}.", result.StderrTail),
                retryNotBefore);
        }

        var validation = ContractValidator.Validate(contract, outputDirectory);
        if (!validation.IsSatisfied)
        {
            // #1594, conductor-writes shape (owner ruling, 2026-09-01, on #1606): the worker exited 0
            // -- it did not crash mid-write -- but a declared output is absent. Give OutputMaterializer
            // a chance to extract the worker's own terminal response into an engine-owned file (see
            // that class's own remarks for why it never touches the declared output directory); this
            // NEVER re-validates the contract, since that directory cannot have changed. The
            // captured-response arm below always settles Failed(Permanent): a retry against the same,
            // still-unsatisfied workspace would only burn budget, and the ruling is "the conductor
            // resolves this", not "the engine retries it".
            var captured = OutputMaterializer.TryCaptureFinalResponse(validation, contract, outputDirectory, responseParser);
            if (captured is not null)
            {
                try
                {
                    Console.Error.WriteLine(
                        "CAPTURED (#1594): the worker's declared output(s) " +
                        string.Join(", ", captured.UnsatisfiedOutputNames.Select(name => $"'{name}'")) +
                        $" were never written by the worker itself. baton captured its terminal " +
                        $"response to '{captured.FileName}' -- the declared output(s) were NOT " +
                        "written, and this execution settles Failed pending conductor resolution.");
                }
                catch (IOException)
                {
                    // Review F6: this runs on the settle path, which has no outer catch -- a broken
                    // stderr pipe on the way out must not itself orphan the execution (#1582's failure
                    // class). The room fact below still carries the capture regardless of whether this
                    // line reached the console.
                }

                var reason = BuildContractFailureReason(validation.UnsatisfiedOutputs)
                    + $" Response captured to '{captured.FileName}'; awaiting conductor resolution.";

                return new OutcomeClassification(
                    OutcomeVerdict.Failed,
                    FailureClassification.Permanent, // Permanent: conductor-resolves means the engine never auto-retries against a workspace this capture just wrote into (RetryEngine.MayRetry gates on this unconditionally).
                    WithStderr(reason, result.StderrTail),
                    CapturedResponseFile: captured.FileName,
                    UnsatisfiedOutputNames: captured.UnsatisfiedOutputNames);
            }
        }

        if (validation.IsSatisfied)
        {
            if (grantAuditMode == GrantAuditMode.AuditedNotEnforced)
            {
                // Premise verification: BATON_OUTPUT_DIR (the outbox, under artifacts/) lives OUTSIDE the provisioned worktree
                // (workspaces/<worker>), so legitimate output writes never dirty the worktree.
                var audit = Workspaces.WorktreeProvisioner.Audit(worktreePath);
                if (!audit.IsClean)
                {
                    return new OutcomeClassification(
                        OutcomeVerdict.Failed,
                        FailureClassification.Permanent, // Permanent: a worker mutating files outside declared outputs violates its role contract; retrying will produce identical stray mutations.
                        WithStderr(audit.FailureReason ?? "Grant audit failed: worktree is dirty.", result.StderrTail));
                }
            }

            // #914: an auto-denied tool is the ONLY thing that vetoes an otherwise-satisfied exit-0
            // run — agy denies a tool, exits 0, and the worker still writes its contract output, so
            // nothing else here would catch it. Gate specifically on ToolDenied: quota exhaustion
            // (ExhaustedUntil) cannot reach a *satisfied* contract, and gating narrowly keeps this from
            // ever stamping some other classification with the auto-denied message below.
            if (failureClassifier is not null && failureClassifier.TryClassifyFailure(
                    result.StderrTail, result.StdoutTail, timeProvider ?? TimeProvider.System, out var classifiedFailure, out var retryNotBefore)
                && classifiedFailure == FailureClassification.ToolDenied)
            {
                return new OutcomeClassification(
                    OutcomeVerdict.Failed,
                    classifiedFailure,
                    WithStderr("Execution failed: a required tool was auto-denied.", result.StderrTail),
                    retryNotBefore);
            }

            return new OutcomeClassification(OutcomeVerdict.Succeeded);
        }

        // Stderr is appended here too, not just on the non-zero-exit path. The exit-0-but-no-output
        // worker is #597's case, and a worker that decided it had nothing to write very often says
        // why on stderr on its way out — that is precisely the failure with the least other evidence.
        var (contractClassification, contractRetryNotBefore) = ReadOrClassifyFailure(contract, outputDirectory, result, failureClassifier, timeProvider);
        return new OutcomeClassification(
            OutcomeVerdict.Failed,
            contractClassification,
            WithStderr(BuildContractFailureReason(validation.UnsatisfiedOutputs), result.StderrTail),
            contractRetryNotBefore);
    }

    /// <summary>
    /// Appends a bounded, single-line rendering of the worker's stderr to an already-assembled
    /// reason (#563), or returns <paramref name="reason"/> untouched when the worker wrote nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base reason is assembled and bounded first, then this is appended with its own separate
    /// budget, rather than both sharing one cap. That is the same split
    /// <see cref="BuildContractFailureReason"/> already documents: a single shared cap lets whichever
    /// part happens to be longer starve the other, and here that would mean a verbose worker's
    /// stderr silently swallowing the contract diagnostic, or vice versa.
    /// </para>
    /// <para>
    /// The <c>stderr:</c> separator deliberately matched the now-retired dialogue worker's own
    /// <c>DialogueRunner</c>, which appended a failed vendor turn's stderr to its own message the
    /// same way since M17 Phase 3 (#166), on the same reasoning: an operator should not have to
    /// learn two spellings of the same fact. That worker was archived in #1408; this rendering
    /// stands on its own now.
    /// </para>
    /// </remarks>
    private static string WithStderr(string reason, string? stderrTail)
    {
        if (string.IsNullOrWhiteSpace(stderrTail))
        {
            // A worker that was genuinely silent must produce the byte-for-byte pre-#563 reason —
            // no empty "stderr:" label, which would read as though it had spoken and said nothing.
            return reason;
        }

        // Idempotent on anything CoreDispatcher produced — StderrTailBuffer already collapsed it, and
        // collapsing is what makes the two caps comparable. Repeated here because CoreDispatchResult
        // is a public record any caller may construct (a test double, a future dispatcher), and a raw
        // multi-line value reaching a line-oriented surface is the failure this prevents. It is not
        // where the guarantee comes from; see MaxStderrTailInReason.
        var collapsed = CollapseWhitespace(stderrTail);
        if (collapsed.Length == 0)
        {
            return reason;
        }

        var kept = ContractValidator.KeepLastWithoutSplittingSurrogatePair(collapsed, MaxStderrTailInReason);

        // The ellipsis goes on the front because the cut is on the front: this is a tail, so what was
        // dropped precedes what is shown. Marking the wrong end would claim the opposite.
        var marker = kept.Length < collapsed.Length ? "…" : string.Empty;

        return $"{reason} stderr: {marker}{kept}";
    }

    /// <summary>
    /// The inverse of <see cref="WithStderr"/>, for surfaces that render the two halves separately
    /// (#617's failed-step banner shows the sentence as a headline and the stderr as an excerpt
    /// block). Lives beside the writer so the <c>" stderr: "</c> spelling has one home and a format
    /// change cannot silently strand a reader — the round-trip test is the drift guard. A reason
    /// with no stderr half comes back whole with a null excerpt. A leading <c>…</c> stays on the
    /// excerpt: it is the writer's truncation mark (<see cref="MaxStderrTailInReason"/>), and
    /// stripping it would re-create, on this one surface, exactly the invisible truncation the
    /// writer's own contract forbids — a cut tail shown as though it were the whole capture.
    /// </summary>
    /// <remarks>
    /// The split takes the <i>first</i> separator occurrence, which is exact for the fixed engine
    /// sentences (<c>Execution timed out.</c>, <c>Worker exited with non-zero code N.</c>) but a
    /// heuristic for contract-failure reasons, whose base embeds worker-produced values
    /// (<see cref="DescribeUnsatisfiedOutput"/>) that could themselves contain the literal
    /// <c>" stderr: "</c> — in which case the sentence truncates early and the excerpt starts with
    /// base-reason text. Last-occurrence was considered and rejected as the worse bet: the tail is
    /// raw worker stderr, where a literal <c>stderr:</c> label is common wrapper output, and
    /// mis-splitting on it would fold real stderr into the headline instead. The combined string
    /// is the only durable record (<c>ExecutionAttempt.Reason</c>), so no parse can be exact for
    /// both halves; this picks the failure mode that needs the rarer content.
    /// </remarks>
    public static (string Sentence, string? StderrExcerpt) SplitReasonAndStderr(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ("Step failed.", null);
        }

        const string separator = " stderr: ";
        var index = reason.IndexOf(separator, StringComparison.Ordinal);
        if (index < 0)
        {
            return (reason.Trim(), null);
        }

        var sentence = reason[..index].Trim();
        var excerpt = reason[(index + separator.Length)..].Trim();

        return (sentence, excerpt.Length == 0 ? null : excerpt);
    }

    /// <summary>
    /// Flattens stderr to a single line. Every consumer of <c>Reason</c> is line-oriented — the CLI's
    /// <c>FlowStateReporter</c> writes one <c>"  {StepId}: {Status} — {Reason}"</c> line per step —
    /// so an embedded newline would not merely look untidy, it would break that format into rows
    /// that no longer parse as step lines. Vendor CLIs routinely write multi-line errors.
    /// </summary>
    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                // Deferred rather than emitted, so runs collapse and leading/trailing space never lands.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Assembles the diagnostic for a natural, exit-0 completion whose contract still isn't
    /// satisfied — the exact signature (worker exited 0, wrote none of its declared outputs) that
    /// previously surfaced as a bare <c>ExecutionFailed</c> with no reason.
    /// </summary>
    /// <remarks>
    /// Bounded in two places, and the split matters. Each output's <i>count</i> is capped here and
    /// each rendered <i>value</i> is capped in <see cref="ContractValidator"/>, both with their own
    /// explicit marker; only then is <see cref="Truncate"/> applied to the whole. Capping solely at
    /// the end was wrong: one large value would eat the entire budget and silently drop every other
    /// unsatisfied output, so a reason that promised to name them all named one. With the per-item
    /// bounds in place the final <see cref="Truncate"/> is a backstop that should not normally fire.
    /// </remarks>
    private static string BuildContractFailureReason(IReadOnlyList<UnsatisfiedOutput> unsatisfiedOutputs)
    {
        var listed = unsatisfiedOutputs.Count <= MaxListedOutputs
            ? unsatisfiedOutputs
            : unsatisfiedOutputs.Take(MaxListedOutputs).ToList();

        var reason = "Contract not satisfied: " + string.Join("; ", listed.Select(DescribeUnsatisfiedOutput));

        // The suffix's own length is reserved from the budget rather than appended after
        // truncating. Appending it left the marker as the first thing Truncate cut — reinstating,
        // at the count layer, the very "outputs silently dropped with no signal" this cap exists to
        // prevent. A signal that disappears exactly when it becomes true is worse than none.
        var overflow = unsatisfiedOutputs.Count - listed.Count;
        if (overflow > 0)
        {
            var suffix = $" (+{overflow} more)";
            return Truncate(reason, MaxReasonLength - suffix.Length) + suffix;
        }

        return Truncate(reason, MaxReasonLength);
    }

    private static string DescribeUnsatisfiedOutput(UnsatisfiedOutput output) => output.Reason switch
    {
        UnsatisfiedOutputReason.Missing => $"'{output.Name}' is missing",
        UnsatisfiedOutputReason.NotJson => $"'{output.Name}' is not valid JSON",
        UnsatisfiedOutputReason.ConditionFailed => output.ActualValue is null
            ? $"'{output.Name}': JSON Pointer '{output.ConditionPath}' did not resolve (expected {output.ExpectedValue})"
            : $"'{output.Name}': JSON Pointer '{output.ConditionPath}' resolved to {output.ActualValue}, expected {output.ExpectedValue}",
        UnsatisfiedOutputReason.MalformedCondition =>
            $"'{output.Name}': condition cannot be evaluated — {output.Detail}",
        UnsatisfiedOutputReason.SchemaViolation =>
            $"'{output.Name}' is not a valid document of its declared schema — {output.Detail}",
        _ => throw new ArgumentOutOfRangeException(nameof(output), output.Reason, "Unknown UnsatisfiedOutputReason."),
    };

    /// <summary>
    /// Backstop cap on the assembled reason. Delegates the cut to
    /// <see cref="ContractValidator.TrimWithoutSplittingSurrogatePair"/> so there is one
    /// surrogate-safe truncation in the codebase rather than two that can drift — the per-value cap
    /// needs the identical rule, and a second copy of it is the shape that goes wrong quietly.
    /// The <c>cut &gt; 0</c> guard lives there: it is unreachable while
    /// <see cref="MaxReasonLength"/> is 500, but lowering a display cap is an ordinary later edit and
    /// an unguarded index would throw out of <see cref="Classify"/> while recording an outcome.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        const string ellipsis = "...";
        return ContractValidator.TrimWithoutSplittingSurrogatePair(value, maxLength - ellipsis.Length) + ellipsis;
    }

    /// <summary>
    /// Looks for a worker's optional self-reported <see cref="Domain.FailureClassification"/>,
    /// reported through one of the contract's declared <c>OptionalMetadata</c> file
    /// roles as a top-level <c>FailureClassification</c> JSON field. Checked in declaration order;
    /// the first metadata file that exists, parses as JSON, and carries a recognized value wins.
    /// Absent or unrecognized — including no <c>OptionalMetadata</c> file at all — is null, which
    /// the domain type documents as "treated as Retryable".
    /// </summary>
    private static FailureClassification? ReadFailureClassification(WorkerContract contract, string outputDirectory)
    {
        foreach (var metadataName in contract.OptionalMetadata)
        {
            var path = Path.Combine(outputDirectory, metadataName);
            if (!File.Exists(path))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllBytes(path));
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("FailureClassification", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<FailureClassification>(value.GetString(), ignoreCase: true, out var classification))
                {
                    return classification;
                }
            }
        }

        return null;
    }

    private static (FailureClassification? Classification, DateTimeOffset? RetryNotBefore) ReadOrClassifyFailure(
        WorkerContract contract,
        string outputDirectory,
        CoreDispatchResult result,
        IFailureClassifier? failureClassifier,
        TimeProvider? timeProvider)
    {
        var metadataClassification = ReadFailureClassification(contract, outputDirectory);
        if (metadataClassification is not null)
        {
            return (metadataClassification, null);
        }

        if (failureClassifier is not null && failureClassifier.TryClassifyFailure(
                result.StderrTail, result.StdoutTail, timeProvider ?? TimeProvider.System, out var adapterClassification, out var adapterRetryNotBefore))
        {
            return (adapterClassification, adapterRetryNotBefore);
        }

        return (null, null);
    }
}
