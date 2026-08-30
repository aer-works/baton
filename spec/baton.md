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
it is an **agent harness**: a program that calls `aer dispatch`, polls for a completion sentinel, and
reads structured output. The harness is the user this spec is written for.

Two invariants govern everything below:

- **Routing never reads conversation content.** Flow's scheduling logic reads structured outcomes —
  exit codes, declared outputs, explicit tool returns — never the meaning of what a worker said. This
  is a design invariant held by review, not a gated property: `Aer.Architecture.Tests` (kept, per the
  Appendix) pins the reference-direction half, and its own header states that no static test can
  honestly assert the no-content-reads half — do not cite it as enforcement of this bullet.
- **The journal is the system of record.** Every state a room can be in is a projection of recorded
  events; the system cannot be in a state it has not recorded. §2 states plainly that this is now
  true of *two* journals, not one, and what each one is for.

What Baton is **not**, stated as exclusions (§10 expands each):

- Not a chat product. Chat is one internal *workflow shape* a room can run, not a product surface a
  person opens.
- Not session-parity with a phone or desktop app. There is no daily-driver client this spec assumes
  exists, and none of `Aer.Ui`, `Aer.Ui.Core`, `Aer.Mobile`, or `Aer.Sidecar` survives this reset
  (Appendix).
- Not an orchestrator that decides on a human's behalf by default. §5 states the harness-facing gate
  contract directly: exactly one gate, closed exactly one way.
- Not a UI product. Fleet Glass (§6) is the entire observability surface, full stop — not "at most a
  dev diagnostic surface pending a decision." That decision is made: Fleet Glass, extended with a
  two-level drill-down, is a diagnostic surface built as **(new build)** levels of the MCP tool
  itself, never a second application.

---

## §2 The dispatch unit

A **room** is one working directory: `~/.aer/rooms/<room>/` (`AerPaths.Rooms`,
`src/Aer.Flow/Status/AerPaths.cs`). One directory may contain several repositories; the room does
not know or care.

A room holds, at minimum: `room.json` (the room-kind marker — `AerPaths.RoomMetadataFileName`,
`AerPaths.cs`; absence reads as a workflow room), `bindings.json` (the standing worker grant —
`AerPaths.RoomBindingsFileName`, `AerPaths.cs`), `flow.jsonl` (the workflow event log —
§3), `artifacts/`, and, once terminal, `terminal.json` (§3). `snapshot.json` is present for any
room that has been dispatched at least once — `fleet_status` treats its absence as "no bound
snapshot" and reports it as an error entry rather than a state (`src/Aer.Mcp.Host/FleetStatusTool.cs`).

**There are two independent event logs, not one, and this spec states both honestly.**
`flow.jsonl` is the workflow ledger — steps, executions, decisions — and everything in §3–§9 below
reads and writes only this one. A **second** ledger, `room.jsonl`, exists in the same engine
(`src/Aer.Flow/Domain/RoomEvent.cs`, `src/Aer.Flow/Store/RoomEventLogReader.cs`,
`RoomEventLogWriter.cs`, `src/Aer.Flow/Projection/RoomProjector.cs`,
`src/Aer.Flow/Mutation/RoomMutationInterface.cs`) and its full event vocabulary is: held-work
dispatch/escalation/resolution, grant record/amend/revoke, ask-time escalation, turn-host dormancy
entered/cleared, mid-turn permission ask/answer/revoke, standing-permission revocation, the
workflow on/off switch, worker join/rename, and orchestrator (re)assignment
(`RoomEvent.cs`).

State it plainly: **every one of those event kinds is written only by code this reset deleted.**
The mid-turn permission ask/answer/revoke triad is the deleted ask mechanism (#1417, §5). Held work,
escalation, dormancy, and orchestrator assignment are the resident-orchestrator/wake-loop model
`Aer.Daemon`'s `RoomTurnHost`/`RoomWakeBridge` implement, and that model has no referent left once
the harness — not a resident presence — is the decider (§7). Worker join/rename and the workflow
on/off switch belong to the interactive multi-participant chat room product `Aer.Ui`/`Aer.Mobile`
served. I checked: neither `src/Aer.Cli` nor `src/Aer.Mcp.Host` reference
`RoomMutationInterface`, `RoomEventLogReader`, or `RoomEventLogWriter` anywhere — the harness-facing
surface this spec describes has never touched `room.jsonl`, and `fleet_status` reads only the
terminal sentinel, `snapshot.json`, and `flow.jsonl`
(`FleetStatusTool.cs`) — never `room.jsonl`. Its type definitions stay in `Aer.Flow` because
Architecture Rule 1 keeps the journal engine-owned regardless of who reads it, and deleting dead
infrastructure is a separate cleanup this document does not scope — but a harness author should read
`room.jsonl` as **inert**: nothing in the dispatch/decide/status/fleet_status surface this spec
describes writes to it or reads from it.

A harness invokes work two ways, both in `src/Aer.Cli/Program.cs`:

- **`aer run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>]
  [--echo-worker] [--wait]`** — runs an authored `WorkflowDefinition` to a terminal state or a pause
  (`src/Aer.Cli/RunOptionsParser.cs`).
- **`aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>]
  [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>]`** — the one-shot
  form: `<name>` resolves to either a worker role (needs `--spec`) or a built-in template
  (`src/Aer.Cli/DispatchOptionsParser.cs`). Left unset, `--room-dir` derives a fresh, unique
  directory under `AerPaths.Rooms` per invocation — never a stable name derived from `<name>`, so a
  second `aer dispatch review` reruns rather than resuming the first's terminal snapshot. Bindings are
  written into the room directory by `DispatchCommand.ExecuteAsync`
  (`src/Aer.Cli/DispatchCommand.cs`, via `WorkerBindingConfigWriter.SaveToFileAsync`) before
  `RunCommand` is invoked underneath it.

A room's model is always pinned in `bindings.json` at dispatch time — there is no runtime model
choice a harness makes mid-lane; §9 covers the bindings contract. `aer resume`, `aer decide`, `aer
cancel`, and `aer supply` continue an already-dispatched room; §5 covers `decide` specifically.

### §2 schema — the CLI argument table

| Verb | Usage | Source |
|---|---|---|
| `run` | `aer run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--wait]` | `RunOptionsParser.cs` |
| `dispatch` | `aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>]` | `DispatchOptionsParser.cs` |
| `resume` | `aer resume <room-dir> --worker <role> (--message <text> \| --message-file <path>) --bindings <bindings-file> [--workflow-id <id>]` | `ResumeOptionsParser.cs` |
| `decide` | `aer decide <room-dir> --execution <execution-id> --type resume\|reject\|retry-with-revision\|supersede [--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]` | `DecideOptionsParser.cs` |
| `supply` | `aer supply <room-dir> --worker <role> --output <name> --file <source-path> --bindings <bindings-file> [--workflow-id <id>]` | `SupplyOptionsParser.cs` |
| `cancel` | `aer cancel <room-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]` | `Program.cs` |
| `status` | `aer status <room-dir> [--follow] [--json]` | `StatusOptionsParser.cs` |
| `templates` | `aer templates [--json]` | `Program.cs` |

`templates` narrows to the built-in catalog only (`Aer.Adapters`'s `BuiltInWorkflowTemplates`) —
there is no authoring UI to browse a saved-template library visually against (Appendix, R7 in the
old numbering — dropped here, since there is no longer a separate register to number rulings
against).

---

## §3 The lane protocol (completion contract)

`terminal.json` is written into a room directory the moment its workflow reaches a terminal state —
the completion signal a harness should watch instead of polling `aer status` prose or racing the
`aer run`/`aer dispatch` process's own exit
(`src/Aer.Flow/Status/TerminalSentinelWriter.cs`). It is written **last** — after every output an
outcome could reference already exists on disk — via a temp-file-then-atomic-move sequence, so a
file-watching harness never observes a partial write (`TerminalSentinelWriter.cs`). It is the
identical shape `aer status --json` prints (`WorkflowStatusView`), so a file-watcher and a polling
`status --json` caller read one contract for that pair specifically
(`src/Aer.Flow/Status/WorkflowStatusView.cs`) — `fleet_status` is a **third, related** shape;
see §6.

**Its absence does not always mean "not terminal yet."** Two exceptions, both real:

1. `TerminalSentinelWriter.WriteValidationRefusedAsync` — the pre-ledger refusal path — is only
   invoked when `RoomLedgerProbe.HasLedger` is false (`src/Aer.Cli/Program.cs`,
   `src/Aer.Cli/RoomLedgerProbe.cs`: a `flow.jsonl` that exists and is non-empty). A room that
   already has a real ledger — e.g. a paused room re-dispatched with a bad `--spec` — returns exit code
   2 (`ValidationRefused`) with **no sentinel written**, because the room's ledger (or a still-live
   pump) is its real terminal record and a fresh refusal must not overwrite it with a fabricated
   `Failed`/no-outputs sentinel. `aer resume`'s own refusal path (`Program.cs`) never writes
   a sentinel at all — a resume always targets an already-ledgered room.
2. `RoomHeld` (exit code 5, below) also writes no sentinel: the room may be perfectly healthy (a live
   pump, or a background sweep's brief lock), and writing `Failed` here would tell a file-watcher a
   running room just died while `aer status --json` reads the same room as `Running` at the same
   moment (`Program.cs`).

So: absence means "not terminal yet, **or** refused against an already-ledgered room, **or** another
Flow instance currently holds it" — never simply "never started." A harness that needs to
distinguish these reads `aer status`/`flow.jsonl` directly rather than inferring from the sentinel's
absence alone.

**The sentinel can also disappear.** `TerminalSentinelWriter.DeleteStaleSentinel`
(`TerminalSentinelWriter.cs`) removes a prior sentinel when a room is re-run, so that retrying a
room that previously failed pre-ledger does not leave the old `terminal.json` in place for the whole
duration of a new, genuinely in-progress attempt. A file-watching harness must expect `terminal.json`
to vanish and reappear across a re-dispatch of the same room directory, not treat its disappearance
as an error.

`aer status` is read-only, produces no `CommandResult`, and always exits 0 when it manages to print a
status at all (`Program.cs`) — it cannot complete a room or substitute for watching the
sentinel.

**Two open defects a harness author must know about this contract, cited rather than smoothed
over:** #1375 — `status --json` can report `Running` indefinitely for a dead engine (a crashed pump
leaves no terminal record for the projection to read), so "Running" is a claim about the ledger, not
a liveness proof; and #1377 — a human `reject` decision can surface as state `Failed` with
`error: null`, so an absent `error` never implies an absent cause. Both are tracked bugs against
this contract, not accepted behavior.

### Exit codes

`RunExitCode` (`src/Aer.Cli/RunExitCodeResolver.cs`), returned by `run`, `dispatch`, and
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
instead."*). Concretely: a harness runs `aer dispatch` without `--wait`, the lane reaches a gate and
pauses — the process exits **1**. Reading that as "a step failed" and abandoning a healthy, paused
room is the single most consequential misreading this table can produce, because §5's entire gate
contract depends on that paused room still being there to `aer decide` against. **The rule: exit code
1 alone never tells you whether the room is done. Read `state` from `terminal.json` or `aer status
--json` to distinguish `Failed` from `Running`/`Paused`.** `--wait` makes `run`/`dispatch` block until
the room reaches Terminal (or the wait is itself cancelled); without it, a non-1/0 exit code is the
only signal a lane is even still going, and it is unreliable for that purpose by design.

### §3 schema — `terminal.json` / `aer status --json`

```
{
  "state": string,                     // WorkflowOutcome, e.g. "Succeeded" | "Failed" | ...
  "steps": [
    {
      "id": string,
      "state": string,                 // StepStatus token
      "execution"?: string,
      "linkedFrom"?: string,           // set when this step's latest execution is an `aer resume`
      "usage"?: ExecutionUsageView,
      "linkedFromUsage"?: ExecutionUsageView
    }
  ],
  "outputs": [string],                 // resolved output paths
  "error": string | null,
  "try": string | null                 // corrected-invocation text; only set on a pre-ledger refusal
}
```
where `ExecutionUsageView` is
```
{ "wallClockMs": number, "tokensIn"?: number, "tokensOut"?: number, "turns"?: number }
```
(`WorkflowStatusView.cs`, `src/Aer.Flow/Status/ExecutionUsageView.cs`). `wallClockMs` is
always present when the object is present at all — derived from recorded start/exit timestamps; the
token/turn fields are independently omitted (never `null`, never fabricated as zero) when the
vendor's captured stdout carried no such figure.

**Notation and a real divergence.** `usage`/`linkedFromUsage` are correctly optional-and-omitted —
write it `"field"?: Type`, not `Type | null` with a comment contradicting itself. But `linkedFrom`
is **not** uniformly optional: `WorkflowStatusView` emits it as JSON `null` when absent (no
`JsonIgnore` attribute, `WorkflowStatusView.cs`), while the `fleet_status` variant omits it
entirely (`JsonIgnoreCondition.WhenWritingNull`, `FleetStepStatusView`,
`src/Aer.Mcp.Host/FleetStatusTool.cs`), and the fleet variant additionally carries a
`timestamp` field the terminal-sentinel shape does not have. `terminal.json` and `status --json` are
one contract; `fleet_status` is a third, related shape with its own null-handling — see §6's schema.

---

## §4 Workers and vendor adapters

Vendor-specific behavior is isolated inside `Aer.Adapters`; `Aer.Flow` understands only a single
canonical message protocol. Adapters live behind `IWorkerAdapter`, resolved via
`WorkerAdapterRegistry.Default` (`src/Aer.Adapters/WorkerAdapterRegistry.cs`) — the registry is the
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
only by `aer decide`.** The harness answers it programmatically via `aer decide` (§2's `decide` row
for the verb shape). The decision vocabulary (`resume|reject|retry-with-revision|supersede`) is the
whole of it.

**The mid-lane permission-ask mechanism is deleted (#1417).** `PermissionGateTool.cs`
(the `aer_permission_ask` MCP tool, formerly `Aer.Mcp.Host` — wrote an `ask-<id>.json` file and blocked
up to 180s for an `answer-<id>.json` to appear, denying via a `revoked-<id>.json` on timeout) and
`PermissionReturnShape.cs` are gone, along with the daemon's `/api/rooms/permissions/answer` REST
answerer and its own
crash-reconciliation heal path (both previously in `src/Aer.Daemon/Program.cs`) — the two places that
ever wrote an `answer-<id>.json` file; `Aer.Cli` wrote none. Under this spec's harness-only surface,
that tool had no answerer left; keeping it would have meant a worker blocking on a rendezvous file no
code writes. **A lane is dispatched fully pre-cleared**: every capability a worker will need is
granted in `bindings.json` before `aer run`/`aer dispatch` is called (§9). There is no mid-lane ask.

**A worker that hits a capability it was not pre-cleared for is denied, fail-closed, by the
`PreToolUse`/`agy-hook-check` enforcement in §9** — the same mechanism that already exists for every
other denial, not a new one. The denial surfaces legibly: `FailureClassification.ToolDenied`
(`src/Aer.Flow/Domain/FailureClassification.cs`, one of the enum's four values — see §7 for the
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
(`src/Aer.Mcp.Host/FleetStatusTool.cs`) is a read-only MCP tool that scans rooms across the fleet: it
leverages the terminal-sentinel fast path for terminal rooms and projects active rooms from bound
snapshots plus `flow.jsonl` when no sentinel exists yet (`FleetStatusTool.cs`). It reads
`AerPaths.Rooms` plus any caller-supplied extra `roots` and does not itself depend on a running daemon
process — it opens files directly (`FleetStatusTool.cs`).

**Two-level drill-down, both levels of `fleet_status`'s MCP host, never a second application:** the
tool's per-room summary (level one, `fleet_status` itself) is what exists today. Level two — a room's
own `stdout` tail and `flow.jsonl` timeline, for debugging a specific lane — is now `room_detail`
(`src/Aer.Mcp.Host/RoomDetailTool.cs`, #1427): a sibling tool in the same MCP host, gated by its own
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
component anywhere under `src/Aer.Mcp*` or `src/Aer.Daemon` at HEAD — nothing broadcast-shaped
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
      "timestamp"?: string, "usage"?: ExecutionUsageView, "linkedFromUsage"?: ExecutionUsageView }
  ],
  "outputs"?: [string],
  "error"?: string,
  "try"?: string
}
```
(`FleetStatusTool.cs`). Optional fields are omitted, never emitted `null`
(`JsonIgnoreCondition.WhenWritingNull` throughout `FleetRoomStatusView`/`FleetStepStatusView`). This
is a **third shape**, related to but not identical with `terminal.json`/`status --json` — see §3's
note on `linkedFrom` and `timestamp` for the concrete divergence.

The scan itself is a **single-level** `Directory.GetDirectories` per root
(`FleetStatusTool.cs`) — it does not recurse, so project-grouped nesting is not found by the scan
alone. §8 depends on this fact directly, and closes it by unioning the scan with a registry rather
than by making the scan recurse.

### §6 schema — `room_detail`

Input:
```
{
  "room": string,                 // room name (resolved under AerPaths.Rooms + roots) or an absolute path
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
  them (`src/Aer.Flow/Status/ExecutionUsageView.cs`). It is engine-adjacent housekeeping, not a UI
  concern, and belongs in the narrowed daemon's kept surface alongside the room-watcher.
- **Fleet-wide concurrency caps** — `DaemonSettingsStore` (`src/Aer.Adapters/DaemonSettingsStore.cs`,
  reading/writing `AerPaths.SettingsFile`, i.e. `{Root}/settings.json`) plus `ConcurrencySlotGate.SetCaps`,
  applied at daemon startup (`Program.cs`). At HEAD this settings file holds only
  `GlobalConcurrencyCap`/`PerVendorConcurrencyCap` (`DaemonSettingsStore.cs`) — it is machine-wide,
  not per-room, so it belongs in the narrowed daemon too.

Explicitly **not** kept: pairing (`PairedClientsStore`), WebSocket broadcast (`/api/ws`,
`/api/ws/progress`), sidecar/Tailscale supervision, a desktop-owner-only auth tier, template-picker
endpoints, orchestrator reassignment, and the permission REST answerer (§5) — all of that existed to
serve `Aer.Ui`/`Aer.Mobile` and dies with them (Appendix).

### The quota ledger — what is new build, stated correctly

Polls vendor CLIs' print-mode `/usage`; accumulation from lane logs is attribution only, never the
reset-time source of truth. Quota data rides the push mailbox (§6). I could not find a `/usage`-polling
implementation, a runway projection, or push delivery for quota anywhere in `src/` at HEAD — that part
is genuinely **(new build)**.

What is **not** new build, and must not be re-derived: `FailureClassification`
(`src/Aer.Flow/Domain/FailureClassification.cs`) has **four** values —
`Retryable, Permanent, ExhaustedUntil, ToolDenied` — not two. `ExhaustedUntil` is load-bearing
throughout the scheduler, not a stub: it appears across `Aer.Flow/Scheduling/RetryEngine.cs`,
`Aer.Flow/Mutation/MutationInterface.cs`, `Aer.Flow/Outcomes/OutcomeClassifier.cs`,
`Aer.Flow/Status/WorkflowOutcome.cs`, and both adapters. Concretely, `AgyWorkerAdapter` already parses
a vendor-reported reset time into an `ExhaustedUntil` classification and a `retryNotBefore` instant
(`src/Aer.Adapters/AgyWorkerAdapter.cs`). So: the classification vocabulary, the retry/
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
*shrinks* `fleet_status`'s coverage — `fleet_status` derives coverage from `AerPaths.Rooms` plus
caller-supplied `roots` and nothing else at the scan layer (`FleetStatusTool.cs`); it does not depend
on any daemon surface, so deleting one cannot regress it. The real risk is narrower and still real:
the scan itself is **single-level** (`Directory.GetDirectories`, one call per root, §6) — it has no
notion of "every room across every project a harness might dispatch into," only "every room directly
under whichever roots I was told about." A harness that dispatches into a fresh project directory the
operator never passed as a `roots` entry was invisible to `fleet_status` until someone remembered to
add it. The registry closes *that* gap.

**The mechanism.** `RoomRegistryStore` (`src/Aer.Flow/Status/RoomRegistryStore.cs`, namespace
`Aer.Adapters` for the same reason `AerPaths` lives there — `fleet_status` reads it with no
`Aer.Adapters` project reference) reads and writes `AerPaths.RoomRegistryFile`
(`{AER_HOME}/room-registry.jsonl`), one JSON line per registration: room directory path, project root,
created-at.

- **Writer.** `RunCommand.ExecuteAsync` — the one pump both `aer run` and `aer dispatch` share —
  registers the room right after creating its directory, on every call through that pump (a fresh
  dispatch, or a repeated `aer run` against a room this pump already started), so a registration lost
  to a crash between directory creation and the write is repaired the next time this pump runs against
  the same room. `aer dispatch` passes its own resolved workspace (honouring `--workspace`) as the
  project root; a bare `aer run` has no separate workspace concept and uses the process cwd. This does
  *not* cover the separate `aer resume`/`decide`/`supply` mutation verbs — they only ever act against a
  room `aer run`/`dispatch` already created and never re-register it, so a room whose very first
  registration attempt failed and is thereafter driven only through one of those verbs stays
  unregistered until the next plain `aer run`/`dispatch` against it. The write is fire-and-forget with
  respect to the run itself — an `IOException`/`UnauthorizedAccessException`/`WaitHandleCannotBeOpenedException`
  is reported on stderr and swallowed, never surfaced as a run failure, because the registry only ever
  *adds* `fleet_status` coverage and must never gate a dispatch.
- **Format: append-only JSONL, not a rewritten JSON map, guarded by a named `Mutex`.** Every dispatch
  that creates a room is a separate, potentially concurrent `aer` process — that concurrency is the
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
- **Reader.** `FleetStatusTool` unions the registry's entries with its existing `AerPaths.Rooms` +
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
(`src/Aer.Cli/DispatchCommand.cs`) before `RunCommand` runs; `aer decide` requires `--bindings`
explicitly on every call (`DecideOptionsParser.cs`: *"pass --bindings <path-to-bindings.json>
naming the same bindings the paused room was dispatched with"*) — there is no separate global
last-used-file fallback the CLI path is ever subject to.

**The three-scope model survives: project ceiling ∩ room ∩ step, always narrowing, never widening.**
`bindings.json` is only the **room ∩ step** half of that intersection. The **project ceiling** — the
owner's own control on what any harness-authored `bindings.json` can grant in the first place — lives
in Baton's own app-level config, never inside the project tree, so a compromised or over-permissive
project cannot author its own way past it. `AerPaths.SettingsFile` (`{Root}/settings.json`,
`AerPaths.cs`) is the one app-level, per-machine config file this tree has today, and at HEAD
it holds only the daemon concurrency caps (`DaemonSettingsStore.cs`) — **no project-ceiling
implementation exists there or anywhere else in `src/` that I could find.** This is
`UNVERIFIED — fill from code`: the ceiling's register is settled direction, not a shipped contract,
and a build against this section should not assume `AerPaths.SettingsFile` is already shaped for it.

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
(`Program.cs`, `src/Aer.Cli/HookCheckCommand.cs`, `src/Aer.Cli/AgyHookCheckCommand.cs`).

**The hook is binary: allow / deny, nothing else.** The ask band that once made it ternary
(`AER_HOOK_ASK_TOOLS`, the `permissionDecision: "ask"` STDOUT envelope) was part of the mid-lane
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
(reads only the room's copy, per this section's own rule above). `aer resume` is bound by the same
rule as `decide`: the bindings passed continue the room's own standing permissions — the
composition never widens mid-room through any verb.

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
  other agent that can run CLI verbs and read `terminal.json`/`fleet_status`) driving `aer dispatch`,
  which keeps one set of hands on the workers. A direct phone-to-worker control path would be a
  second interaction surface outside the orchestrator, which the one-surface design retires.
  `Aer.Sidecar` — the Go tsnet component that existed solely to give a paired remote client
  zero-config Tailscale reach to the daemon's REST/WS API — is DELETED, done (#1420): it was a real,
  tracked Go module (an earlier draft claimed otherwise; a lane verified it existed — corrected), and
  it went with the pairing surface it served, along with `Aer.Daemon.csproj`'s optional copy step for
  its binary.
  **The harness seam is vendor-neutral, deliberately:** any agent that can run `aer` CLI verbs and
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
this repo ships and is verified on, not about `external/aer-core` (a separate repo, pinned by SHA,
out of this decision's scope) or about a vendor CLI's own OS support (`docs/vendor-doc-audit.md`,
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
| `Aer.Flow` | **KEEP** | Engine core; vendor/UI-agnostic; untouched by this reset except that `room.jsonl`'s machinery (§2, §5) is now dead code from the harness surface's perspective — kept in place, not exercised. |
| `Aer.Adapters` (incl. `BuiltInWorkflowTemplates`) | **KEEP** | The cross-vendor seam; the template catalog narrows to built-in only. |
| `Aer.Cli` | **KEEP**, verb set narrows | `run`/`dispatch`/`decide`/`cancel`/`supply`/`resume`/`status` stay; `templates` narrows to the built-in catalog. |
| `Aer.Mcp` / `Aer.Mcp.Host` | **KEEP**, grows | `fleet_status` is the anchor and gains the §6 drill-down levels; `YieldTool`, `MemoryProposalTool` stay, orthogonal to this reset. `PermissionGateTool` and `PermissionReturnShape` — the ask machinery — are **DELETED** (#1417, §5); confirmed `PermissionReturnShape` had no other consumer in the tree. |
| `Aer.Daemon` | **NARROWED — done (#1420)** | Every REST/WS route, pairing, WebSocket broadcast, sidecar supervision, template-picker endpoints, and orchestrator reassignment are deleted; the permission REST answerer (`/api/rooms/permissions/answer`) and its `DoorbellMonitor`/`PendingGateRegistry`/crash-reconciliation plumbing were already **DELETED** (#1417). `Aer.RoomSession` (the room-reading path `RoomClient`/`MainWindowViewModel` were replaced with, #1412) is deleted too, #1420 — no caller of it survived once every route was gone. What remains is a bare hosted-service runner: mutex, settings load, fleet-wide concurrency-cap apply (`DaemonSettingsStore`/`ConcurrencySlotGate`), and `RoomRetentionSweep`. The room-watcher (serving `fleet_status`/the registry, §8), the snapshot push loop (§6), and the quota-runway ledger (§7) are unbuilt new work for a later PR, not something this narrowing preserved — homes stated in §7. |
| `Aer.Ui` | **DELETED** (#1412 Part 2) | Not a description of the existing Avalonia app with features removed — a full archive, then deletion. Fleet Glass (§6) is the diagnostic surface, built as MCP-tool levels, never a UI app. |
| `Aer.Ui.Core` | **DELETED** (#1412 Part 2) | `RoomClient` and `MainWindowViewModel` were named explicitly here because `Aer.Daemon`'s PORT row above depended on both and the narrowing had to break that dependency, not carry it forward silently — resolved by extracting the salvageable read-model surface into `Aer.RoomSession` (#1412 Part 1) before deleting the rest. The bulk (`ChatViewModel`, `RoomsViewModel`, `RemoteViewModel`, `TemplateEditorViewModel`, `StandingPermissionsViewModel`) was UI-surface logic for the retired product and is gone with it. `RoomProjection.cs`, `RoomFilesProjector.cs`/`RoomFilesViewModels.cs`, and `ExecutionHistoryProjector.cs`'s equivalents lived on in `Aer.RoomSession` — itself deleted in full, #1420, once `RoomClient` and every daemon route were gone and nothing called them. |
| `Aer.Mobile` | **DELETED** (#1407) | No harness-driven use case; deleted along with its dedicated build machinery (CI job, pixi tasks, scripts) rather than left archived. |
| `Aer.Sidecar` | **DELETED** — done (#1420) | The tracked Go module and `Aer.Daemon.csproj`'s optional binary copy step both went. Remote dispatch is closed, orchestrator-only (§10); no resurrection case remains. (An earlier draft claimed the project was absent from the tree; corrected — it existed and was deleted deliberately.) |
| `Aer.Workers.Dialogue` | **DELETED** (#1408) | Vendor-neutral multi-model machinery that served the retired interactive/chat product; no harness-facing use case survives this reset. |
| `Aer.Flow.CrashTestHost`, `Aer.Architecture.Tests` | **KEEP** | The gate mechanisms stay untouched. |
| `Aer.Journeys.Tests`, `Aer.Plan.Tests` | **DELETED** (by this spec's own landing PR) | Both existed solely to cross-check `docs/plan.md` and `spec/journeys.md`, deleted with them; harness-facing journeys are future work that brings its own checks when it exists. |
| `docs/design/*` | **DELETE** | Per §11 — not archived, deleted. Its methodology (settle definition before screens) is worth reusing as a technique; its content does not survive and there is nowhere left for it to live. |

---

## Uncertain

Claims I could not verify by reading the tree, or that rest on something outside this session's
reach:

- **`AerPaths.SettingsFile` has no project-ceiling implementation at HEAD.** I read
  `DaemonSettingsStore.cs` in full — it holds only `GlobalConcurrencyCap`/`PerVendorConcurrencyCap`.
  The three-scope model's ceiling half (§9) is settled direction, not a shipped contract; a build
  against §9 should not assume any existing file is already shaped to hold it.
- **The exact shape of the outbound push mailbox (§6).** Unbuilt; I could not verify anything about
  its intended transport beyond "quota data rides it" and "gate-pending visibility rides it," both
  stated as rulings rather than measured facts.
- **The room registry's (§8) registration mechanism**, and whether it shares an implementation with
  the quota ledger (§7) or is fully separate. Both are named as parallel new-build items with no
  stated relationship; I treated them as independent.
- **Whether `Aer.Flow`/`Aer.Adapters` have silently accreted a human-watching assumption anywhere
  outside the paths this document cites directly** (terminal sentinel, status projection, hook
  enforcement, `FailureClassification`, `PermissionGrant`). I did not do a full pass of scheduling
  code; `Aer.Architecture.Tests` is the stated defense and I did not verify its actual coverage.
- **`YieldTool`/`MemoryProposalTool` in `Aer.Mcp.Host`.** I confirmed they exist and are distinct from
  the archived `PermissionGateTool`/`PermissionReturnShape`, but did not read their implementations —
  the Appendix's "orthogonal to this reset" call is a structural inference (they are not part of the
  ask machinery, the daemon, or the UI), not a read-through verification of their own content.

---

## Naming note (transitional)

The product converges on **Baton everywhere**: the CLI binary becomes `baton`, namespaces become `Baton.*`, state moves to `~/.baton`, and the surviving projects restructure to a one-binary, five-project tree (`src/Baton` engine, `src/Baton.Vendors`, `src/Baton.Cli` with `baton mcp` and `baton daemon` as verbs, `native/core`, two test projects). That rename lands as the FINAL PR of the reset series, after the code archives — every `Aer.*`/`aer` citation in this document refers to the current tree and is updated wholesale by that PR.

