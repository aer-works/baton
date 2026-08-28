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
  is enforced by `Aer.Architecture.Tests` (kept, per the Appendix).
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
`src/Aer.Flow/Status/AerPaths.cs:68`). One directory may contain several repositories; the room does
not know or care.

A room holds, at minimum: `room.json` (the room-kind marker — `AerPaths.RoomMetadataFileName`,
`AerPaths.cs:79`; absence reads as a workflow room), `bindings.json` (the standing worker grant —
`AerPaths.RoomBindingsFileName`, `AerPaths.cs:93`), `flow.jsonl` (the workflow event log —
§3), `artifacts/`, and, once terminal, `terminal.json` (§3). `snapshot.json` is present for any
room that has been dispatched at least once — `fleet_status` treats its absence as "no bound
snapshot" and reports it as an error entry rather than a state (`src/Aer.Mcp.Host/FleetStatusTool.cs:164-171`).

**There are two independent event logs, not one, and this spec states both honestly.**
`flow.jsonl` is the workflow ledger — steps, executions, decisions — and everything in §3–§9 below
reads and writes only this one. A **second** ledger, `room.jsonl`, exists in the same engine
(`src/Aer.Flow/Domain/RoomEvent.cs`, `src/Aer.Flow/Store/RoomEventLogReader.cs`,
`RoomEventLogWriter.cs`, `src/Aer.Flow/Projection/RoomProjector.cs`,
`src/Aer.Flow/Mutation/RoomMutationInterface.cs`) and its full event vocabulary is: held-work
dispatch/escalation/resolution, grant record/amend/revoke, ask-time escalation, turn-host dormancy
entered/cleared, mid-turn permission ask/answer/revoke, standing-permission revocation, the
workflow on/off switch, worker join/rename, and orchestrator (re)assignment
(`RoomEvent.cs:10-26`).

State it plainly: **every one of those event kinds is written only by code this document archives.**
The mid-turn permission ask/answer/revoke triad is the ARCHIVEd ask mechanism (§5). Held work,
escalation, dormancy, and orchestrator assignment are the resident-orchestrator/wake-loop model
`Aer.Daemon`'s `RoomTurnHost`/`RoomWakeBridge` implement, and that model has no referent left once
the harness — not a resident presence — is the decider (§7). Worker join/rename and the workflow
on/off switch belong to the interactive multi-participant chat room product `Aer.Ui`/`Aer.Mobile`
served. I checked: neither `src/Aer.Cli` nor `src/Aer.Mcp.Host` reference
`RoomMutationInterface`, `RoomEventLogReader`, or `RoomEventLogWriter` anywhere — the harness-facing
surface this spec describes has never touched `room.jsonl`, and `fleet_status` reads only the
terminal sentinel, `snapshot.json`, and `flow.jsonl`
(`FleetStatusTool.cs:164-201`) — never `room.jsonl`. Its type definitions stay in `Aer.Flow` because
Architecture Rule 1 keeps the journal engine-owned regardless of who reads it, and deleting dead
infrastructure is a separate cleanup this document does not scope — but a harness author should read
`room.jsonl` as **inert**: nothing in the dispatch/decide/status/fleet_status surface this spec
describes writes to it or reads from it.

A harness invokes work two ways, both in `src/Aer.Cli/Program.cs`:

- **`aer run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>]
  [--echo-worker] [--wait]`** — runs an authored `WorkflowDefinition` to a terminal state or a pause
  (`src/Aer.Cli/RunOptionsParser.cs:16`).
- **`aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>]
  [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>]`** — the one-shot
  form: `<name>` resolves to either a worker role (needs `--spec`) or a built-in template
  (`src/Aer.Cli/DispatchOptionsParser.cs:15`). Left unset, `--room-dir` derives a fresh, unique
  directory under `AerPaths.Rooms` per invocation — never a stable name derived from `<name>`, so a
  second `aer dispatch review` reruns rather than resuming the first's terminal snapshot. Bindings are
  written into the room directory by `DispatchCommand.ExecuteAsync`
  (`src/Aer.Cli/DispatchCommand.cs:108-111`, via `WorkerBindingConfigWriter.SaveToFileAsync`) before
  `RunCommand` is invoked underneath it.

A room's model is always pinned in `bindings.json` at dispatch time — there is no runtime model
choice a harness makes mid-lane; §9 covers the bindings contract. `aer resume`, `aer decide`, `aer
cancel`, and `aer supply` continue an already-dispatched room; §5 covers `decide` specifically.

### §2 schema — the CLI argument table

| Verb | Usage | Source |
|---|---|---|
| `run` | `aer run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--wait]` | `RunOptionsParser.cs:16` |
| `dispatch` | `aer dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>]` | `DispatchOptionsParser.cs:15` |
| `resume` | `aer resume <room-dir> --worker <role> (--message <text> \| --message-file <path>) --bindings <bindings-file> [--workflow-id <id>]` | `ResumeOptionsParser.cs:13` |
| `decide` | `aer decide <room-dir> --execution <execution-id> --type resume\|reject\|retry-with-revision\|supersede [--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]` | `DecideOptionsParser.cs:18` |
| `supply` | `aer supply <room-dir> --worker <role> --output <name> --file <source-path> --bindings <bindings-file> [--workflow-id <id>]` | `SupplyOptionsParser.cs:13` |
| `cancel` | `aer cancel <room-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]` | `Program.cs:66-67` |
| `status` | `aer status <room-dir> [--follow] [--json]` | `StatusOptionsParser.cs:11` |
| `templates` | `aer templates [--json]` | `Program.cs:76` |

`templates` narrows to the built-in catalog only (`Aer.Adapters`'s `BuiltInWorkflowTemplates`) —
there is no authoring UI to browse a saved-template library visually against (Appendix, R7 in the
old numbering — dropped here, since there is no longer a separate register to number rulings
against).

---

## §3 The lane protocol (completion contract)

`terminal.json` is written into a room directory the moment its workflow reaches a terminal state —
the completion signal a harness should watch instead of polling `aer status` prose or racing the
`aer run`/`aer dispatch` process's own exit
(`src/Aer.Flow/Status/TerminalSentinelWriter.cs:5-16`). It is written **last** — after every output an
outcome could reference already exists on disk — via a temp-file-then-atomic-move sequence, so a
file-watching harness never observes a partial write (`TerminalSentinelWriter.cs:37-48`). It is the
identical shape `aer status --json` prints (`WorkflowStatusView`), so a file-watcher and a polling
`status --json` caller read one contract for that pair specifically
(`src/Aer.Flow/Status/WorkflowStatusView.cs:32-47`) — `fleet_status` is a **third, related** shape;
see §6.

**Its absence does not always mean "not terminal yet."** Two exceptions, both real:

1. `TerminalSentinelWriter.WriteValidationRefusedAsync` — the pre-ledger refusal path — is only
   invoked when `RoomLedgerProbe.HasLedger` is false (`src/Aer.Cli/Program.cs:252`,
   `src/Aer.Cli/RoomLedgerProbe.cs:20-24`: a `flow.jsonl` that exists and is non-empty). A room that
   already has a real ledger — e.g. a paused room re-dispatched with a bad `--spec` — returns exit code
   2 (`ValidationRefused`) with **no sentinel written**, because the room's ledger (or a still-live
   pump) is its real terminal record and a fresh refusal must not overwrite it with a fabricated
   `Failed`/no-outputs sentinel. `aer resume`'s own refusal path (`Program.cs:265-268`) never writes
   a sentinel at all — a resume always targets an already-ledgered room.
2. `RoomHeld` (exit code 5, below) also writes no sentinel: the room may be perfectly healthy (a live
   pump, or a background sweep's brief lock), and writing `Failed` here would tell a file-watcher a
   running room just died while `aer status --json` reads the same room as `Running` at the same
   moment (`Program.cs:220-230`).

So: absence means "not terminal yet, **or** refused against an already-ledgered room, **or** another
Flow instance currently holds it" — never simply "never started." A harness that needs to
distinguish these reads `aer status`/`flow.jsonl` directly rather than inferring from the sentinel's
absence alone.

**The sentinel can also disappear.** `TerminalSentinelWriter.DeleteStaleSentinel`
(`TerminalSentinelWriter.cs:79-84`) removes a prior sentinel when a room is re-run, so that retrying a
room that previously failed pre-ledger does not leave the old `terminal.json` in place for the whole
duration of a new, genuinely in-progress attempt. A file-watching harness must expect `terminal.json`
to vanish and reappear across a re-dispatch of the same room directory, not treat its disappearance
as an error.

`aer status` is read-only, produces no `CommandResult`, and always exits 0 when it manages to print a
status at all (`Program.cs:105-114`) — it cannot complete a room or substitute for watching the
sentinel.

### Exit codes

`RunExitCode` (`src/Aer.Cli/RunExitCodeResolver.cs:14-32`), returned by `run`, `dispatch`, and
`resume` only — `cancel`/`decide`/`supply` keep the unchanged binary success/failure code
(`Program.cs:214-218`):

| Code | Name | Meaning |
|---|---|---|
| 0 | `Succeeded` | Every step succeeded |
| 1 | `Failed` | **Not** exclusively terminal-and-failed — see below |
| 2 | `ValidationRefused` | Provisioning/validation refused, independent of ledger state; the **sentinel write** (not the exit code) is what is conditional on `RoomLedgerProbe.HasLedger` (above) |
| 3 | `Timeout` | At least one step's failure is a timeout and none is a hard failure (`RunExitCodeResolver.ResolveFailed`, `:66-74`) |
| 4 | `Cancelled` | — |
| 5 | `RoomHeld` | Another Flow instance already holds this room — retry later, not a terminal outcome; no sentinel is written (`Program.cs:220-230`) |

**Exit code 1 is not "terminal, a step failed."** `RunExitCodeResolver.Resolve` falls through to
`Failed` for **`Running` and `Paused` too** — any outcome that is not `Succeeded`, `Cancelled`, or the
resolved `Failed`/`Timeout` split (`RunExitCodeResolver.cs:57-63`, comment verbatim: *"Running or
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
(`WorkflowStatusView.cs:12-53`, `src/Aer.Flow/Status/ExecutionUsageView.cs:11-26`). `wallClockMs` is
always present when the object is present at all — derived from recorded start/exit timestamps; the
token/turn fields are independently omitted (never `null`, never fabricated as zero) when the
vendor's captured stdout carried no such figure.

**Notation and a real divergence.** `usage`/`linkedFromUsage` are correctly optional-and-omitted —
write it `"field"?: Type`, not `Type | null` with a comment contradicting itself. But `linkedFrom`
is **not** uniformly optional: `WorkflowStatusView` emits it as JSON `null` when absent (no
`JsonIgnore` attribute, `WorkflowStatusView.cs:19`), while the `fleet_status` variant omits it
entirely (`JsonIgnoreCondition.WhenWritingNull`, `FleetStepStatusView`,
`src/Aer.Mcp.Host/FleetStatusTool.cs:274-276`), and the fleet variant additionally carries a
`timestamp` field the terminal-sentinel shape does not have. `terminal.json` and `status --json` are
one contract; `fleet_status` is a third, related shape with its own null-handling — see §6's schema.

---

## §4 Workers and vendor adapters

Vendor-specific behavior is isolated inside `Aer.Adapters`; `Aer.Flow` understands only a single
canonical message protocol. Two adapters exist today — `ClaudeWorkerAdapter`, `AgyWorkerAdapter` —
behind `IWorkerAdapter`, resolved via `WorkerAdapterRegistry.Default`
(`src/Aer.Adapters/WorkerAdapterRegistry.cs:18-19`, `Program.cs:128`). Baton never reads, copies,
forwards, or stores a vendor credential; it spawns the vendor's own already-authenticated CLI. The
`PreToolUse`/`agy-hook-check` enforcement below (§9) runs as a fast, dependency-free stdin round trip,
spawned directly by the vendor CLI on every tool call — deliberately outside the workflow-execution
pipeline, because `PreToolUse` blocks the model's own turn until it returns
(`Program.cs:15-20,40-44`).

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

**The mid-lane permission-ask mechanism is archived.** `src/Aer.Mcp.Host/PermissionGateTool.cs` (the
`aer_permission_ask` MCP tool — writes an `ask-<id>.json` file and blocks up to 180s for an
`answer-<id>.json` to appear, denying via a `revoked-<id>.json` on timeout,
`PermissionGateTool.cs:9-25,101-160`) and `PermissionReturnShape.cs` are ARCHIVE. I confirmed the
`answer-<id>.json` filename is written from exactly two places in the whole tree: the daemon's REST
answerer and its own crash-reconciliation heal path (both `src/Aer.Daemon/Program.cs`) — `Aer.Cli`
writes none. Under this spec's harness-only surface, that tool has no answerer left; keeping it would
mean a worker blocking on a rendezvous file no code writes. **A lane is dispatched fully pre-cleared**:
every capability a worker will need is granted in `bindings.json` before `aer run`/`aer dispatch` is
called (§9). There is no mid-lane ask.

**A worker that hits a capability it was not pre-cleared for is denied, fail-closed, by the
`PreToolUse`/`agy-hook-check` enforcement in §9** — the same mechanism that already exists for every
other denial, not a new one. The denial surfaces legibly: `FailureClassification.ToolDenied`
(`src/Aer.Flow/Domain/FailureClassification.cs:14`, one of the enum's four values — see §7 for the
other three) is the vocabulary a harness reads off the failed step in `terminal.json`. A harness that
sees `ToolDenied` re-dispatches — with a widened grant in a fresh `bindings.json`, or a narrowed task
that does not need the capability. That is the whole of the recovery path; there is no live channel to
answer the denial in place.

**The second ledger, honestly.** §2 already states this in full: `room.jsonl` carried the
mid-turn ask/answer/revoke triad this section retires, plus held-work/escalation/dormancy/orchestrator
machinery §7 retires for an unrelated reason (no resident orchestrator). `fleet_status` never reads
`room.jsonl` (`FleetStatusTool.cs:164-201`) — it only ever read `flow.jsonl`, the terminal sentinel,
and `snapshot.json`. So a room paused on a `PausePoint` shows up correctly in Fleet Glass (§6); a
room that — under the *prior* draft's design — was waiting on a mid-lane permission ask would not
have. That gap is now moot rather than fixed, because the mechanism it was a gap in no longer exists.

---

## §6 Fleet Glass — observability

This is the entire user-facing surface, unconditionally. `fleet_status`
(`src/Aer.Mcp.Host/FleetStatusTool.cs`) is a read-only MCP tool that scans rooms across the fleet: it
leverages the terminal-sentinel fast path for terminal rooms and projects active rooms from bound
snapshots plus `flow.jsonl` when no sentinel exists yet (`FleetStatusTool.cs:129-161`). It reads
`AerPaths.Rooms` plus any caller-supplied extra `roots` and does not itself depend on a running daemon
process — it opens files directly (`FleetStatusTool.cs:78-91`).

**Two-level drill-down, both **(new build)** levels of `fleet_status` itself, never a second
application:** the tool's per-room summary (level one) is what exists today; a room's own `stdout`
tail and `flow.jsonl` timeline (level two, for debugging a specific lane) does not exist at HEAD — the
tool currently reports only the terminal sentinel or a `state`+`error` projection
(`FleetStatusTool.cs:129-240`), never live stdout. This settles the prior draft's open question:
there is no separate diagnostic UI, dev or otherwise. Fleet Glass **is** the diagnostic story, and its
second level is scoped work against the same MCP tool, not a new surface.

The outbound push mailbox — the mechanism that would notify a harness of a state-change event without
polling — is **(new build)**. I did not find `push`, `mailbox`, or an outbound-webhook-shaped
component anywhere under `src/Aer.Mcp*` or `src/Aer.Daemon` at HEAD. `src/Aer.Daemon` does contain an
inbound-facing notification pump with a backup poll (`DoorbellMonitor.cs:51-146`) and a client
fan-out (`DaemonBroadcast`) — neither is an *outbound, harness-facing* mailbox, so the "unbuilt"
ruling survives, but state it precisely: the search was scoped to outbound/webhook shape, not "no
notification code of any kind." Quota data (§7) and gate-pending visibility both ride this mailbox
once it exists; its transport (webhook, log-append, something else) is unspecified here — that is
design work for the build.

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
(`FleetStatusTool.cs:32-48,246-286`). Optional fields are omitted, never emitted `null`
(`JsonIgnoreCondition.WhenWritingNull` throughout `FleetRoomStatusView`/`FleetStepStatusView`). This
is a **third shape**, related to but not identical with `terminal.json`/`status --json` — see §3's
note on `linkedFrom` and `timestamp` for the concrete divergence.

The scan itself is a **single-level** `Directory.GetDirectories` per root
(`FleetStatusTool.cs:100`) — it does not recurse, so project-grouped nesting is not found today. §8
depends on this fact directly.

---

## §7 The daemon, narrowed

**The harness is the orchestrator.** There is no resident conversational presence a room maintains
between harness invocations. `RoomTurnHost`/`RoomWakeBridge`
(`src/Aer.Daemon/RoomTurnHost.cs`, `src/Aer.Daemon/RoomWakeBridge.cs`, registered at
`src/Aer.Daemon/Program.cs:178-184`) and the daemon's reassignment/pairing/broadcast REST surface are
archived along with the daemon narrowing below.

What the daemon narrows **to**: a **room-watcher serving `fleet_status`/the registry** (§8 — though
`fleet_status` itself needs no daemon, per §6), the **snapshot push loop** feeding the mailbox (§6),
and the **quota-runway ledger** (below). Two more live responsibilities need a stated home rather
than silently dropping out when the rest of `Aer.Daemon` is archived:

- **`RoomRetentionSweep`** (`Program.cs:187`, a hosted service) — it prunes execution directories, and
  `ExecutionUsageProjector` has an explicit pruned-path fallback specifically because the sweep moves
  them (`src/Aer.Flow/Status/ExecutionUsageView.cs`). It is engine-adjacent housekeeping, not a UI
  concern, and belongs in the narrowed daemon's kept surface alongside the room-watcher.
- **Fleet-wide concurrency caps** — `DaemonSettingsStore` (`src/Aer.Adapters/DaemonSettingsStore.cs`,
  reading/writing `AerPaths.SettingsFile`, i.e. `{Root}/settings.json`) plus `ConcurrencySlotGate.SetCaps`,
  applied at daemon startup (`Program.cs:65-66`). At HEAD this settings file holds only
  `GlobalConcurrencyCap`/`PerVendorConcurrencyCap` (`DaemonSettingsStore.cs:8-15`) — it is machine-wide,
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
(`src/Aer.Flow/Domain/FailureClassification.cs:9-15`) has **four** values —
`Retryable, Permanent, ExhaustedUntil, ToolDenied` — not two. `ExhaustedUntil` is load-bearing
throughout the scheduler, not a stub: it appears across `Aer.Flow/Scheduling/RetryEngine.cs`,
`Aer.Flow/Mutation/MutationInterface.cs`, `Aer.Flow/Outcomes/OutcomeClassifier.cs`,
`Aer.Flow/Status/WorkflowOutcome.cs`, and both adapters. Concretely, `AgyWorkerAdapter` already parses
a vendor-reported reset time into an `ExhaustedUntil` classification and a `retryNotBefore` instant
(`src/Aer.Adapters/AgyWorkerAdapter.cs:1401-1403`). So: the classification vocabulary, the retry/
dependency handling built on top of it, and at least one adapter's refusal-message parse into
`ExhaustedUntil` all exist today. What is missing is specifically the proactive `/usage` poll, the
runway projection, and the push delivery — build against that gap, not against a two-value enum that
does not exist.

**Both vendors' `/usage` support.** Per this spec's own owner ruling, both `agy -p "/usage"` and
`claude -p "/usage"` answer structured usage data without a model turn, verified live on 2026-08-28.
I could not independently verify this myself — it rests on a live CLI run this session did not
perform — so it is stated here as the settled basis the quota ledger is built against, flagged
`UNVERIFIED — fill from code` for the `agy` half specifically: nothing in `src/` at HEAD implements or
tests an `agy` `/usage` poll, so there is no code path to check it against yet. Both vendors
participate in the ledger.

---

## §8 Multi-project room registry

**(new build).** No registry implementation exists at HEAD; this section states the invariant a build
must satisfy, not a shipped contract. Name the invariant: **`fleet_status` coverage never shrinks
when daemon surfaces are deleted** — a room that `fleet_status` could find before a given daemon
endpoint was removed must still be findable after. This needs a regression test, not just a design
note.

**The true reason this is a prerequisite, stated correctly:** it is not that deleting daemon surfaces
*shrinks* `fleet_status`'s coverage — I checked, and `fleet_status` derives coverage from
`AerPaths.Rooms` plus caller-supplied `roots` and nothing else (`FleetStatusTool.cs:78-91`); it does
not depend on any daemon surface today, so deleting one cannot regress it. The real risk is narrower
and still real: `fleet_status`'s scan is **single-level**
(`Directory.GetDirectories`, one call per root, §6) — it has no notion of "every room across every
project a harness might dispatch into," only "every room directly under whichever roots I was told
about." A harness that dispatches into a fresh project directory the operator never passed as a
`roots` entry is invisible to `fleet_status` until someone remembers to add it. The registry closes
*that* gap — project-grouped discovery and cross-root coverage a caller does not have to enumerate by
hand — not a regression from deleted daemon code.

The exact registration mechanism (how `aer dispatch` announces a room's existence and project grouping
to the registry) is unspecified here — that is design work for the build.

---

## §9 Bindings and permissions

**`bindings.json` is the room's standing permission for the room ∩ step scopes.** For a harness, "answer
once" means: the bindings file is the pre-answered ladder, written once at dispatch/run time and
consulted on every subsequent decision against that room. **Re-prompting a headless lane for a
permission it already carries in its bindings is a spec violation**, not a defensible conservative
default. `DispatchCommand.ExecuteAsync` writes bindings into the room directory
(`src/Aer.Cli/DispatchCommand.cs:108-111`) before `RunCommand` runs; `aer decide` requires `--bindings`
explicitly on every call (`DecideOptionsParser.cs:91-95`: *"pass --bindings <path-to-bindings.json>
naming the same bindings the paused room was dispatched with"*) — there is no separate global
last-used-file fallback the CLI path is ever subject to.

**The three-scope model survives: project ceiling ∩ room ∩ step, always narrowing, never widening.**
`bindings.json` is only the **room ∩ step** half of that intersection. The **project ceiling** — the
owner's own control on what any harness-authored `bindings.json` can grant in the first place — lives
in AER's own app-level config, never inside the project tree, so a compromised or over-permissive
project cannot author its own way past it. `AerPaths.SettingsFile` (`{Root}/settings.json`,
`AerPaths.cs:119-122`) is the one app-level, per-machine config file this tree has today, and at HEAD
it holds only the daemon concurrency caps (`DaemonSettingsStore.cs:8-15`) — **no project-ceiling
implementation exists there or anywhere else in `src/` that I could find.** This is
`UNVERIFIED — fill from code`: the ceiling's register is settled direction, not a shipped contract,
and a build against this section should not assume `AerPaths.SettingsFile` is already shaped for it.

**Grants fail closed: if a denial cannot be enforced for the chosen vendor, the run does not start.**
This is a rule about what the ceiling ∩ bindings composition must guarantee before dispatch, not
merely at runtime — stated here as this spec's own rule, since a harness author needs it to reason
about what "dispatch succeeded" implies about enforceability.

**The `PreToolUse`/`agy-hook-check` hook stays the enforcement mechanism** — the only enforcement
point over the toolset a worker actually has, since `--allowedTools` pre-approves rather than
restricting (measured directly: `PermissionGrant.cs:69-73`, citing the
`gate.allowedtools-is-preapproval-not-ceiling` sentinel check in `tools/vendor-verify/verify.py`).
Baton ships one on every
spawned worker, on both vendors, via `hook-check`/`agy-hook-check`
(`Program.cs:15-59`, `src/Aer.Cli/HookCheckCommand.cs`, `src/Aer.Cli/AgyHookCheckCommand.cs`).

**The hook is ternary, not binary, and only when Baton says so.** With
`HookCheckCommand.AskToolsEnvironmentVariable` (`AER_HOOK_ASK_TOOLS`) present, the claude-side hook
decides allow / **ask** / deny rather than allow / deny: a category the operator never granted routes
to a human via a `permissionDecision: "ask"` STDOUT envelope, rather than hard-failing
(`HookCheckCommand.cs:32-38,94-95,293-306`). A tool in both the denied list and the ask list is
**denied** — a standing "never" is not reopened by an ask band being present. **With the variable
absent — every one-shot harness dispatch, per this section's own "fully pre-cleared" rule — none of
that exists, and an ungranted capability exits 2 exactly as if the ask band did not exist**
(`HookCheckCommand.cs:67-72`). The measured headless outcome for the ask band, when it *is* enabled:
under `-p` the forced prompt fails closed and the operation does not happen
(`HookCheckCommand.cs:88-92`). A denial surfaces as `FailureClassification.ToolDenied` (§5, §7) —
that is the vocabulary a harness reads, whichever band produced it.

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
each worker role's adapter and permission grant, resolvable at both dispatch time (writes the room's
copy) and decide time (reads only the room's copy, per this section's own rule above).

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
  zero-config Tailscale reach to the daemon's REST/WS API — is ARCHIVE: `src/Aer.Sidecar/` is a real, tracked Go module (an earlier draft claimed otherwise; a lane verified it exists — corrected), and it goes with the pairing surface it served, along with `Aer.Daemon.csproj`'s optional copy step for its binary.
  **The harness seam is vendor-neutral, deliberately:** any agent that can run `aer` CLI verbs and
  read `terminal.json`/`fleet_status` can be the orchestrator. Claude Code is the current occupant of
  that seam, not a requirement of it.

---

## §11 Register

This document and the code it cites are the **only** registers. `docs/decisions/*`, `docs/design/*`,
and the prior `spec/*` files are being deleted, not archived — there is nothing left to supersede or
cross-reference, and a future reader will not find them. Every rule this document states was
previously justified by a decision record; that justification is now stated inline, in the section
the rule belongs to, and the supersession apparatus (numbered decisions, "supersedes 0049"-style
prose) is dropped entirely.

New decision records are created **fresh**, only when a genuinely new decision is made after this
document ships — never retroactively, and never to re-derive something this document already states
as settled. If a future change needs to record its own reasoning, it gets its own record; it does not
reach backward to reconstruct a numbering scheme that no longer exists.

---

## Appendix: full subsystem ruling table

| Project / verb | Ruling | Note |
|---|---|---|
| `Aer.Flow` | **KEEP** | Engine core; vendor/UI-agnostic; untouched by this reset except that `room.jsonl`'s machinery (§2, §5) is now dead code from the harness surface's perspective — kept in place, not exercised. |
| `Aer.Adapters` (incl. `BuiltInWorkflowTemplates`) | **KEEP** | The cross-vendor seam; the template catalog narrows to built-in only. |
| `Aer.Cli` | **KEEP**, verb set narrows | `run`/`dispatch`/`decide`/`cancel`/`supply`/`resume`/`status` stay; `templates` narrows to the built-in catalog. |
| `Aer.Mcp` / `Aer.Mcp.Host` | **KEEP**, grows | `fleet_status` is the anchor and gains the §6 drill-down levels; `YieldTool`, `MemoryProposalTool` stay, orthogonal to this reset. `PermissionGateTool` and `PermissionReturnShape` — the ask machinery — are **ARCHIVE** (§5); I confirmed `PermissionReturnShape` has no other consumer in the tree. |
| `Aer.Daemon` | **PORT, drastically** | Narrows to the room-watcher (serving `fleet_status`/the registry, §8), the snapshot push loop (§6, new build), the quota-runway ledger (§7, partly new build), `RoomRetentionSweep`, and the fleet-wide concurrency caps (`DaemonSettingsStore`/`ConcurrencySlotGate`) — homes stated in §7. Pairing, WebSocket broadcast, sidecar supervision, template-picker endpoints, orchestrator reassignment, and the permission REST answerer are ARCHIVE. **Breaking dependency the narrowing must resolve:** `Aer.Daemon.csproj:12` holds a hard `ProjectReference` to `Aer.Ui.Core.csproj`, and it is not incidental — `Program.cs` uses `MainWindowViewModel` (`:172`) and constructs `RoomClient` (`:199-231`), which the WebSocket endpoint, `/api/version`, the reconcile loop, and essentially every kept `/api/rooms/*`/`/api/sessions/*` handler in the file consume as a DI parameter. A daemon that narrows to room-watcher + push loop + quota ledger needs its own room-reading path that does **not** go through `RoomClient`/`MainWindowViewModel` — both of which are `Aer.Ui.Core` types this table ARCHIVEs below. This is new engineering work the narrowing creates, not a rename. |
| `Aer.Ui` | **DELETED** (#1412 Part 2) | Not a description of the existing Avalonia app with features removed — a full archive, then deletion. Fleet Glass (§6) is the diagnostic surface, built as MCP-tool levels, never a UI app. |
| `Aer.Ui.Core` | **DELETED** (#1412 Part 2) | `RoomClient` and `MainWindowViewModel` were named explicitly here because `Aer.Daemon`'s PORT row above depended on both and the narrowing had to break that dependency, not carry it forward silently — resolved by extracting the salvageable read-model surface into `Aer.RoomSession` (#1412 Part 1) before deleting the rest. The bulk (`ChatViewModel`, `RoomsViewModel`, `RemoteViewModel`, `TemplateEditorViewModel`, `StandingPermissionsViewModel`) was UI-surface logic for the retired product and is gone with it. `RoomProjection.cs`, `RoomFilesProjector.cs`/`RoomFilesViewModels.cs`, and `ExecutionHistoryProjector.cs`'s equivalents now live in `Aer.RoomSession`, confirmed Avalonia-free (that project's `.csproj` references only `Aer.Flow`/`Aer.Cli`/`Aer.Adapters`) — the "Uncertain" section's salvage-candidate entry below is resolved. |
| `Aer.Mobile` | **DELETED** (#1407) | No harness-driven use case; deleted along with its dedicated build machinery (CI job, pixi tasks, scripts) rather than left archived. |
| `Aer.Sidecar` | **ARCHIVE** | `src/Aer.Sidecar/` (a tracked Go module) and `Aer.Daemon.csproj`'s optional binary copy step both go. Remote dispatch is closed, orchestrator-only (§10); no resurrection case remains. (An earlier draft claimed the project was absent from the tree; corrected — it exists and is archived deliberately.) |
| `Aer.Workers.Dialogue` | **ARCHIVE** | Vendor-neutral multi-model machinery that served the retired interactive/chat product; no harness-facing use case survives this reset. |
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
- **`agy`'s live `/usage` behavior (§7).** The owner ruling states both vendors now answer structured
  `/usage` data live as of 2026-08-28. I did not run either CLI this session and could not verify it
  independently. I did confirm there is no `/usage`-polling code path in `src/` yet for either vendor,
  so there is nothing in the tree the claim could conflict with — it is simply unverified, not
  contradicted.
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

