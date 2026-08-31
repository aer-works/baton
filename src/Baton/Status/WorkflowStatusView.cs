using System.Text.Json.Serialization;
using Baton.Artifacts;
using Baton.Domain;
using Baton.Outcomes;
using Baton.Scheduling;

namespace Baton.Status;

/// <summary>
/// One step's machine-readable state, per <c>baton status --json</c>'s shape (#1356): a bare
/// <see cref="StepStatus"/> token, never the human prose <c>StatusCommand.FormatStepStatus</c> prints
/// (a parked/liveness-annotated sentence a machine consumer would have to parse back apart).
/// <see cref="Liveness"/> (#1375, spec/baton.md §3) is the one exception carried as a separate,
/// structured field rather than folded into <see cref="State"/> — see its own remarks.
/// </summary>
public sealed record WorkflowStatusStepView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("execution")] string? Execution,
    // #1359: the execution `baton resume` continued, when Execution is a resume's own new attempt —
    // null for every ordinary dispatch/retry. Lets a status consumer render both executions of a
    // resumed step without a second lookup.
    [property: JsonPropertyName("linkedFrom")] string? LinkedFrom = null,
    // #1360: Execution's own usage -- absent (not present as a whole) when that execution has no
    // recorded start/exit pair to derive wall-clock from (still running, or Flow crashed before Core
    // recorded either lifecycle event). See ExecutionUsageProjector.
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? Usage = null,
    // #1360: LinkedFrom's own usage, kept separate from Usage rather than merged -- a resumed step's
    // two executions are two distinct cost entries, not one to be added or overwritten.
    [property: JsonPropertyName("linkedFromUsage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionUsageView? LinkedFromUsage = null,
    // #1375: the SAME EngineLivenessProbe the human `baton status` rendering consults
    // (StatusCommand.FormatStepStatus), never a second probe -- present only for a Running step,
    // FormatStepStatus's own gate (why non-Running steps claim nothing: spec/baton.md §3).
    // "alive" | "dead" | "unknown", lower-cased from EngineLivenessStatus; omitted, never null, for
    // every non-Running step so the field's mere presence already answers "does liveness apply here".
    [property: JsonPropertyName("liveness")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Liveness = null,
    // #1509: a CONSECUTIVE-FAILURE-derived ordinal, not a true lifetime execution count -- Flow
    // persists StepState.ConsecutiveFailureCount, not a monotonic attempt counter, so this is the
    // most honest number derivable from what's actually recorded: ConsecutiveFailureCount+1 while
    // Running (this attempt hasn't failed yet), ConsecutiveFailureCount itself once Failed (the
    // latest attempt IS the Nth consecutive failure). Omitted -- never fabricated -- whenever
    // ConsecutiveFailureCount is 0 (indistinguishable from "never failed" vs. "nothing recorded")
    // or Status is neither Running nor Failed. Two known ways this undercounts relative to a true
    // execution ordinal, both because ConsecutiveFailureCount itself is defined that way
    // (StateProjector.cs): a FailureClassification.ExhaustedUntil failure does not increment the
    // count (0026 obliges the engine not to spend a retry-budget attempt against an exhausted
    // quota), so that execution's own ordinal renders one low; a DecisionType.RetryWithRevision
    // resume resets the count to 0, so the next real failure after a human-revised retry renders as
    // attempt 1 again rather than continuing the count. Both are reported findings (see
    // report-1509.md), not silently swallowed -- fixing them needs a persisted lifetime counter Flow
    // does not have today, which is bigger than this field's brief authorizes.
    [property: JsonPropertyName("attempt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Attempt = null,
    // #1509: the step definition's RetryPolicy.MaxAttempts, carried alongside Attempt so a
    // consumer can render "attempt 3/5" without a second lookup. Present only when Attempt is.
    [property: JsonPropertyName("maxAttempts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxAttempts = null,
    // #1510: StepState.LatestFailureClassification's enum member name verbatim (Retryable /
    // Permanent / ExhaustedUntil / ToolDenied) -- the engine's own taxonomy, never a new one.
    // Present only for a Failed step that recorded a classification; a Failed step whose worker
    // reported none stays omitted rather than defaulting to "Retryable", even though that is how
    // RetryEngine itself treats an absent classification -- the field states what was recorded, not
    // what it is treated as.
    [property: JsonPropertyName("failureKind")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureKind = null,
    // #1510: RetryEngine.MayRetry's own verdict for this step, never a second taxonomy. Present
    // only alongside a Failed step; a step that hasn't failed has nothing to be "eligible to
    // retry".
    [property: JsonPropertyName("retryEligible")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RetryEligible = null);

/// <summary>
/// The one JSON object <c>baton status --json</c> writes to stdout (#1356's machine completion
/// contract): <c>{state, steps:[{id, state, execution, linkedFrom, usage, linkedFromUsage, liveness}],
/// outputs:[...], error, try, rejected}</c> — the canonical statement of this shape, full schema at
/// spec/baton.md §3 (see also <c>docs/agents/invoking-baton.md</c>'s <c>record-once-ok</c> marker,
/// which points here).
/// <c>linkedFrom</c> (#1359) is additive to #1356's shape, same as <c>Try</c> below — see
/// <see cref="WorkflowStatusStepView.LinkedFrom"/>. <c>usage</c>/<c>linkedFromUsage</c> (#1360) are
/// likewise additive — see <see cref="WorkflowStatusStepView.Usage"/>. Also what the terminal sentinel
/// (<c>terminal.json</c>, <see cref="TerminalSentinelWriter"/>) serializes, so a file-watching agent
/// and a polling <c>status --json</c> caller read the identical shape.
/// <c>Try</c> (#1382 F3) is additive to #1356's shape: the corrected-invocation text an
/// <see cref="Baton.BatonFlowException.TryInvocation"/>-carrying refusal set, kept as its own field
/// rather than appended into <see cref="Error"/> so a consumer can tell diagnosis from remedy apart.
/// Only ever populated on the pre-ledger sentinel path (<see cref="TerminalSentinelWriter.WriteValidationRefusedAsync"/>) —
/// a normal ledger projection has no exception to carry one.
/// <see cref="Rejected"/> (#1377) and <see cref="WorkflowStatusStepView.Liveness"/> (#1375) are the
/// two most recently added additive fields — see each property's own remarks for what they carry and
/// why.
/// </summary>
public sealed record WorkflowStatusView(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowStatusStepView> Steps,
    [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("try")] string? Try = null,
    // #1377: true when at least one step settled via `DecisionType.Reject` -- the one structural
    // fact this contract can honestly assert about a rejection. There is no recorded-reason text to
    // surface alongside it: `FlowEvent.ExternalDecisionRecorded` carries no operator-supplied reason
    // field today, so a `reason` field here would always read `null` and this deliberately does not
    // invent one. Lets a caller reading `state: "Failed"`/`error: null` tell "a person said no" apart
    // from "the worker crashed and nobody recorded why" without parsing prose; the branching recipe
    // and the which-step pointer live in spec/baton.md §3.
    [property: JsonPropertyName("rejected")] bool Rejected = false);

/// <summary>
/// Builds <see cref="WorkflowStatusView"/> from the same <see cref="FlowState"/>
/// <c>StatusCommand.PrintState</c>/<c>FlowStateReporter.Report</c> already render (one derivation,
/// two — now three, counting the terminal sentinel — renderings; #1356 requires never forking the
/// projection itself). Never re-reads <c>flow.jsonl</c> or <c>snapshot.json</c> on its own: callers
/// pass in the already-projected <see cref="FlowState"/>, and (#1360) the raw <see cref="LogEntry"/>
/// list a caller already read for that same projection, when per-execution usage is wanted.
/// </summary>
public static class WorkflowStatusProjector
{
    /// <param name="entries">
    /// The same ledger entries the caller already read to produce <paramref name="state"/> (#1360) —
    /// source data for <see cref="ExecutionUsageProjector.BuildByExecutionId"/>. Omitted (or empty)
    /// yields a view with no <c>usage</c> on any step, never a fabricated one; a caller that has no
    /// use for usage data (or has not read the ledger for another reason) is not forced to.
    /// </param>
    /// <param name="adapters">
    /// Registered adapters (#1360) an execution's own dispatched worker is attributed to via
    /// <paramref name="roomDirectoryPath"/>'s <c>bindings.json</c> — see
    /// <see cref="ExecutionUsageProjector"/>'s remarks for how attribution works and what happens
    /// without it.
    /// </param>
    public static WorkflowStatusView Project(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        IReadOnlyList<LogEntry>? entries = null,
        IReadOnlyDictionary<string, IWorkerUsageParser>? adapters = null) =>
        Project<IWorkerUsageParser>(state, snapshot, roomDirectoryPath, entries, adapters);

    public static WorkflowStatusView Project<TParser>(
        FlowState state,
        WorkflowDefinitionSnapshot snapshot,
        string roomDirectoryPath,
        IReadOnlyList<LogEntry>? entries = null,
        IReadOnlyDictionary<string, TParser>? adapters = null)
        where TParser : IWorkerUsageParser
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var stepDefByStepId = snapshot.Steps.ToDictionary(step => step.StepId);
        var artifactsRootPath = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);

        var usageByExecutionId = ExecutionUsageProjector.BuildByExecutionId(
            entries ?? [], artifactsRootPath, adapters, roomDirectoryPath);

        // #1375: the same (pid, engine-start-time) pair StatusCommand.FormatStepStatus reads off
        // ExecutionRequestAccepted to drive EngineLivenessProbe -- built once here rather than
        // re-scanning `entries` per Running step.
        var engineIdentityByExecutionId = new Dictionary<string, (int? Pid, DateTimeOffset? StartTime)>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                engineIdentityByExecutionId[accepted.Request.ExecutionId.Value] = (accepted.EnginePid, accepted.EngineStartTime);
            }
        }

        var steps = new List<WorkflowStatusStepView>(state.Steps.Count);
        var outputs = new List<string>();
        string? firstFailureReason = null;
        var anyRejected = false;

        foreach (var step in state.Steps)
        {
            var usage = step.LatestExecutionId is { } latest && usageByExecutionId.TryGetValue(latest.Value, out var latestUsage)
                ? latestUsage
                : null;
            var linkedFromUsage = step.LinkedFromExecutionId is { } linkedFrom && usageByExecutionId.TryGetValue(linkedFrom.Value, out var linkedUsage)
                ? linkedUsage
                : null;

            // Probe ONLY steps this projection itself calls Running -- same gate as
            // FormatStepStatus's own; the record parameter's comment above says why the gate exists.
            // Unlike FormatStepStatus, a step with no recorded
            // ExecutionRequestAccepted identity still gets probed -- Probe(null, null) itself already
            // reads as EngineLivenessStatus.Unknown, so this always renders a value for a Running step
            // rather than silently omitting the field on a miss (review finding: the two renderings
            // must never disagree about WHETHER a verdict exists, only about its OS-level result).
            string? liveness = null;
            if (step.Status == StepStatus.Running && step.LatestExecutionId is { } runningExecution)
            {
                var identity = engineIdentityByExecutionId.TryGetValue(runningExecution.Value, out var found)
                    ? found
                    : (Pid: (int?)null, StartTime: (DateTimeOffset?)null);
                var probeResult = EngineLivenessProbe.Probe(identity.Pid, identity.StartTime);
                liveness = probeResult.Status switch
                {
                    EngineLivenessStatus.Alive => "alive",
                    EngineLivenessStatus.Dead => "dead",
                    _ => "unknown",
                };
            }

            // #1509/#1510: StepState already carries everything -- ConsecutiveFailureCount,
            // LatestFailureClassification, and (via stepDefByStepId) RetryPolicy.MaxAttempts -- this
            // is just the one place that was discarding it before it reached this view. Gated on
            // ConsecutiveFailureCount > 0, not merely Running/Failed: a step's first-ever execution
            // is indistinguishable from "unknown" if it were rendered as "attempt 1" (the count
            // defaults to 0 both when a step genuinely never failed and when nothing was ever
            // recorded for it) -- so a step with no failure history omits the field entirely rather
            // than asserting attempt 1. See WorkflowStatusStepView.Attempt's own remarks for the two
            // known cases (ExhaustedUntil, RetryWithRevision) where this still undercounts once a
            // failure genuinely has happened.
            int? attempt = step switch
            {
                { Status: StepStatus.Running, ConsecutiveFailureCount: > 0 } => step.ConsecutiveFailureCount + 1,
                { Status: StepStatus.Failed, ConsecutiveFailureCount: > 0 } => step.ConsecutiveFailureCount,
                _ => null,
            };
            int? maxAttempts = attempt is not null && stepDefByStepId.TryGetValue(step.StepId, out var attemptStepDef)
                ? attemptStepDef.RetryPolicy.MaxAttempts
                : null;
            string? failureKind = step.Status == StepStatus.Failed && step.LatestFailureClassification is { } classification
                ? classification.ToString()
                : null;
            bool? retryEligible = step.Status == StepStatus.Failed && stepDefByStepId.TryGetValue(step.StepId, out var retryStepDef)
                ? RetryEngine.MayRetry(step, retryStepDef.RetryPolicy)
                : null;

            steps.Add(new WorkflowStatusStepView(
                step.StepId.Value, step.Status.ToString(), step.LatestExecutionId?.Value, step.LinkedFromExecutionId?.Value,
                usage, linkedFromUsage, liveness, attempt, maxAttempts, failureKind, retryEligible));

            if (firstFailureReason is null && step.Status is StepStatus.Failed or StepStatus.Rejected
                && !string.IsNullOrWhiteSpace(step.LatestFailureReason))
            {
                firstFailureReason = step.LatestFailureReason;
            }

            if (step.Status == StepStatus.Rejected)
            {
                anyRejected = true;
            }

            // #740's rule via StepOutputResolver, the one place it is implemented (#1374 F5) — this
            // must never drift from FlowStateReporter's own printed paths for the same room.
            if (stepDefByStepId.TryGetValue(step.StepId, out var stepDef))
            {
                outputs.AddRange(StepOutputResolver.Resolve(step, stepDef, artifactsRootPath).Select(o => o.Path));
            }
        }

        return new WorkflowStatusView(WorkflowOutcome.Describe(state), steps, outputs, firstFailureReason, Rejected: anyRejected);
    }

    /// <summary>
    /// Extracts UTC timestamps for each execution from log entries (Flow and Core lifecycle events),
    /// with latest event winning per execution ID.
    /// </summary>
    public static Dictionary<string, DateTime> ExtractEventTimestamps(IReadOnlyList<LogEntry> entries)
    {
        var timestamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string? execId = null;
            DateTime? timestamp = null;

            switch (entry)
            {
                case LogEntry.FlowLogEntry flowEntry:
                    timestamp = flowEntry.WriterUtcTimestamp;
                    execId = flowEntry.Event switch
                    {
                        FlowEvent.ExecutionRequestAccepted accepted => accepted.Request.ExecutionId.Value,
                        FlowEvent.ExecutionSucceeded succeeded => succeeded.ExecutionId.Value,
                        FlowEvent.ExecutionFailed failed => failed.ExecutionId.Value,
                        _ => null,
                    };
                    break;
                case LogEntry.CoreLogEntry coreEntry:
                    timestamp = coreEntry.WriterUtcTimestamp;
                    execId = coreEntry.Event switch
                    {
                        CoreEvent.ExecutionStarted started => started.ExecutionId.Value,
                        CoreEvent.ExecutionExited exited => exited.ExecutionId.Value,
                        _ => null,
                    };
                    break;
            }

            if (execId is not null && timestamp.HasValue)
            {
                timestamps[execId] = timestamp.Value;
            }
        }

        return timestamps;
    }
}
