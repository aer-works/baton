# Baton: the worker-room layer — spec v2.0

**Status: settled, revision cycle 2 of #1396.** Target reader: an agent-harness author integrating
against Baton — dispatching lanes, polling completion, reading structured output. Not the Baton
team, not a human app user. This document is the top-level statement of what the system is.

Every claim below either cites a real path in this tree or is marked **(new build)** — settled
direction not yet implemented — or `UNVERIFIED — fill from code`, where I could not confirm a shape
by reading the file. There are no open decision boxes left in this document: the two the prior
draft carried (a surviving dev diagnostic UI; whether remote dispatch is in scope) are both closed
below, in §6 and §10.

**On citations.** Per the register posture (§11), this document cites only code paths and its own
sections. It does not cite decision records, design docs, or the old room/behavioral specs — those
are being deleted, not archived, and a spec that outlives its sources cannot depend on them. Every
rule the previous register held is restated here, in full, as this document's own rule.

---

## §1 Identity

Baton is a headless, vendor-neutral engine that runs vendor CLI agents — Claude Code, `agy` — as
**workers** inside **rooms**, under a durable, replayable journal. It is not an interactive product.
It has no chat surface, no daily-driver UI, no resident conversational partner. The thing that drives
it is an **agent harness**: a program that calls `baton dispatch`, polls for a completion sentinel, and
reads structured output. The harness is the user this spec is written for.

Two invariants govern everything below:

- **Routing never reads conversation content.** Flow's scheduling logic reads structured outcomes —
  exit codes, declared outputs, explicit tool returns — never the meaning of what a worker said. This
  is a design invariant held by review, not a gated property: `Baton.Architecture.Tests` (kept, per the
  Appendix) pins the reference-direction half, and its own header states that no static test can
  honestly assert the no-content-reads half — do not cite it as enforcement of this bullet.
- **The journal is the system of record.** Every state a room can be in is a projection of recorded
  events; the system cannot be in a state it has not recorded. §2 states plainly that this is now
  true of *two* journals, not one, and what each one is for.

What Baton is **not**, stated as exclusions (§10 expands each):

- Not a chat product. Chat is one internal *workflow shape* a room can run, not a product surface a
  person opens.
- Not session-parity with a phone or desktop app. There is no daily-driver client this spec assumes
  exists, and none of `Baton.Ui`, `Baton.Ui.Core`, `Baton.Mobile`, or `Baton.Sidecar` survives this reset
  (Appendix).
- Not an orchestrator that decides on a human's behalf by default. §5 states the harness-facing gate
  contract directly: exactly one gate, closed exactly one way.
- Not a UI product. Fleet Glass (§6) is the entire observability surface, full stop — not "at most a
  dev diagnostic surface pending a decision." That decision is made: Fleet Glass, extended with a
  two-level drill-down, is a diagnostic surface built as **(new build)** levels of the MCP tool
  itself, never a second application.

---

## §2 The dispatch unit

A **room** is one working directory: `~/.baton/rooms/<room>/` (`BatonPaths.Rooms`,
`src/Baton/Status/BatonPaths.cs`). One directory may contain several repositories; the room does
not know or care.

A room holds, at minimum: `room.json` (the room-kind marker — `BatonPaths.RoomMetadataFileName`,
`BatonPaths.cs`; absence reads as a workflow room), `bindings.json` (the standing worker grant —
`BatonPaths.RoomBindingsFileName`, `BatonPaths.cs`), `flow.jsonl` (the workflow event log —
§3), `artifacts/`, and, once terminal, `terminal.json` (§3). `snapshot.json` is present for any
room that has been dispatched at least once — `fleet_status` treats its absence as "no bound
snapshot" and reports it as an error entry rather than a state (`src/Baton.Cli/Mcp/FleetStatusTool.cs`).

**There are two independent event logs, not one, and this spec states both honestly.**
`flow.jsonl` is the workflow ledger — steps, executions, decisions — and everything in §3–§9 below
reads and writes only this one. A **second** ledger, `room.jsonl`, exists in the same engine
(`src/Baton/Domain/RoomEvent.cs`, `src/Baton/Store/RoomEventLogReader.cs`,
`RoomEventLogWriter.cs`, `src/Baton/Projection/RoomProjector.cs`,
`src/Baton/Mutation/RoomMutationInterface.cs`) and its full event vocabulary is: held-work
dispatch/escalation/resolution, grant record/amend/revoke, ask-time escalation, turn-host dormancy
entered/cleared, mid-turn permission ask/answer/revoke, standing-permission revocation, the
workflow on/off switch, worker join/rename, and orchestrator (re)assignment
(`RoomEvent.cs`).

State it plainly: **every one of those event kinds is written only by code this reset deleted.**
The mid-turn permission ask/answer/revoke triad is the deleted ask mechanism (#1417, §5). Held work,
escalation, dormancy, and orchestrator assignment are the resident-orchestrator/wake-loop model
`Baton.Daemon`'s `RoomTurnHost`/`RoomWakeBridge` implement, and that model has no referent left once
the harness — not a resident presence — is the decider (§7). Worker join/rename and the workflow
on/off switch belong to the interactive multi-participant chat room product `Baton.Ui`/`Baton.Mobile`
served. I checked: `src/Baton.Cli` (including its folded-in `Mcp/` verb, the former standalone
Baton.Mcp.Host project, #1458) references none of `RoomMutationInterface`, `RoomEventLogReader`, or `RoomEventLogWriter` — the harness-facing
surface this spec describes has never touched `room.jsonl`, and `fleet_status` reads only the
terminal sentinel, `snapshot.json`, and `flow.jsonl`
(`FleetStatusTool.cs`) — never `room.jsonl`. Its type definitions stay in `Baton` because
Architecture Rule 1 keeps the journal engine-owned regardless of who reads it, and deleting dead
infrastructure is a separate cleanup this document does not scope — but a harness author should read
`room.jsonl` as **inert**: nothing in the dispatch/decide/status/fleet_status surface this spec
describes writes to it or reads from it.

A harness invokes work two ways, both in `src/Baton.Cli/Program.cs`:

- **`baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>]
  [--echo-worker] [--wait]`** — runs an authored `WorkflowDefinition` to a terminal state or a pause
  (`src/Baton.Cli/RunOptionsParser.cs`).
- **`baton dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>]
  [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>]`**
  — the one-shot form: `<name>` resolves to either a worker role (needs `--spec`) or a built-in
  template (`src/Baton.Cli/DispatchOptionsParser.cs`). Left unset, `--room-dir` derives a fresh, unique
  directory under `BatonPaths.Rooms` per invocation — never a stable name derived from `<name>`, so a
  second `baton dispatch review` reruns rather than resuming the first's terminal snapshot. Bindings are
  written into the room directory by `DispatchCommand.ExecuteAsync`
  (`src/Baton.Cli/DispatchCommand.cs`, via `WorkerBindingConfigWriter.SaveToFileAsync`) before
  `RunCommand` is invoked underneath it. `--timeout` (#1442) overrides the dispatched role's own
  catalog timeout for just this dispatch, recorded into that same `bindings.json` (never
  `workflow.json` — a worker's timeout has always been kept off the frozen `WorkflowDefinitionSnapshot`,
  the M7 Phase 7 split `WorkerBindingConfigEntry`'s own doc states). It is the escape hatch for a role
  that legitimately needs longer than its fixed tier timebox — an orchestrator coordinating sub-lanes,
  say — so such a lane does not die mid-flight. Role dispatch only, rejected for a template: a
  template's phases each carry their own role's timeout, so there is no single one to override. Values
  are whole minutes, rejected outright above a 24h ceiling (no interactive confirmation exists for a
  non-interactive CLI) and merely flagged on stderr above 2h.

A room's model is always pinned in `bindings.json` at dispatch time — there is no runtime model
choice a harness makes mid-lane; §9 covers the bindings contract. `baton resume`, `baton decide`, `baton
cancel`, and `baton supply` continue an already-dispatched room; §5 covers `decide` specifically.

**`baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] [--effort
<name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>]`** (#1441) reruns a single-role
`baton dispatch` room into a fresh one, once the operator finds the brief was wrong or incomplete —
without hand-retyping the adapter/model/effort/workspace/timeout flags a from-scratch `baton dispatch`
would otherwise force. `<room-dir>` names the parent room; like `baton dispatch`, the new room's own
directory is always freshly generated (`RedispatchOptionsParser.cs`) — a redispatch is never a resume,
same rule as §2's dispatch entry above. Every flag inherits the parent room's recorded `bindings.json`
entry as its default — adapter, model, effort, workspace, timeout — and is overridden by whichever
flag the operator actually passes (`RedispatchCommand.InheritBinding`); `--output` is the one
exception, never inherited, because a prior `--output`'s destination copy path is not persisted
anywhere in the room (only the produced output's customized *name* is, on the bindings entry's
contract) — a redispatch's own `--output`, when given, works exactly like dispatch's own. `--spec`
omitted reuses the parent's already-built prompt verbatim; given, the amended brief is rebuilt through
the same `RoleDispatch.Materialize` a fresh dispatch uses, with the parent's recorded axes as defaults.
The parent must be Terminal (`terminal.json` present) — a still-running or never-dispatched parent is
refused with a typed `CliArgumentException` naming `baton status` as the retry (no interactive
confirmation exists for a non-interactive CLI, the same doctrine `--timeout`'s ceiling above rests on);
a Terminal-but-not-`Succeeded` parent is redispatched anyway, with a stderr note rather than a silent
rerun of a failed or cancelled lane. A parent whose `bindings.json` binds more than one worker (a
composed template, never a single role) is refused — redispatch supports a single-role dispatch only.
The parent's own artifacts are never copied into the child room — the child's `--spec` can cite paths
under the parent room if it needs to, but copying would blur which run produced what. Lineage is
recorded on the new room's own `.baton/room.json` marker (`RoomMetadataFileName`, `BatonPaths.cs`) — the
parent room directory, and the parent's own execution id when cheaply known from its terminal
sentinel — rather than a new parallel file, since that marker is already this room's metadata home.
One inheritance rule differs from what `--adapter`'s name implies: on the `--spec`-omitted path, an
adapter swap re-derives only the adapter-scoped axes (model and effort drop to the new vendor's
defaults per the vendor-swap rule above; `StreamJson` is recomputed for the new adapter). The
parent's resolved `PermissionGrant`, `GrantAuditMode`, and worktree-provisioning intent are carried
across the swap **unchanged**, because the role's *declared* grant intent — what
`RoleDispatch.ToBinding` derives those from per adapter — is not recoverable from `bindings.json`
(only the already-resolved grant is persisted). So a redispatched worker can run under a grant shape
a fresh dispatch of the same role+adapter would never produce; the command prints a stderr note on
every such swap, and an operator who needs the grant re-derived passes `--spec`, which rebuilds
through `RoleDispatch.Materialize` against the real role catalog.

### §2 schema — the CLI argument table

| Verb | Usage | Source |
|---|---|---|
| `run` | `baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--wait]` | `RunOptionsParser.cs` |
| `dispatch` | `baton dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>]` | `DispatchOptionsParser.cs` |
| `redispatch` | `baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] [--effort <name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>]` | `RedispatchOptionsParser.cs` |
| `resume` | `baton resume <room-dir> --worker <role> (--message <text> \| --message-file <path>) --bindings <bindings-file> [--workflow-id <id>]` | `ResumeOptionsParser.cs` |
| `decide` | `baton decide <room-dir> --execution <execution-id> --type resume\|reject\|retry-with-revision\|supersede [--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]` | `DecideOptionsParser.cs` |
| `supply` | `baton supply <room-dir> --worker <role> --output <name> --file <source-path> --bindings <bindings-file> [--workflow-id <id>]` | `SupplyOptionsParser.cs` |
| `cancel` | `baton cancel <room-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]` | `Program.cs` |
| `status` | `baton status <room-dir> [--follow] [--json]` | `StatusOptionsParser.cs` |
| `templates` | `baton templates [--json]` | `Program.cs` |

`templates` narrows to the built-in catalog only (`Baton.Vendors`'s `BuiltInWorkflowTemplates`) —
there is no authoring UI to browse a saved-template library visually against (Appendix, R7 in the
old numbering — dropped here, since there is no longer a separate register to number rulings
against).

---

## §3 The lane protocol (completion contract)

`terminal.json` is written into a room directory the moment its workflow reaches a terminal state —
the completion signal a harness should watch instead of polling `baton status` prose or racing the
`baton run`/`baton dispatch` process's own exit
(`src/Baton/Status/TerminalSentinelWriter.cs`). It is written **last** — after every output an
outcome could reference already exists on disk — via a temp-file-then-atomic-move sequence, so a
file-watching harness never observes a partial write (`TerminalSentinelWriter.cs`). It is the
identical shape `baton status --json` prints (`WorkflowStatusView`), so a file-watcher and a polling
`status --json` caller read one contract for that pair specifically
(`src/Baton/Status/WorkflowStatusView.cs`) — `fleet_status` is a **third, related** shape;
see §6.

**Its absence does not always mean "not terminal yet."** Two exceptions, both real:

1. `TerminalSentinelWriter.WriteValidationRefusedAsync` — the pre-ledger refusal path — is only
   invoked when `RoomLedgerProbe.HasLedger` is false (`src/Baton.Cli/Program.cs`,
   `src/Baton.Cli/RoomLedgerProbe.cs`: a `flow.jsonl` that exists and is non-empty). A room that
   already has a real ledger — e.g. a paused room re-dispatched with a bad `--spec` — returns exit code
   2 (`ValidationRefused`) with **no sentinel written**, because the room's ledger (or a still-live
   pump) is its real terminal record and a fresh refusal must not overwrite it with a fabricated
   `Failed`/no-outputs sentinel. `baton resume`'s own refusal path (`Program.cs`) never writes
   a sentinel at all — a resume always targets an already-ledgered room.
2. `RoomHeld` (exit code 5, below) also writes no sentinel: the room may be perfectly healthy (a live
   pump, or a background sweep's brief lock), and writing `Failed` here would tell a file-watcher a
   running room just died while `baton status --json` reads the same room as `Running` at the same
   moment (`Program.cs`).

So: absence means "not terminal yet, **or** refused against an already-ledgered room, **or** another
Flow instance currently holds it" — never simply "never started." A harness that needs to
distinguish these reads `baton status`/`flow.jsonl` directly rather than inferring from the sentinel's
absence alone.

**The sentinel can also disappear.** `TerminalSentinelWriter.DeleteStaleSentinel`
(`TerminalSentinelWriter.cs`) removes a prior sentinel when a room is re-run, so that retrying a
room that previously failed pre-ledger does not leave the old `terminal.json` in place for the whole
duration of a new, genuinely in-progress attempt. A file-watching harness must expect `terminal.json`
to vanish and reappear across a re-dispatch of the same room directory, not treat its disappearance
as an error.

`baton status` is read-only, produces no `CommandResult`, and always exits 0 when it manages to print a
status at all (`Program.cs`) — it cannot complete a room or substitute for watching the
sentinel.

**Two defects this contract used to carry, now closed (#1375, #1377) — cited so a harness author who
read an older version of this section knows what changed:** a dead engine's `Running` step
now also reports `steps[].liveness: "dead"` (§3 schema below), computed by the identical
`EngineLivenessProbe` the human `baton status` rendering already used — one probe, two renderings,
never two that can disagree; and a decision-rejected step now sets the top-level `rejected: true`
(§3 schema below) alongside `state: "Failed"`/`error: null`, so an absent `error` no longer implies an
absent cause — it can mean "a person said no" as well as "not yet recorded". Neither fix invents a
value the ledger cannot actually support: there is still no operator-supplied rejection *reason* to
surface (`FlowEvent.ExternalDecisionRecorded` carries none), so `rejected` stays a boolean, not a
`reason` field that would always read `null`.

### Exit codes

`RunExitCode` (`src/Baton.Cli/RunExitCodeResolver.cs`), returned by `run`, `dispatch`, and
`resume` only — `cancel`/`decide`/`supply` keep the unchanged binary success/failure code
(`Program.cs`):

| Code | Name | Meaning |
|---|---|---|
| 0 | `Succeeded` | Every step succeeded |
| 1 | `Failed` | **Not** exclusively terminal-and-failed — see below |
| 2 | `ValidationRefused` | Provisioning/validation refused, independent of ledger state; the **sentinel write** (not the exit code) is what is conditional on `RoomLedgerProbe.HasLedger` (above) |
| 3 | `Timeout` | At least one step's failure is a timeout and none is a hard failure (`RunExitCodeResolver.ResolveFailed`) |
| 4 | `Cancelled` | — |
| 5 | `RoomHeld` | Another Flow instance already holds this room — retry later, not a terminal outcome; no sentinel is written (`Program.cs`) |

**Exit code 1 is not "terminal, a step failed."** `RunExitCodeResolver.Resolve` falls through to
`Failed` for **`Running` and `Paused` too** — any outcome that is not `Succeeded`, `Cancelled`, or the
resolved `Failed`/`Timeout` split (`RunExitCodeResolver.cs`, comment verbatim: *"Running or
Paused: the pump returned short of Terminal (no `--wait`, or `--wait`'s poll loop was cancelled before
the room settled)... a caller that cares about 'still going' reads `status --json`'s `state` field
instead."*). Concretely: a harness runs `baton dispatch` without `--wait`, the lane reaches a gate and
pauses — the process exits **1**. Reading that as "a step failed" and abandoning a healthy, paused
room is the single most consequential misreading this table can produce, because §5's entire gate
contract depends on that paused room still being there to `baton decide` against. **The rule: exit code
1 alone never tells you whether the room is done. Read `state` from `terminal.json` or `baton status
--json` to distinguish `Failed` from `Running`/`Paused`.** `--wait` makes `run`/`dispatch` block until
the room reaches Terminal (or the wait is itself cancelled); without it, a non-1/0 exit code is the
only signal a lane is even still going, and it is unreliable for that purpose by design.

### §3 schema — `terminal.json` / `baton status --json`

```
{
  "state": string,                     // WorkflowOutcome, e.g. "Succeeded" | "Failed" | ...
  "steps": [
    {
      "id": string,
      "state": string,                 // StepStatus token
      "execution"?: string,
      "linkedFrom"?: string,           // set when this step's latest execution is an `baton resume`
      "usage"?: ExecutionUsageView,
      "linkedFromUsage"?: ExecutionUsageView,
      "liveness"?: "alive" | "dead" | "unknown"   // #1375, only present while this step reads "Running"
    }
  ],
  "outputs": [string],                 // resolved output paths
  "error": string | null,
  "try": string | null,                // corrected-invocation text; only set on a pre-ledger refusal
  "rejected": boolean                  // #1377, true iff some step settled via `DecisionType.Reject`
}
```
where `ExecutionUsageView` is
```
{ "wallClockMs": number, "tokensIn"?: number, "tokensOut"?: number, "turns"?: number }
```
(`WorkflowStatusView.cs`, `src/Baton/Status/ExecutionUsageView.cs`). `wallClockMs` is
always present when the object is present at all — derived from recorded start/exit timestamps; the
token/turn fields are independently omitted (never `null`, never fabricated as zero) when the
vendor's captured stdout carried no such figure.

**Notation and a real divergence.** `usage`/`linkedFromUsage` are correctly optional-and-omitted —
write it `"field"?: Type`, not `Type | null` with a comment contradicting itself. But `linkedFrom`
is **not** uniformly optional: `WorkflowStatusView` emits it as JSON `null` when absent (no
`JsonIgnore` attribute, `WorkflowStatusView.cs`), while the `fleet_status` variant omits it
entirely (`JsonIgnoreCondition.WhenWritingNull`, `FleetStepStatusView`,
`src/Baton.Cli/Mcp/FleetStatusTool.cs`), and the fleet variant additionally carries a
`timestamp` field the terminal-sentinel shape does not have. `terminal.json` and `status --json` are
one contract; `fleet_status` is a third, related shape with its own null-handling — see §6's schema.

**`liveness`/`rejected` (#1375/#1377) round-trip through `fleet_status` too (#1462).** `FleetStatusTool`
builds `FleetStepStatusView`/`FleetRoomStatusView` by copying named fields off the same
`WorkflowStatusView`/`WorkflowStatusStepView` projection — never a second probe or a second
computation — so `FleetStepStatusView.Liveness` and `FleetRoomStatusView.Rejected` are the identical
values `status --json` would report for the same room (`FleetStatusTool.cs`; the terminal-sentinel
path copies `sentinel.Liveness`/`sentinel.Rejected` since the sentinel already **is** a
`WorkflowStatusView`). A fleet_status caller can now tell a dead engine or a rejection apart from an
ordinary `Failed`/`Running` room without a second `status --json` call per room.
`liveness` is present only on a step this same projection calls `"Running"` — the identical gate
`StatusCommand.FormatStepStatus` uses before probing (a `Paused` step's engine has legitimately
exited; a step with no execution yet has nothing to probe) — so its mere presence in the JSON already
answers "does liveness apply here" before a caller reads its value. `rejected` carries no reason text
alongside it: `FlowEvent.ExternalDecisionRecorded` records no operator-supplied reason field, so
there is nothing structural to surface beyond the boolean fact itself; which step rejected, if that
matters, is `steps[].state == "Rejected"` — already a token distinct from `"Failed"`.

---

## §4 Workers and vendor adapters

Vendor-specific behavior is isolated inside `Baton.Vendors`; `Baton` understands only a single
canonical message protocol. Adapters live behind `IWorkerAdapter`, resolved via
`WorkerAdapterRegistry.Default` (`src/Baton.Vendors/WorkerAdapterRegistry.cs`) — the registry is the
authority on what is registered; this document deliberately does not count them. The two production
vendor adapters whose enforcement mechanics §9 measures are `ClaudeWorkerAdapter` and
`AgyWorkerAdapter`. Baton never reads, copies, forwards, or stores a vendor credential; it spawns the
vendor's own already-authenticated CLI. The `PreToolUse`/`agy-hook-check` enforcement below (§9) runs
as a fast, dependency-free stdin round trip, spawned directly by the vendor CLI on every tool call —
deliberately outside the workflow-execution pipeline, because `PreToolUse` blocks the model's own
turn until it returns.

What "vendor-neutral" guarantees, concretely: a harness author writing against `terminal.json`,
`fleet_status`, and the CLI verb table never needs vendor-specific branches — those seams are
adapter-internal. What it does not guarantee: enforcement mechanics, which genuinely diverge between
vendors and are stated exactly, not smoothed over, in §9.

---

## §5 Gates — exactly one, closed exactly one way

**There is exactly one gate type in this spec's surface: the workflow pause, `PausePoint`, closed
only by `baton decide`.** The harness answers it programmatically via `baton decide` (§2's `decide` row
for the verb shape). The decision vocabulary (`resume|reject|retry-with-revision|supersede`) is the
whole of it.

**The mid-lane permission-ask mechanism is deleted (#1417).** `PermissionGateTool.cs`
(the `aer_permission_ask` MCP tool, formerly `Baton.Mcp.Host` — wrote an `ask-<id>.json` file and blocked
up to 180s for an `answer-<id>.json` to appear, denying via a `revoked-<id>.json` on timeout) and
`PermissionReturnShape.cs` are gone, along with the daemon's `/api/rooms/permissions/answer` REST
answerer and its own
crash-reconciliation heal path (both previously in `DaemonHost.cs`, now `src/Baton.Cli/Daemon/DaemonHost.cs`
after #1458 folded the standalone Baton.Daemon project's `Program.cs` into it) — the two places that
ever wrote an `answer-<id>.json` file; `Baton.Cli` wrote none. Under this spec's harness-only surface,
that tool had no answerer left; keeping it would have meant a worker blocking on a rendezvous file no
code writes. **A lane is dispatched fully pre-cleared**: every capability a worker will need is
granted in `bindings.json` before `baton run`/`baton dispatch` is called (§9). There is no mid-lane ask.

**A worker that hits a capability it was not pre-cleared for is denied, fail-closed, by the
`PreToolUse`/`agy-hook-check` enforcement in §9** — the same mechanism that already exists for every
other denial, not a new one. The denial surfaces legibly: `FailureClassification.ToolDenied`
(`src/Baton/Domain/FailureClassification.cs`, one of the enum's four values — see §7 for the
other three) is the vocabulary a harness reads off the failed step in `terminal.json`. A harness that
sees `ToolDenied` re-dispatches — with a widened grant in a fresh `bindings.json`, or a narrowed task
that does not need the capability. That is the whole of the recovery path; there is no live channel to
answer the denial in place.

**The second ledger, honestly.** §2 already states this in full: `room.jsonl` carried the
mid-turn ask/answer/revoke triad this section retires, plus held-work/escalation/dormancy/orchestrator
machinery §7 retires for an unrelated reason (no resident orchestrator). `fleet_status` never reads
`room.jsonl` (`FleetStatusTool.cs`) — it only ever read `flow.jsonl`, the terminal sentinel,
and `snapshot.json`. So a room paused on a `PausePoint` shows up correctly in Fleet Glass (§6); a
room that — under the *prior* draft's design — was waiting on a mid-lane permission ask would not
have. That gap is now moot rather than fixed, because the mechanism it was a gap in no longer exists.

---

## §6 Fleet Glass — observability

This is the entire user-facing surface, unconditionally. `fleet_status`
(`src/Baton.Cli/Mcp/FleetStatusTool.cs`) is a read-only MCP tool that scans rooms across the fleet: it
leverages the terminal-sentinel fast path for terminal rooms and projects active rooms from bound
snapshots plus `flow.jsonl` when no sentinel exists yet (`FleetStatusTool.cs`). It reads
`BatonPaths.Rooms` plus any caller-supplied extra `roots` and does not itself depend on a running daemon
process — it opens files directly (`FleetStatusTool.cs`).

**Two-level drill-down, both levels of `fleet_status`'s MCP host, never a second application:** the
tool's per-room summary (level one, `fleet_status` itself) is what exists today. Level two — a room's
own `stdout` tail and `flow.jsonl` timeline, for debugging a specific lane — is now `room_detail`
(`src/Baton.Cli/Mcp/RoomDetailTool.cs`, #1427): a sibling tool in the same MCP host, gated by its own
`--room-detail-tool` flag in `Program.cs`, resolving a room by name or absolute path and returning a
bounded (64 KiB) tail of an execution's `.stdout.log` plus a bounded (500-entry tail) projection of
`flow.jsonl` (event type and writer-stamped timestamp per line, never the raw event payloads — both
halves are capped for the same reason `fleet_status`'s own output stays MCP-friendly). Which
execution's stdout: the most recently written one by default (a heuristic that can name the wrong
lane after a retry, since the newest write is not necessarily the one being debugged), or a
caller-pinned `execution` id to bypass the heuristic. Same direct-file-read posture as level one — no
daemon dependency, and a missing or still-running room, a held-open ledger, or a malformed line all
degrade to a partial view plus a `note`/`unreadable` marker, never a throw. This settles the prior
draft's open question: there is no separate diagnostic UI, dev or otherwise. Fleet Glass **is** the
diagnostic story, and its second level is scoped work against the same MCP tool surface, not a new
one.

The outbound push mailbox — the mechanism that would notify a harness of a state-change event without
polling — is **(new build)**. There is no `push`, `mailbox`, or outbound-webhook-shaped
component anywhere under `src/Baton.Cli/Mcp` or `src/Baton.Cli/Daemon` at HEAD — nothing broadcast-shaped
survives the daemon narrowing (`DaemonBroadcast` and `DoorbellMonitor` both died with it, #1417/#1420),
so the "unbuilt" ruling stands with no surviving near-miss to distinguish it from. Quota data (§7)
and gate-pending visibility both ride this mailbox once it exists; its transport (webhook,
log-append, something else) is unspecified here — that is design work for the build.

**Current reality, stated so this section cannot overclaim:** a transitional status page exists
today *outside this repo* — a pushed snapshot rendered remotely for the operator. #1413 tracks
folding its pipeline into `tools/`; it is the mailbox's display end and a prototype of the push
loop, not a product surface this spec endorses. "Never a second application" constrains what Baton
*builds and ships* — the MCP tool is the surface — and stays honest only while that page remains a
disposable prototype rather than a maintained app.

### §6 schema — `fleet_status`

Input:
```
{
  "roots"?: [string],             // extra directories containing rooms to scan
  "include_terminal"?: boolean    // default true
}
```
Output: a JSON array of
```
{
  "name": string,
  "path": string,
  "project"?: string,             // §8 registry: the project root this room was dispatched for
  "state"?: string,
  "steps"?: [
    { "id": string, "state": string, "execution"?: string, "linkedFrom"?: string,
      "timestamp"?: string, "usage"?: ExecutionUsageView, "linkedFromUsage"?: ExecutionUsageView,
      "liveness"?: string }
  ],
  "outputs"?: [string],
  "error"?: string,
  "try"?: string,
  "rejected"?: boolean
}
```
(`FleetStatusTool.cs`). Optional fields are omitted, never emitted `null`
(`JsonIgnoreCondition.WhenWritingNull` throughout `FleetRoomStatusView`/`FleetStepStatusView`);
`rejected` follows the same omit-when-uninformative convention via
`JsonIgnoreCondition.WhenWritingDefault`, so it is absent rather than emitted `false`. This is a
**third shape**, related to but not identical with `terminal.json`/`status --json` — see §3's note on
`linkedFrom` and `timestamp` for the concrete divergence; `liveness`/`rejected` themselves are
identical values across all three shapes (§3).

The scan itself is a **single-level** `Directory.GetDirectories` per root
(`FleetStatusTool.cs`) — it does not recurse, so project-grouped nesting is not found by the scan
alone. §8 depends on this fact directly, and closes it by unioning the scan with a registry rather
than by making the scan recurse.

### §6 schema — `room_detail`

Input:
```
{
  "room": string,                 // room name (resolved under BatonPaths.Rooms + roots) or an absolute path
  "roots"?: [string],             // extra directories to search when 'room' is a name
  "execution"?: string            // pin a specific execution id's stdout; default: most recently written
}
```
Output:
```
{
  "name": string,
  "path"?: string,
  "stdout"?: { "text": string, "truncated": boolean, "totalBytes": number, "source": string, "readError"?: string },
  "timeline"?: { "entries": [ { "type": string, "timestamp"?: string, "detail"?: string } ],
                 "truncated": boolean, "totalEntries": number },
  "error"?: string,
  "note"?: string
}
```
(`RoomDetailTool.cs`). Optional fields are omitted, never emitted `null`, the same convention as
`fleet_status`'s shapes. `stdout` is absent (not an error) for a room with no captured output yet;
`timeline` is absent for a room with no `flow.jsonl` yet (pre-ledger). A held-open ledger or a
malformed line surfaces as a single `timeline.entries` item with `"type": "unreadable"` and a
`detail` message, rather than failing the call. `error` is set only when `room` itself does not
resolve to a directory.

---

## §7 The daemon, narrowed

**The harness is the orchestrator.** There is no resident conversational presence a room maintains
between harness invocations. `RoomTurnHost`/`RoomWakeBridge` (deleted, #1420) and the daemon's
reassignment/pairing/broadcast REST surface went with the daemon narrowing below.

What the daemon narrows **to**: a **room-watcher serving the §8 registry** (`fleet_status` itself
needs no daemon, §6 — the watcher serves the registry the tool will consult, never the tool's own
file reads), the **snapshot push loop** feeding the mailbox (§6),
and the **quota-runway ledger** (below). Two more live responsibilities need a stated home rather
than silently dropping out with the rest of the deleted daemon surface:

- **`RoomRetentionSweep`** (`Program.cs`, a hosted service) — it prunes execution directories, and
  `ExecutionUsageProjector` has an explicit pruned-path fallback specifically because the sweep moves
  them (`src/Baton/Status/ExecutionUsageView.cs`). It is engine-adjacent housekeeping, not a UI
  concern, and belongs in the narrowed daemon's kept surface alongside the room-watcher.
- **Fleet-wide concurrency caps** — `DaemonSettingsStore` (`src/Baton.Vendors/DaemonSettingsStore.cs`,
  reading/writing `BatonPaths.SettingsFile`, i.e. `{Root}/settings.json`) plus `ConcurrencySlotGate.SetCaps`,
  applied at daemon startup (`Program.cs`). At HEAD this settings file holds only
  `GlobalConcurrencyCap`/`PerVendorConcurrencyCap` (`DaemonSettingsStore.cs`) — it is machine-wide,
  not per-room, so it belongs in the narrowed daemon too.

Explicitly **not** kept: pairing (`PairedClientsStore`), WebSocket broadcast (`/api/ws`,
`/api/ws/progress`), sidecar/Tailscale supervision, a desktop-owner-only auth tier, template-picker
endpoints, orchestrator reassignment, and the permission REST answerer (§5) — all of that existed to
serve `Baton.Ui`/`Baton.Mobile` and dies with them (Appendix).

### The quota ledger — what is new build, stated correctly

Polls vendor CLIs' print-mode `/usage`; accumulation from lane logs is attribution only, never the
reset-time source of truth. Quota data rides the push mailbox (§6). I could not find a `/usage`-polling
implementation, a runway projection, or push delivery for quota anywhere in `src/` at HEAD — that part
is genuinely **(new build)**.

What is **not** new build, and must not be re-derived: `FailureClassification`
(`src/Baton/Domain/FailureClassification.cs`) has **four** values —
`Retryable, Permanent, ExhaustedUntil, ToolDenied` — not two. `ExhaustedUntil` is load-bearing
throughout the scheduler, not a stub: it appears across `Baton/Scheduling/RetryEngine.cs`,
`Baton/Mutation/MutationInterface.cs`, `Baton/Outcomes/OutcomeClassifier.cs`,
`Baton/Status/WorkflowOutcome.cs`, and both adapters. Concretely, `AgyWorkerAdapter` already parses
a vendor-reported reset time into an `ExhaustedUntil` classification and a `retryNotBefore` instant
(`src/Baton.Vendors/AgyWorkerAdapter.cs`). So: the classification vocabulary, the retry/
dependency handling built on top of it, and at least one adapter's refusal-message parse into
`ExhaustedUntil` all exist today. What is missing is specifically the proactive `/usage` poll, the
runway projection, and the push delivery — build against that gap, not against a two-value enum that
does not exist.

**Both vendors' `/usage` support.** Both `agy -p "/usage"` and `claude -p "/usage"` answer
structured usage data without a model turn — measured live, with a dated primary-source transcript
for the `agy` half recorded in `docs/vendor-capabilities.md` (the vendor register, which outranks
this paragraph on vendor facts). Nothing in `src/` at HEAD implements a `/usage` poll for either
vendor yet — the measurement is the settled basis the quota ledger is built against, not a shipped
code path. Both vendors participate in the ledger.

---

## §8 Multi-project room registry

**Shipped (#1426).** Name the invariant: **`fleet_status` coverage never shrinks when daemon surfaces
are deleted** — a room that `fleet_status` could find before a given daemon endpoint was removed must
still be findable after. Regression-tested directly:
`FleetStatusToolTests.RegistryEntry_OutsideEveryScannedRoot_IsStillFoundByFleetStatus` registers a room
under a project directory passed as no `roots` entry and asserts `fleet_status` still returns it.

**The true reason this is a prerequisite, stated correctly:** it is not that deleting daemon surfaces
*shrinks* `fleet_status`'s coverage — `fleet_status` derives coverage from `BatonPaths.Rooms` plus
caller-supplied `roots` and nothing else at the scan layer (`FleetStatusTool.cs`); it does not depend
on any daemon surface, so deleting one cannot regress it. The real risk is narrower and still real:
the scan itself is **single-level** (`Directory.GetDirectories`, one call per root, §6) — it has no
notion of "every room across every project a harness might dispatch into," only "every room directly
under whichever roots I was told about." A harness that dispatches into a fresh project directory the
operator never passed as a `roots` entry was invisible to `fleet_status` until someone remembered to
add it. The registry closes *that* gap.

**The mechanism.** `RoomRegistryStore` (`src/Baton/Status/RoomRegistryStore.cs`, namespace
`Baton.Vendors` for the same reason `BatonPaths` lives there — `fleet_status` reads it with no
`Baton.Vendors` project reference) reads and writes `BatonPaths.RoomRegistryFile`
(`{BATON_HOME}/room-registry.jsonl`), one JSON line per registration: room directory path, project root,
created-at.

- **Writer.** `RunCommand.ExecuteAsync` — the one pump both `baton run` and `baton dispatch` share —
  registers the room right after creating its directory, on every call through that pump (a fresh
  dispatch, or a repeated `baton run` against a room this pump already started), so a registration lost
  to a crash between directory creation and the write is repaired the next time this pump runs against
  the same room. `baton dispatch` passes its own resolved workspace (honouring `--workspace`) as the
  project root; a bare `baton run` has no separate workspace concept and uses the process cwd. This does
  *not* cover the separate `baton resume`/`decide`/`supply` mutation verbs — they only ever act against a
  room `baton run`/`dispatch` already created and never re-register it, so a room whose very first
  registration attempt failed and is thereafter driven only through one of those verbs stays
  unregistered until the next plain `baton run`/`dispatch` against it. The write is fire-and-forget with
  respect to the run itself — an `IOException`/`UnauthorizedAccessException`/`WaitHandleCannotBeOpenedException`
  is reported on stderr and swallowed, never surfaced as a run failure, because the registry only ever
  *adds* `fleet_status` coverage and must never gate a dispatch.
- **Format: append-only JSONL, not a rewritten JSON map, guarded by a named `Mutex`.** Every dispatch
  that creates a room is a separate, potentially concurrent `baton` process — that concurrency is the
  reason a fleet-wide registry exists at all. A last-writer-wins map would need a read-modify-write
  cycle on every registration; append avoids that. `FileMode.Append` alone is **not** atomic across
  processes on Windows — measured with no lock and no `FileShare` restriction at all: six concurrent
  processes appending under `FileMode.Append`/`FileShare.ReadWrite` lost roughly a fifth of their
  lines, some to two JSON objects concatenated with no newline between them. The shipped writer
  additionally opens with the narrower `FileShare.Read` (the same choice `FlowEventLogWriter` makes
  for `flow.jsonl`), which stops that byte-level interleaving on its own — but not losses: without a
  lock, a second concurrent writer gets a sharing-violation `IOException` instead, which the registry's
  fail-open contract requires swallowing, i.e. a dropped registration rather than corrupted bytes.
  `RoomRegistryStore` closes that gap by serializing every access, read or write, behind one named
  `Mutex` keyed on the registry file path, so a concurrent writer waits and then succeeds rather than
  losing its registration to a sharing violation
  (`RoomRegistryStoreTests.Concurrent_appends_from_many_tasks_lose_no_entries` drives fifty concurrent
  writers at the store's public API and asserts none are lost). "Last-writer-wins per room" is the
  *read-time* semantic on top of that — `RoomRegistryStore.ReadDistinctByRoomAsync` folds repeated
  lines for one room path down to the last one written.
- **Reader.** `FleetStatusTool` unions the registry's entries with its existing `BatonPaths.Rooms` +
  caller `roots` scan. A registry entry whose room directory no longer exists is skipped (not pruned
  from the file yet — see below). Every room `fleet_status` returns, whether found by the scan or the
  registry, carries a `project` field (§6 schema) when a registry entry names one, so callers can
  group the level-one summary by project without enumerating project directories themselves.
- **Malformed/missing tolerated.** A missing registry file reads as no entries; a malformed line is
  skipped without failing the read or hiding the well-formed lines around it — the registry degrades
  to exactly what the directory scan alone would have returned, never fewer.

**Left undone, reported rather than silently dropped:** stale entries (a registered room directory
later deleted) are skipped on every read but not physically pruned from the file. The `Mutex` above
would make a compaction rewrite safe against a concurrent appender; writing that rewrite (fold to one
line per room, drop entries whose directory no longer exists, replace the file under the same lock)
was judged out of scope for this build regardless. The registry file grows without bound as rooms are
created and later cleaned up by `RoomRetentionSweep` (§7); a follow-up should add that compaction
(e.g. gated the same way `RoomRetentionSweep` already is) or confirm the growth rate is immaterial in
practice.

---

## §9 Bindings and permissions

**`bindings.json` is the room's standing permission for the room ∩ step scopes.** For a harness, "answer
once" means: the bindings file is the pre-answered ladder, written once at dispatch/run time and
consulted on every subsequent decision against that room. **Re-prompting a headless lane for a
permission it already carries in its bindings is a spec violation**, not a defensible conservative
default. `DispatchCommand.ExecuteAsync` writes bindings into the room directory
(`src/Baton.Cli/DispatchCommand.cs`) before `RunCommand` runs; `baton decide` requires `--bindings`
explicitly on every call (`DecideOptionsParser.cs`: *"pass --bindings <path-to-bindings.json>
naming the same bindings the paused room was dispatched with"*) — there is no separate global
last-used-file fallback the CLI path is ever subject to.

**The three-scope model survives: project ceiling ∩ room ∩ step, always narrowing, never widening.**
`bindings.json` is only the **room ∩ step** half of that intersection. The **project ceiling** — the
owner's own control on what any harness-authored `bindings.json` can grant in the first place — lives
in Baton's own app-level config, never inside the project tree, so a compromised or over-permissive
project cannot author its own way past it. `BatonPaths.SettingsFile` (`{Root}/settings.json`,
`BatonPaths.cs`) is the one app-level, per-machine config file this tree has today, and at HEAD
it holds only the daemon concurrency caps (`DaemonSettingsStore.cs`) — **no project-ceiling
implementation exists there or anywhere else in `src/` that I could find.** This is
`UNVERIFIED — fill from code`: the ceiling's register is settled direction, not a shipped contract,
and a build against this section should not assume `BatonPaths.SettingsFile` is already shaped for it.

**Grants fail closed — as a dispatch-time obligation, not a measured runtime property.** The rule:
if a denial cannot be enforced for the chosen vendor, the run must not start. Read it together with
the broken-hook paragraph below, which this rule would otherwise contradict: a hook that fails to
*load* fails **open** at runtime, on both vendors — that measured fact is precisely *why*
enforceability must be established before dispatch rather than trusted at runtime. What exists today
is the measurement (`gate.broken-hook-fails-open` and its `agy` sibling in
`tools/vendor-verify/verify.py` characterize the hazard), not an enforcement of the rule itself. A
dispatch-time probe that a *fresh environment's* hook actually loads is
**(new build)** — until it exists, this guarantee is only as strong as the environment's hook
installation, and a harness author dispatching into an unfamiliar environment should treat it as
such.

**The `PreToolUse`/`agy-hook-check` hook stays the enforcement mechanism** — the only enforcement
point over the toolset a worker actually has, since `--allowedTools` pre-approves rather than
restricting (measured directly: `PermissionGrant.cs`, citing the
`gate.allowedtools-is-preapproval-not-ceiling` sentinel check in `tools/vendor-verify/verify.py`).
Baton ships one on every
spawned worker, on both vendors, via `hook-check`/`agy-hook-check`
(`Program.cs`, `src/Baton.Cli/HookCheckCommand.cs`, `src/Baton.Cli/AgyHookCheckCommand.cs`).

**The hook is binary: allow / deny, nothing else.** The ask band that once made it ternary
(`BATON_HOOK_ASK_TOOLS`, the `permissionDecision: "ask"` STDOUT envelope) was part of the mid-lane
ask machinery and is DELETED (#1417) — lanes are fully pre-cleared, so an ungranted capability
fails closed (the hook's own exit code 2 inside claude's `PreToolUse` protocol — a vendor-internal
convention, unrelated to §3's `ValidationRefused` CLI exit code that happens to share the number)
with no human routing, and a tool on the denied list is denied regardless of anything else. A denial
surfaces as `FailureClassification.ToolDenied` (§5, §7) — that is the vocabulary a harness reads.
(#1390 tracks a measured hollow-success defect against this: a denied worker that exits 0 anyway can
read as `Succeeded` — the classification is the contract; that bug is open, not folklore.)

**"Denied" at runtime means:** the hook exits non-zero on claude, or returns a `decision` field
refusing the call on agy — the worker is told it was refused and continues rather than dying.

**A broken hook fails open on both vendors — and its silence is measured on `claude` only.**
`tools/vendor-verify/verify.py`'s `gate.broken-hook-fails-open` check measures a claude `PreToolUse`
hook that cannot execute (missing script, bad interpreter, CRLF-plus-space path) as an **allow**, and
separately measures whether the CLI says anything about the failure at all — distinguishing "fails
open loudly" (detectable at startup) from "fails open silently" (not). Its `agy` sibling,
`agy.broken-hook-fails-open`, is written to claim **fail-open only** — its own description states
plainly: *"whether agy REPORTS the failure is not claimed."* A harness author dispatching into a
fresh config directory or a containerized environment must not assume a hook that failed to load will
announce itself on `agy` — that half is genuinely unmeasured, not merely undocumented.

**What a harness author must configure before dispatch does anything:** a `bindings.json` naming
each worker role's adapter, **model** (§2: always pinned at dispatch time, never a mid-lane choice),
and permission grant, resolvable at both dispatch time (writes the room's copy) and decide time
(reads only the room's copy, per this section's own rule above). `baton resume` is bound by the same
rule as `decide`: the bindings passed continue the room's own standing permissions — the
composition never widens mid-room through any verb.

**The `review` role's ceiling: read-only `git`/`gh`, enforced, not a flat shell refusal (#1456,
operator-approved reversal of #1355).** `WorkerRoles.json`'s `review` entry now carries
`run_shell_commands: true` scoped by `shell_command_patterns` to exactly: `git diff`, `log`, `show`,
`blame`, `status`, `grep`, `rev-parse`, `merge-base`, `ls-files`, and `git branch --list`; and `gh pr
view`/`diff`/`checks`, `gh issue view`. `denied_shell_command_patterns` closes the named mutating
families (`commit`, `push`, `merge`, `checkout`, `switch`, `reset`, `clean`, `gh pr
comment`/`edit`/`merge`, `gh issue comment`/`edit`, `gh label`) as a standing, subtractive "never"
(0022's DenyAlways) on top of the allowlist. `gh api` is deliberately **not** granted: its HTTP
method is a runtime flag/field (`-X`, `-f`), not something `ShellCommandPatternMatcher`'s glob
prefix-match can bind to GET-only, so admitting it would be an unenforced hole wearing a scoped
label rather than an actually-scoped grant.

**The enforcement is claude's `--allowedTools`/`--disallowedTools`, and it is real for this shape —
correcting this section's own earlier framing where it over-generalised.** §9 above (and
`PermissionGrant.CategoriesDefeatedByTheShell`'s prior doc comment) said flatly that `--allowedTools`
"pre-approves rather than restricts." That is accurate for **cross-tool substitution** — a withheld
`Write` reached through a granted `Bash` (#529) — and for a **wholly omitted** tool name (#331: a
`Bash` absent from both lists still ran). It is not the full picture for **same-tool Bash pattern
discrimination**: `docs/vendor-capabilities.md`'s "canonical ceiling" measurement shows
`--disallowedTools Bash(pattern)` enforced, with precedence over `--allowedTools`, and a Bash pattern
*not* on the allow list denied outright (`Bash(npm *)` refused when only `Bash(git *)` was granted —
the negative control that makes it a ceiling rather than a coincidence). Two granularity limits of
that measurement, stated rather than assumed (#1456 second reader): the negative control differs at
the PROGRAM level (`npm` vs `git`), not the subcommand level — whether an unlisted `git`/`gh`
subcommand is denied the same way is unmeasured — and nothing measures whether the pattern is
matched against the whole command line or only its leading tokens (shell chaining/redirection inside
an allowed prefix). Until both are measured and recorded in `docs/vendor-capabilities.md`, the deny
list above enumerates every known-mutating `git`/`gh` subcommand family explicitly rather than
relying on allowlist-omission alone, and the read-only assertion should be read as resting on that
explicit deny-subset plus the measured program-level control. `review`'s grant relies on exactly
that: only the enumerated `Bash(git …*)`/`Bash(gh …*)` patterns are pre-approved (no bare `Bash`),
and the deny-subset above is belt-and-braces on top. `PermissionGrant.ShellCommandsAreReadOnly`
(new, #1456) is the named, author-asserted escape hatch that lets a grant like this one compose
without widening `WriteFiles`/`NetworkAccess` just to satisfy `CategoriesDefeatedByTheShell`'s
coherence check — it only counts when a non-empty pattern list backs it (an unscoped shell claiming
read-only is refused as incoherent); see that type's own doc comment for exactly what the assertion
claims and does not derive.

**Network honesty: `review`'s `network_access` stays `false`, and `gh` reaches github.com anyway.**
The categorical `NetworkAccess` grant (claude's `WebFetch`/`WebSearch`, arbitrary URLs) is
deliberately **not** granted — that would be a materially larger surface than this role needs. But
the allowed `gh pr view`/`diff`/`checks`/`gh issue view` patterns genuinely talk to github.com as
part of doing their job. So `review`'s "no network" posture is true of the categorical grant and
false of the worker's actual reach: state it that way rather than letting the flag imply a stronger
guarantee than it gives. `ShellCommandsAreReadOnly` is what lets this narrow, command-scoped network
reach coexist with `NetworkAccess: false` in the coherence check — see the field's own doc comment.

**`agy` cannot express this at all, and the review role's shell reversal does not reach it.**
`AgyWorkerAdapter.TryTranslatePermissionGrant` refuses `RunShellCommands` without `NetworkAccess`
outright (no scoped-shell-without-network exists on that vendor, #1387 is the open ask for one) —
unchanged by this work. `review`'s tier defaults to `claude` (`WorkerTiers.json`'s `frontier` entry),
so a default dispatch is unaffected; an operator who overrides `--adapter agy` on `review` now gets
`PermissionGrantUnsupportedException` at bind time — a loud refusal, not a silent drop back to
`review`'s pre-#1456 no-shell shape. This is the same #529 coherence rule §9 already enforces
everywhere else, applied to a grant that #1355 had previously kept flat specifically to avoid it;
#1456 accepts the agy-side refusal as the honest cost of giving claude real scoped shell rather than
declining both to keep the two vendors' capability identical.

**`tools/baton-agy-loop/dispatch.py`'s own grant model is extended to match.** That tool reads the
same `WorkerRoles.json`/`WorkerTiers.json` catalog (`_load_worker_catalog`, the #836 shared-source
pattern) but has its own `grant_refusal()` coherence check and its own `build_bindings()`
permission-grant construction. All three scoped-shell fields are exported on
`BuiltInWorkflowTemplates.RoleTemplateExport` (so `baton templates --json` carries them),
`grant_refusal()` mirrors `ShellCommandsAreReadOnly`'s exact exemption (WriteFiles/NetworkAccess
only, never ReadFiles), and `build_bindings()` threads the fields into the `PermissionGrant` it
actually sends — without that last step the tool would have dispatched `review` with an UNSCOPED
shell grant, the silent hole the whole design refuses elsewhere.

---

## §10 What is explicitly out of scope

- **Chat as a product surface.** Chat is one internal workflow shape a harness can dispatch (§2); it
  is not a thing a person opens and drives turn by turn.
- **Session-parity UI (desktop/phone daily-driver use).** Nothing here promises feature parity with
  either vendor's own app.
- **Interactive mid-run steering.** Reaching into a running worker mid-generation to redirect it
  without stopping it first is out of scope; only cancellation-then-restart and between-step
  pause/decide (§5) exist.
- **Phone pairing and remote *viewing* infrastructure built for a paired client.** `PairedClientsStore`
  and WebSocket broadcast (§7) are archived; the mailbox (§6) is the harness-era replacement for
  "something remote learns what happened," not a client-pairing model.
- **A resident orchestrator that decides on a human's behalf.** There is no room-resident presence;
  the harness is the decider, always (§5, §7).
- **Remote *dispatch* triggering — closed, orchestrator-only.** Settled, not open: remote dispatch
  already exists as "talk to your harness from the phone" — a Claude Code mobile session (or any
  other agent that can run CLI verbs and read `terminal.json`/`fleet_status`) driving `baton dispatch`,
  which keeps one set of hands on the workers. A direct phone-to-worker control path would be a
  second interaction surface outside the orchestrator, which the one-surface design retires.
  `Baton.Sidecar` — the Go tsnet component that existed solely to give a paired remote client
  zero-config Tailscale reach to the daemon's REST/WS API — is DELETED, done (#1420): it was a real,
  tracked Go module (an earlier draft claimed otherwise; a lane verified it existed — corrected), and
  it went with the pairing surface it served, along with `Baton.Daemon.csproj`'s optional copy step for
  its binary.
  **The harness seam is vendor-neutral, deliberately:** any agent that can run `baton` CLI verbs and
  read `terminal.json`/`fleet_status` can be the orchestrator. Claude Code is the current occupant of
  that seam, not a requirement of it.

---

## §11 Register

This document and the code it cites are the **only** registers. `docs/design/*` and the prior
`spec/*` files are deleted, not archived — there is nothing left to supersede or cross-reference,
and a future reader will not find them. `docs/decisions/*` was deleted the same way, then
**partially restored** (#1431): the records still cited by live code are back verbatim under
`docs/decisions/` as read-only history — never edited, resolved mechanically by the
comment-citation gate — and the uncited rest remain reachable only through git history. A restored
record is a citation target, not a register: this document still owns what is settled. Every rule
this document states was
previously justified by a decision record; that justification is now stated inline, in the section
the rule belongs to, and the supersession apparatus (numbered decisions, "supersedes 0049"-style
prose) is dropped entirely.

New decision records are created **fresh**, only when a genuinely new decision is made after this
document ships — never retroactively, and never to re-derive something this document already states
as settled. If a future change needs to record its own reasoning, it gets its own record; it does not
reach backward to reconstruct a numbering scheme that no longer exists.

### C-10 — Windows-only build, test, CI, and packaging

The owner runs everything on one Windows machine. Build, test, CI, and packaging are Windows-only:
no ubuntu/macos CI legs, no non-Windows pixi platforms shipped as a support target, and no per-OS
conditional kept alive for a platform that no longer builds (#1405). This is a statement about what
this repo ships and is verified on. #1458 folded aer-core into this repo as `native/core`, a Rust
crate built and tested Windows-only through this repo's own CI like everything else; #1474 then
ported that engine into plain C# and deleted `native/core` outright, so there is no longer a second
toolchain or a second CI leg to say this about — the whole engine is .NET, verified the one way this
entry already describes. This was never a statement about the archived `aer-works/aer-core` repo
`native/core` was imported from (a separate, now-frozen repo whose own historical CI is out of this
decision's scope) or about a vendor CLI's own OS support (`docs/vendor-doc-audit.md`,
`docs/vendor-capabilities.md`).

**Carve-out, so this entry and `pixi.toml` never contradict each other:** `pixi.toml`'s
`platforms` list keeps `linux-64` alongside `win-64`. That is a dev-sandbox accommodation — a Claude
Code cloud session doing *development* work on this repo from a Linux sandbox — not a second support
target; nothing is built, tested, or packaged for it, and `osx-arm64` is dropped outright.

---

## Appendix: full subsystem ruling table

One vocabulary note, so this table and §11 never diverge: code is **DELETED** or **NARROWED** —
git history is the archive; "ARCHIVE" as a distinct ruling applied to nothing and is not used here.

| Project / verb | Ruling | Note |
|---|---|---|
| `Baton` | **KEEP** | Engine core; vendor/UI-agnostic; untouched by this reset except that `room.jsonl`'s machinery (§2, §5) is now dead code from the harness surface's perspective — kept in place, not exercised. |
| `Baton.Vendors` (incl. `BuiltInWorkflowTemplates`) | **KEEP** | The cross-vendor seam; the template catalog narrows to built-in only. |
| `Baton.Cli` | **KEEP**, verb set narrows | `run`/`dispatch`/`decide`/`cancel`/`supply`/`resume`/`status` stay; `templates` narrows to the built-in catalog. |
| `Baton.Mcp` / `Baton.Mcp.Host` | **KEEP**, grows | `fleet_status` is the anchor and gains the §6 drill-down levels; `YieldTool`, `MemoryProposalTool` stay, orthogonal to this reset. `PermissionGateTool` and `PermissionReturnShape` — the ask machinery — are **DELETED** (#1417, §5); confirmed `PermissionReturnShape` had no other consumer in the tree. |
| `Baton.Daemon` | **NARROWED — done (#1420)** | Every REST/WS route, pairing, WebSocket broadcast, sidecar supervision, template-picker endpoints, and orchestrator reassignment are deleted; the permission REST answerer (`/api/rooms/permissions/answer`) and its `DoorbellMonitor`/`PendingGateRegistry`/crash-reconciliation plumbing were already **DELETED** (#1417). `Baton.RoomSession` (the room-reading path `RoomClient`/`MainWindowViewModel` were replaced with, #1412) is deleted too, #1420 — no caller of it survived once every route was gone. What remains is a bare hosted-service runner: mutex, settings load, fleet-wide concurrency-cap apply (`DaemonSettingsStore`/`ConcurrencySlotGate`), and `RoomRetentionSweep`. The room-watcher (serving `fleet_status`/the registry, §8), the snapshot push loop (§6), and the quota-runway ledger (§7) are unbuilt new work for a later PR, not something this narrowing preserved — homes stated in §7. |
| `Baton.Ui` | **DELETED** (#1412 Part 2) | Not a description of the existing Avalonia app with features removed — a full archive, then deletion. Fleet Glass (§6) is the diagnostic surface, built as MCP-tool levels, never a UI app. |
| `Baton.Ui.Core` | **DELETED** (#1412 Part 2) | `RoomClient` and `MainWindowViewModel` were named explicitly here because `Baton.Daemon`'s PORT row above depended on both and the narrowing had to break that dependency, not carry it forward silently — resolved by extracting the salvageable read-model surface into `Baton.RoomSession` (#1412 Part 1) before deleting the rest. The bulk (`ChatViewModel`, `RoomsViewModel`, `RemoteViewModel`, `TemplateEditorViewModel`, `StandingPermissionsViewModel`) was UI-surface logic for the retired product and is gone with it. `RoomProjection.cs`, `RoomFilesProjector.cs`/`RoomFilesViewModels.cs`, and `ExecutionHistoryProjector.cs`'s equivalents lived on in `Baton.RoomSession` — itself deleted in full, #1420, once `RoomClient` and every daemon route were gone and nothing called them. |
| `Baton.Mobile` | **DELETED** (#1407) | No harness-driven use case; deleted along with its dedicated build machinery (CI job, pixi tasks, scripts) rather than left archived. |
| `Baton.Sidecar` | **DELETED** — done (#1420) | The tracked Go module and `Baton.Daemon.csproj`'s optional binary copy step both went. Remote dispatch is closed, orchestrator-only (§10); no resurrection case remains. (An earlier draft claimed the project was absent from the tree; corrected — it existed and was deleted deliberately.) |
| `Baton.Workers.Dialogue` | **DELETED** (#1408) | Vendor-neutral multi-model machinery that served the retired interactive/chat product; no harness-facing use case survives this reset. |
| `Baton.CrashTestHost`, `Baton.Architecture.Tests` | **KEEP** | The gate mechanisms stay untouched. |
| `Baton.Journeys.Tests`, `Baton.Plan.Tests` | **DELETED** (by this spec's own landing PR) | Both existed solely to cross-check `docs/plan.md` and `spec/journeys.md`, deleted with them; harness-facing journeys are future work that brings its own checks when it exists. |
| `docs/design/*` | **DELETE** | Per §11 — not archived, deleted. Its methodology (settle definition before screens) is worth reusing as a technique; its content does not survive and there is nowhere left for it to live. |

---

## Uncertain

Claims I could not verify by reading the tree, or that rest on something outside this session's
reach:

- **`BatonPaths.SettingsFile` has no project-ceiling implementation at HEAD.** I read
  `DaemonSettingsStore.cs` in full — it holds only `GlobalConcurrencyCap`/`PerVendorConcurrencyCap`.
  The three-scope model's ceiling half (§9) is settled direction, not a shipped contract; a build
  against §9 should not assume any existing file is already shaped to hold it.
- **The exact shape of the outbound push mailbox (§6).** Unbuilt; I could not verify anything about
  its intended transport beyond "quota data rides it" and "gate-pending visibility rides it," both
  stated as rulings rather than measured facts.
- **The room registry's (§8) registration mechanism**, and whether it shares an implementation with
  the quota ledger (§7) or is fully separate. Both are named as parallel new-build items with no
  stated relationship; I treated them as independent.
- **Whether `Baton`/`Baton.Vendors` have silently accreted a human-watching assumption anywhere
  outside the paths this document cites directly** (terminal sentinel, status projection, hook
  enforcement, `FailureClassification`, `PermissionGrant`). I did not do a full pass of scheduling
  code; `Baton.Architecture.Tests` is the stated defense and I did not verify its actual coverage.
- **`YieldTool`/`MemoryProposalTool` in `Baton.Mcp.Host`.** I confirmed they exist and are distinct from
  the archived `PermissionGateTool`/`PermissionReturnShape`, but did not read their implementations —
  the Appendix's "orthogonal to this reset" call is a structural inference (they are not part of the
  ask machinery, the daemon, or the UI), not a read-through verification of their own content.

---

## Naming note

The product converged on **Baton everywhere** (#1458): the CLI binary is `baton`, namespaces are `Baton.*`, state lives at `~/.baton`, and the tree is the one-binary, five-project shape this document describes throughout (`src/Baton` engine — including the managed process-execution core since #1474 — `src/Baton.Vendors`, `src/Baton.Cli` with `baton mcp` and `baton daemon` as verbs, two test projects). Every `Baton.*`/`baton` citation in this document refers to the current tree.

