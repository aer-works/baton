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
  [--echo-worker] [--wait] [--wait-timeout <minutes>]`** — runs an authored `WorkflowDefinition` to a
  terminal state or a pause (`src/Baton.Cli/RunOptionsParser.cs`). `--wait-timeout` (#1378) bounds how
  long `--wait`'s poll loop sits on an undecided pause: ignored without `--wait`, and once it elapses
  the call stops waiting and reports exit code 3 (`Timeout`, below) rather than blocking forever on a
  workflow nobody has decided.
- **`baton dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>]
  [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>]
  [--token-budget <n>] [--label <text>] [--workstream <slug>]`**
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
  non-interactive CLI) and merely flagged on stderr above 2h. `--label` (#1499) is display text only —
  a short human-readable name (e.g. "the #1496 env-snapshot lane") so Fleet Glass (§6) can show
  something legible instead of a bare `dispatch-<role>-<hex8>` directory name; it is never part of the
  room directory's own name, which stays the generated hex identity above. Sanitized at parse time
  (`DispatchOptionsParser.SanitizeLabel`): trimmed, embedded newlines folded to spaces, capped at
  `DispatchOptionsParser.MaxLabelLength` chars; a blank result is treated as omitted rather than refused.
  Persisted onto every entry of that
  room's own `bindings.json` (`WorkerBindingConfigEntry.Label`) rather than a new file, since bindings
  already exists for every room regardless of terminal state — see §6 schema for how `fleet_status`
  reads it back. `--token-budget` (#1623) overrides the dispatched role's own default per-execution
  token ceiling — §3's "Engine-run verify and the token budget" subsection is the full contract; this
  entry only names the flag. `--workstream` (#1619, rung 1 of #1614's ruling) is a **grouping key, not a title** —
  a room keeps its generated hex identity on disk; the slug only makes several rooms (e.g. an
  implement lane and its review redispatch) read as one workstream in Fleet Glass. Do not conflate it
  with `--label`: a label is 60-char free display text never written into a path
  (`DispatchOptionsParser.SanitizeLabel`); a workstream slug IS later used as a Windows directory name
  (below), so it is validated rather than truncated —
  `DispatchOptionsParser.SanitizeWorkstream` trims it, then refuses (never truncates) anything
  over `MaxWorkstreamLength` (60) chars or outside the grammar `^[A-Za-z0-9][A-Za-z0-9._-]*$` — a
  blank result after trimming is treated as omitted, the same as `--label`. A value that passes the
  grammar check is then folded to lowercase, per the #1614 design record's own slug wording
  ("path-safe, lowercase, short"): NTFS resolves `BatonPaths.ByWorkstream` directory names
  case-insensitively while Fleet Glass's grouping (below) keys on the exact string in a
  case-sensitive JS `Map`, so `--workstream W1619` and `--workstream w1619` fold to the same slug
  rather than sharing one junction directory while rendering as two glass groups. Persisted the same
  way as `--label`, onto every entry of the room's own `bindings.json`
  (`WorkerBindingConfigEntry.Workstream`) — see §6 schema for how `fleet_status` reads it back, and
  the paragraph immediately below for the navigation half.

  **The by-workstream junction directory.** When `--workstream` is passed, `DispatchCommand` also
  creates a Windows directory junction (`mklink /J` via `WorkstreamJunctionLinker`, no elevation
  required) at `BatonPaths.ByWorkstream/<slug>/<room-name>-<hash>` pointing at the room's real
  directory under `BatonPaths.Rooms` — so `cd ~/.baton/by-workstream/<slug>` lists every room in
  that workstream without moving a single file on disk. The `<hash>` suffix (`WorkstreamJunctionLinker.ResolveLinkPath`,
  eight hex characters of the room's own full path) exists because `<room-name>` alone is not unique:
  an explicit `--room-dir` — the pattern every invoking harness uses — is passed through verbatim
  rather than minted fresh, so two rooms with different parents can share a leaf. `BatonPaths.ByWorkstream`
  is **deliberately a sibling of `BatonPaths.Rooms`, never a child**: `FleetStatusTool`, `RoomRetentionSweep`,
  and the fleet-glass pusher (`pusher.py`) all walk `rooms/` exactly one level deep, and a workstream
  directory nested under it would be picked up by every one of those scans and reported as a phantom
  room with no bound snapshot — the same reason `fleet_status`'s caller-supplied `roots` refuses to
  walk `BatonPaths.ByWorkstream` itself (it would double-count a room already found by its real path).
  A failed junction (a machine policy refusing `mklink`, an occupied name that resolves to a
  different room) degrades to a stderr warning naming the existing target — it never fails the
  dispatch, since the room itself is already fully functional without the shortcut.

A room's model is always pinned in `bindings.json` at dispatch time — there is no runtime model
choice a harness makes mid-lane; §9 covers the bindings contract. `baton resume`, `baton decide`, `baton
cancel`, and `baton supply` continue an already-dispatched room; §5 covers `decide` specifically.
`baton resolve` (#1608, §3 below) also targets an already-dispatched room, but never drives it
forward — it settles one execution's `Indeterminate` verdict and stops.

**`baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] [--effort
<name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--label
<text>] [--workstream <slug>]`** (#1441) reruns
a single-role `baton dispatch` room into a fresh one, once the operator finds the brief was wrong or
incomplete — without hand-retyping the adapter/model/effort/workspace/timeout flags a from-scratch
`baton dispatch` would otherwise force. `<room-dir>` names the parent room; like `baton dispatch`, the
new room's own directory is always freshly generated (`RedispatchOptionsParser.cs`) — a redispatch is
never a resume, same rule as §2's dispatch entry above. Every flag inherits the parent room's recorded
`bindings.json` entry as its default — adapter, model, effort, workspace, timeout, token budget (#1623),
and (#1499) label —
and is overridden by whichever flag the operator actually passes (`RedispatchCommand.InheritBinding`);
`--output` is the one exception, never inherited, because a prior `--output`'s destination copy path is
not persisted anywhere in the room (only the produced output's customized *name* is, on the bindings
entry's contract) — a redispatch's own `--output`, when given, works exactly like dispatch's own.
`--label` inherits unlike `--output` does: the parent's label IS a persisted, durable room-level fact
(`WorkerBindingConfigEntry.Label`), not a process-local copy target, so a redispatched lane keeps
reading as the same human-named thing — absent inherits the parent's label, specified-and-blank
(`--label ""`) clears it, and specified-and-nonblank overrides it (`RedispatchCommand.InheritBinding`).
`--workstream` (#1619) inherits the identical way, via its own `WorkstreamSpecified` mirror of
`LabelSpecified` (`RedispatchOptionsParser.cs`, `RedispatchOptions.WorkstreamSpecified`) — absent
inherits the parent's workstream, specified-and-blank clears it, specified-and-nonblank overrides it
— so a redispatch chain keeps grouping as one workstream in Fleet Glass without the operator
re-passing the slug on every hop, and can still deliberately break a lane out of its workstream by
passing `--workstream ""`. `RedispatchCommand` also (re-)creates that redispatched room's
by-workstream junction against whichever slug `InheritBinding` just resolved — inherited, cleared, or
overridden — never the raw `--workstream` flag alone, since a bare `baton redispatch` with no
`--workstream` flag at all must still link into the parent's workstream directory. `--spec`
omitted reuses the parent's already-built prompt verbatim; given, the amended brief is rebuilt through
the same `RoleDispatch.Materialize` a fresh dispatch uses, with the parent's recorded axes as defaults
— including the inherited-unless-overridden label, applied after that rebuild since
`RoleDispatch.Materialize` itself knows nothing of it (`RedispatchCommand.ExecuteAsync`).
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
| `run` | `baton run <workflow-file> --bindings <bindings-file> [--room-dir <dir>] [--workflow-id <id>] [--echo-worker] [--wait] [--wait-timeout <minutes>]` | `RunOptionsParser.cs` |
| `dispatch` | `baton dispatch <name> [--spec <spec-file>] [--adapter <name>] [--model <name>] [--effort <name>] [--room-dir <dir>] [--workspace <dir>] [--workflow-id <id>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--label <text>] [--workstream <slug>]` | `DispatchOptionsParser.cs` |
| `redispatch` | `baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <name>] [--model <name>] [--effort <name>] [--workspace <dir>] [--output <path>] [--timeout <minutes>] [--token-budget <n>] [--label <text>] [--workstream <slug>]` | `RedispatchOptionsParser.cs` |
| `resume` | `baton resume <room-dir> --worker <role> (--message <text> \| --message-file <path>) --bindings <bindings-file> [--workflow-id <id>]` | `ResumeOptionsParser.cs` |
| `decide` | `baton decide <room-dir> --execution <execution-id> --type resume\|reject\|retry-with-revision\|supersede [--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]` | `DecideOptionsParser.cs` |
| `resolve` | `baton resolve <room-dir> [--execution <execution-id>] --accept-capture \| --reject --reason <text>` | `ResolveOptionsParser.cs` |
| `supply` | `baton supply <room-dir> --worker <role> --output <name> --file <source-path> --bindings <bindings-file> [--workflow-id <id>]` | `SupplyOptionsParser.cs` |
| `cancel` | `baton cancel <room-dir> [--execution <execution-id>] [--bindings <bindings-file>] [--workflow-id <id>]` | `Program.cs` |
| `status` | `baton status <room-dir> [--follow] [--json]` | `StatusOptionsParser.cs` |
| `templates` | `baton templates [--json]` | `Program.cs` |
| `keep` | `baton keep <room-dir>` | `KeepOptionsParser.cs` |
| `unkeep` | `baton unkeep <room-dir>` | `UnkeepOptionsParser.cs` |

`templates` narrows to the built-in catalog only (`Baton.Vendors`'s `BuiltInWorkflowTemplates`) —
there is no authoring UI to browse a saved-template library visually against (Appendix, R7 in the
old numbering — dropped here, since there is no longer a separate register to number rulings
against).

**`cancel`'s `--execution` is now optional** (#1495): omitted, it targets "the target lane" —
exactly one candidate's latest execution, refused (naming every candidate) on zero or more than one
(`RunningExecutionResolver.cs`). A candidate is a currently-`Running` step, or (#1607) a quota-parked
one — `Failed` with a scheduled `RetryNotBefore`, the identical shape `MutationInterface`'s
`IsParkedRetryTarget` and `CancelRequestPoller`'s own `isParked` check already use. A parked
candidate is not delivered the same way a running one is: it is settled through the dedicated path
#1605 built (`InFlightExecutionRegistry.MarkParkedCancelIntent` /
`MutationInterface.SettleParkedCancelIntentsAsync`), never through `CoreEventAggregation` or
`NonProcessCancellationDetector`'s own Running-only filters, which stay unmodified and unconsulted
for a parked target. **Behaviour change from the widening, not just an addition:** a room with one
`Running` step and a sibling sitting in ordinary retry backoff — previously an unambiguous single
`Running` candidate — is now ambiguous and refuses/rejects, since the sibling's `RetryNotBefore` makes
it a second candidate. Deliberately pinned
(`RunningExecutionResolverTests.A_Running_step_and_a_quota_parked_step_together_are_ambiguous`): the
resolver cannot tell "the operator means the one that's actually running" from "the operator means
the one closest to being retried" without guessing, and guessing is exactly what this resolver exists
to refuse to do. Against a room whose `baton run` pump is still live, the direct
mutation call cannot win `flow.lock` — `cancel` catches that specific `WorkflowLockedException` and
writes a room-scoped `cancel.request` file instead (`CancelRequestFile.cs`), which the pump itself
polls at a modest cadence without ever contending the lock (`CancelRequestPoller.cs`) and delivers
through the same `FlowEvent.CancellationRequested` path `MutationInterface` already uses. The
fall-through path re-resolves `latest` at poll time (arresting whatever is running or parked then),
whereas the direct path cancels the execution resolved at command time; on the fall-through path, zero
or more than one candidate at act time lands as a `.rejected` record in the room (with the diagnostic
reason written in its body), rather than a terminal command-line refusal. This is the arrest half of
§10's "only cancellation-then-restart" ruling, not a reopening of it: nothing here reaches into a
running worker to redirect it — it only makes the existing stop-then-`redispatch` sequence reachable
from outside the lane's own process. **Ordering guarantee (#1649):** `RunCommand`'s own startup sweep
of a leftover `cancel.request` cannot claim a live write from a `cancel` racing that same startup
window — the discriminating rule lives on `CancelRequestFile.DeleteStalePendingRequestAsync` itself,
not restated here.

A parked candidate reached through the **direct** path (no live pump contending the lock) is
reachable only when its `RetryNotBefore` has already elapsed AND a live pump is confirmed — a
genuinely still-future park is refused outright by the dead-holder check below before the resolver
ever runs, since that check scans every step for a future deferral, not just the one being targeted.
That check itself was widened in the same change (#1607) from firing only on a confirmed-`Dead`
holder to firing on anything but a confirmed-`Alive` one — see `CancelCommand.cs`'s own dead-holder
gate comment for which `EngineLivenessProbe.Unknown` cases motivate this and why leaving it at
`Dead`-only would have reopened #1586's hang from a new entry point. An already-overdue park
raced against a confirmed-live pump loses to `MutationInterface`'s own retry-obligation check, which
redispatches it before a poller-less pump's parked-cancel-intent wait is ever reached — the same
outcome explicit `--execution` targeting an overdue park already had (tracked separately, #1634);
#1607 did not introduce it and does not close it.

**The dead-holder gate applies to both targeting modes, deliberately, with a real cost on the
explicit one.** The gate runs before `--execution` is even inspected, so `cancel <room> --execution
<id>` against a still-future park is refused on Unknown liveness exactly like the bare `cancel
<room>` form — not because the two paths share reasoning about *which* candidate to pick (they
don't), but because the hang the gate prevents follows from the room holding any pending future
`RetryNotBefore` once `flow.lock` is won, regardless of which execution the caller named. Scoping the
refusal to room-level targeting only would leave the explicit path free to reopen #1586's hang from
the one entry point #1607 widened this gate to close, which would defeat the point of widening it at
all. The accepted cost: before #1607, `Dead` was the only liveness value this gate refused on, so a
genuinely-alive pump with a failed or missing sidecar write (`Unknown`, not `Dead`) still had a
working path — `--execution <id>` would proceed, lose the lock race to the real pump, and fall
through to the `WorkflowLockedException` handling that writes `cancel.request`. Since #1607 widened
`Dead`-only to "anything but confirmed `Alive`," that fall-through is no longer reachable either: an
`Unknown` verdict now refuses both paths up front, even when the pump is genuinely alive. There is
currently no verb that reaches a still-alive pump whose holder record can't be confirmed — the
refusal's own hint (`CancelCommand.cs`) says so rather than pointing at a recovery that does not
exist; `baton status` is not offered as one, since it consults the identical `EngineLivenessProbe`
and would report the same `Unknown`.

**`cancel`'s `--bindings` is now optional too** (#1607 friction fix): omitted, it defaults to
`<room-dir>/bindings.json` — the file a room dispatched via `dispatch`/`redispatch` already holds,
since both write one there (`CancelOptionsParser.cs`). A room started via bare `baton run --bindings
<elsewhere>` never gets one copied in, so the default there simply won't exist; a nonexistent default
surfaces through the same "file not found" `WorkerBindingConfigException` `WorkerBindingConfigParser`
already raises for a bad explicit path — no new failure mode, and the operator falls back to passing
`--bindings` explicitly as before. One fewer argument to retype for the common (dispatched-room) case.
`CancelCommand` augments that exception's message for exactly this default-path case (never for
run/decide/supply, whose `--bindings` is required rather than defaulted) — naming the defaulted path
as a default rather than a mistyped explicit argument, and saying `--bindings` is still available for
a room whose bindings file lives elsewhere.

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
as an error. **And that delete can refuse the run (#1608 re-review).** When the stale sentinel cannot
be deleted — held open by a reader without `FileShare.Delete` — `baton run`/`dispatch`/`redispatch`
refuse before the pump starts, exiting `ValidationRefused` with a message naming the locked file,
rather than pumping behind a record that reads "already done"; only the post-`resolve` delete
(`Program.cs`, which runs after a durable mutation) swallows that failure and warns instead. So the
mirror of the absence list above holds too: **presence** can mean "a previous attempt's sentinel this
attempt refused to start behind", and a harness keyed on the file existing must read the exit code
before treating it as the current dispatch's result.

`baton status` is read-only, produces no `CommandResult`, and always exits 0 when it manages to print a
status at all (`Program.cs`) — it cannot complete a room or substitute for watching the
sentinel.

**Three defects this contract used to carry, now closed (#1375, #1377, #1513) — cited so a harness
author who read an older version of this section knows what changed:** a dead engine's `Running` step
now also reports `steps[].liveness: "dead"` (§3 schema below), computed by the identical
`EngineLivenessProbe` the human `baton status` rendering already used — one probe, two renderings,
never two that can disagree; and a decision-rejected step now sets the top-level `rejected: true`
(§3 schema below) alongside `state: "Failed"`/`error: null`, so an absent `error` no longer implies an
absent cause — it can mean "a person said no" as well as "not yet recorded". Neither fix invents a
value the ledger cannot actually support: there is still no operator-supplied rejection *reason* to
surface (`FlowEvent.ExternalDecisionRecorded` carries none), so `rejected` stays a boolean, not a
`reason` field that would always read `null`.

**#1513 closes a gap #1462 left, not a choice it made.** #1462 added `liveness` as an additive
per-step signal and did not address room-level `state` at all — its issue body frames the change as
extending two fleet views with the same two fields, never weighing whether to fold liveness into
`state`, and neither its own spec text nor the test doc comment it wrote states a reason for leaving
`state` alone. #1513 is the PR that first considers the room-level question. Issue
#1513 was filed against a room (`dispatch-implement-2c5dcd8d`) whose `flow.jsonl` appeared stalled on
`executionRequestAccepted` with its deliverable already on disk — but that room's engine was in fact
still alive and finished naturally minutes later (`terminal.json`: `Succeeded`); the reproducible
defect is a distinct, confirmed live signature — four sibling rooms
(`a0c38801`/`b161e85a`/`d1fb0d42`/`e5d1747c`) each `Failed` with a still-pending `RetryNotBefore`
whose pump process an operator killed, none of which will ever go terminal on their own (§7). That
shape — `Running` (a pending retry is not yet terminal) with **nothing left alive to act on it** —
is exactly the case an operator scanning `fleet_status` most needs protecting from.
`FleetStatusTool.ProcessRoomAsync` now overrides its **own returned `FleetRoomStatusView.State`**
(never `WorkflowStatusView.State`/`WorkflowOutcome`/`state.Status` itself — `RunExitCodeResolver` and
`TerminalSentinelWriter` are unaffected, and `status --json` keeps reporting its own `state`
unchanged, though it now also carries `liveness` on the widened set of steps described below) to
`"Stalled"` — a `fleet_status`-only display string never folded into `WorkflowOutcome` itself —
whenever the room reads
`Running` and every step whose `liveness` this projection computes reads `"dead"` with none reading
`"alive"`. The condition `liveness` is computed under
(`WorkflowStatusProjector.Project`, `src/Baton/Status/WorkflowStatusView.cs`) also widened: previously only a `Running` step was probed;
now a `Failed` step still carrying a `RetryNotBefore` is too (no expiry check — a step keeps this
gate as long as `RetryNotBefore` is set at all, since a stale-but-still-set value is itself part of
the bug this closes), since that step's own promise ("this will retry") rests on the identical fact —
the pump that recorded `StepRetryScheduled` staying alive long enough to act on it (§7: there is no
daemon reaper; `MutationInterface`'s scheduling loop `Task.Delay`s the wait **in-process**).

**`baton resume` does not recover these rooms.** `MutationInterface.RecordResumeAsync` does dispatch a
fresh linked execution off the step's `LatestExecutionId` regardless of `RetryNotBefore`, gated only
on the target not being a multi-step worker or a `NonProcess` binding — but `ResumeCommand.ExecuteAsync`
refuses before that method is ever reached, the moment the bindings entry has no `SessionId`
recorded, and nothing in this codebase writes a non-null one today (adapters do not yet capture a
vendor session id into the room ledger on their own — that capture is #1381, open). Every room this
section describes reaches that refusal. `--message`/`--message-file` is also mandatory on `baton
resume` (`ResumeOptions.cs`) — exactly one is required, so an operator recovering a stalled room has
to invent one even where it applies.

`baton redispatch` is not a substitute either, for an unrelated reason: it refuses any parent room
with no `terminal.json`, and a room in this shape has none by definition (`RedispatchCommand.cs`).

**The verb that actually recovers a room in this shape, verified by running it against a copy of one
of #1513's own four stalled rooms rather than assumed: a fresh `baton run` against the room's own
`workflow.json`/`bindings.json`, `--room-dir` pointed at the room.** `RunCommand` recognizes the
existing `snapshot.json`, accepts the room instead of refusing it, and re-enters the same in-process
wait the original pump was doing — nothing dispatches again until `RetryNotBefore` elapses, and the
process driving that wait has to stay alive for it to fire, exactly as the mechanism above describes.
This is the same re-drive the "known limitation" paragraph below already assumes exists; that
paragraph's own caveat (briefly misreported as still `"Stalled"` while a live pump is in fact waiting)
is the accurate scoping of what this recovers and what it does not.

**`baton cancel` was also checked rather than assumed, and originally left the room worse than it
found it — closed by #1586.** Without `--execution`, a room with no `Running` step used to refuse
outright (`RunningExecutionResolver` had no notion of a parked candidate) — #1607 widened the
resolver so a genuinely-still-parked room now targets that step the same way an explicit
`--execution` always could (§2 above). With the parked execution's id (explicit or resolved), it
used to take the room's lock, clobber the one artifact
naming which engine died, and never come back — `CancelCommand`'s own dead-holder-check comment is
the canonical account of that old failure and today's guard against it, not restated here. #1586's fix
runs before any acquire: `CancelCommand` reuses the same `EngineLivenessProbe` arbiter this section's
`baton status` line already relies on — the two verbs share the probe, not the recorded identity it
probes. `baton status` probes the event-recorded engine identity (`ExecutionRequestAccepted`'s
`EnginePid`/`EngineStartTime`); `CancelCommand` probes the lock-holder sidecar's recorded pid and
process start time instead, since a dead-mid-park room's own `flow.lock.holder` is the only place
that identity survives. So a dead holder with a step still owed a future retry is refused outright,
pointed at the `baton run --room-dir` recovery above, sidecar untouched. A holder the lock is still
genuinely OS-held by (a live pump) falls through unchanged to the pre-existing behaviour.

**#1586 also closed the discoverability half: `baton redispatch`'s own missing-`terminal.json`
refusal, and `baton status`'s dead-engine parked line, now cite the identical `baton run --room-dir`
wording (`Baton.Cli.RecoveryGuidance`) — one string, not three independently drifting phrasings of
the same recovery.** `baton resume`'s refusal is not included: it fires for an unrelated reason (no
`SessionId` recorded, above) that `baton run --room-dir` does not fix either, and #1381 — not #1586 —
is what would let it.

So `"Stalled"` reads as "nothing is currently making progress, but this is not done, and recovering
it needs the operator to start a fresh `baton run` pointed at the room" — never as a `Failed` room a
caller might reasonably discard, and never as a room `baton resume` will quietly fix on its own.

**Known limitation, not closed by this change: a re-drive can still misreport briefly.** If an
operator revives a stalled room with a fresh `baton run` (rather than `baton resume`) while the room
is still inside its retry backoff wait, the new pump re-enters the same `Task.Delay` without writing
a fresh `ExecutionRequestAccepted` — nothing is dispatched again until the wait elapses. `liveness`
still probes the dead original pump's `EnginePid` until then, so `fleet_status` keeps reporting
`"Stalled"` for a room a live pump is, in fact, quietly waiting on. Tracked as #1577, filed rather
than fixed here — closing it needs the new pump to record its own liveness before dispatch, which
belongs with #1556's arrest-predicate/pump-liveness plumbing rather than bolted on separately.

### The terminal vocabulary, and the two-predicate model (#1586 S1)

`WorkflowOutcome` (`src/Baton/Status/WorkflowOutcome.cs`) has **six** members today:

| Value | Meaning |
|---|---|
| `Running` | At least one step's latest attempt is still in flight, or Flow crashed before recording its outcome |
| `Paused` | Nothing running; at least one step idle at a decision point |
| `Succeeded` | Every step succeeded |
| `Failed` | At least one step failed or was rejected, and the room did not settle any other terminal way |
| `Cancelled` | At least one step was cancelled and nothing failed |
| `Indeterminate` | Journal facts alone could not decide success vs failure — see below |

**The two-predicate model.** A room's completion has always actually been two separate questions:
*execution outcome* (did the worker's process finish, crash, or get cancelled — `OutcomeVerdict` /
`FailureClassification`, Flow's own observation) and *contract completion* (did the declared outputs
end up satisfied — `ContractValidator`, a fact about the filesystem). Every value above except
`Indeterminate` is a case where the two predicates agree, or where one alone is enough to decide
(`Cancelled` short-circuits contract completion entirely). `Indeterminate` is what the schema had
never had a word for: the two predicates *disagree* — most concretely, #1594's shape, where the
worker plainly did substantial work (a response-bearing envelope) but the contract's declared
output(s) are simply absent, so "did this succeed" cannot be read off the journal alone. #1608 closed
that one live exception: `OutcomeClassifier.Classify`'s captured-response arm settles
`OutcomeVerdict.Indeterminate` (carrying no `FailureClassification` at all — that vocabulary answers
"why did a genuine failure happen", not "why can this not yet be read off the journal"), never
`Failed(Permanent)`. A worktree fingerprint that fails to reconcile at settle time is the same shape
from a different source, still unimplemented (`baton settle`, S2, tracked on #1586). This is a
**single added enum value, not a two-field split** — the schema keeps its one `state` string; the two
predicates live in code (`OutcomeClassification`/`ContractValidator`), not as two parallel top-level
fields. `StepStatus` itself stays untouched by this ruling too: a step whose latest execution is
`Indeterminate` still projects `StepStatus.Failed` (`Domain.FlowEvent.ExecutionIndeterminate`,
`Projection.StateProjector`); the room-level word is what changes, driven by
`Domain.StepState.IndeterminateAwaitingResolution` (`Status.WorkflowOutcome.DescribeTerminal`, checked
ahead of the ordinary `Failed`/`Rejected` read).

**Four producers, since #1608, #1593 and #1623.** S1 added only the vocabulary, its consumer
obligations below, and the missing retry-foreclosure primitive (next paragraph) — nothing in `src/`
wrote `Indeterminate` from that slice alone. What writes it now:

| Producer | Event | `Domain.IndeterminateProducer` | Landed |
|---|---|---|---|
| `OutcomeClassifier.Classify`'s #1594 captured-response arm — declared output(s) missing, but a terminal response was recoverable | `FlowEvent.ExecutionIndeterminate` (non-null `CapturedResponseFile`) | `CapturedResponse` | #1608 |
| `OutcomeClassifier.Classify`'s #1593 uncaptured contract-failure arm — declared outputs simply absent or failed validation, or a dead worker (stream-json ending without a `result` record) on a mutated workspace, with no response to capture | `FlowEvent.ExecutionIndeterminate` (null `CapturedResponseFile`) | `ContractFailure` | #1593 |
| The role's engine-run verify command exited non-zero after a clean, contract-satisfied worker exit | `FlowEvent.VerifyFailed` | `VerifyFailed` | #1623 |
| A live execution crossed its role's token budget and was arrested | `FlowEvent.ExecutionArrested` | `Arrested` | #1623 |

Every other Failed/Cancelled/Succeeded path is unchanged. All four raise the **one** flag
`Domain.StepState.IndeterminateAwaitingResolution` (`Projection.StateProjector`), which is the single
predicate `Status.WorkflowOutcome.DescribeTerminal` and `Scheduling.RetryEngine.MayRetry` each read —
one arm apiece, never one check per producer. Alongside it, `Domain.StepState.IndeterminateProducer`
(F1, #1593 review) records which of the four raised it — the discriminant `baton resolve`'s admission
test reads (Consumer obligations, below), replacing an earlier `LatestCapturedResponseFile` null/not-null
read that could not tell `ContractFailure` (which DOES have something to reject: the conductor's
judgement after inspecting the workspace) from `VerifyFailed`/`Arrested` (which never do).
`VerifyFailed`/`Arrested` additionally carry human-readable diagnostic text on
`Domain.StepState.IndeterminateReason`; that field is **display only and never a gate**
(`WorkflowOutcomeAndExitCodeTests.An_IndeterminateReason_without_the_flag_describes_as_Failed_not_Indeterminate`
is the discriminating control for that claim). A `ContractFailure` step is never automatically retried
either: re-running blind on a potentially mutated workspace is refused the same way, via the one
`IndeterminateAwaitingResolution` arm — and a `--reject` of it stays retry-foreclosed afterward too
(F8, below), unlike a rejected `CapturedResponse`. `baton settle` (S2, tracked on #1586) is expected to
be able to settle a room *to* `Indeterminate` for the worktree-fingerprint shape; until it lands, that
fifth source is reachable only by a test fabricating a `terminal.json`/status-view shape directly.

**Behaviour change (#1593 F3):** the bounded self-iteration pattern (a worker exits 0 having written a
declared output whose `OutputCondition` is unsatisfied, gets retried, and eventually satisfies it) no
longer retries. `ContractValidator.Validate` reports `UnsatisfiedOutputReason.ConditionFailed` the same
way it reports `Missing`, and `OutcomeClassifier.Classify`'s uncaptured-contract-failure arm does not
distinguish the two — both settle `ContractFailure` Indeterminate. This is the #1593 ruling's own
reasoning applied to a second shape, not a separate decision: an exit-0 worker that fails its output
contract has done unknown work on the workspace, whether the contract violation is a missing file or a
failed condition, so re-running it blind is wrong either way. A worker relying on the old
retry-until-satisfied pattern now settles `Indeterminate` on its first unsatisfied attempt and needs an
explicit `baton resolve --reject --reason <text>` before a fresh dispatch can try again.

**Workspace evidence in the reason (#1593 F2).** #1593's acceptance criteria include: "a room that ends
`Failed` with uncommitted work in its workspace says so somewhere a person will see, rather than
reporting `outputs: []` and leaving the evidence to `git status`." The `ContractFailure` reason text
appends `Workspaces.WorktreeProvisioner.DescribeWorkspaceEvidence`'s bounded account (stray-path count
plus a commits-over-base count, reusing `Audit`'s own git-status read) whenever a worktree path is
available — a room that carries real, uncommitted work reads differently from one that carries nothing,
without a new mechanism. Null (no worktree, or genuinely nothing to report) leaves the reason
byte-identical to before this fix, which is why the fixed no-worktree case stays byte-pinned in
`OutcomeClassifierTests`.

**The resolved base (N2/P4, #1664 review) is meaningful only for a commit-ish ref.** `WorktreeBaseSha`
is `WorktreeProvisioner.ResolveBaseCommit`'s resolution of the worktree spec's ref against the source
repository, re-resolved on every `Walk`/`ReuseForResume` rather than persisted — safe for
`RoleDispatch`'s own `"HEAD"`, since a symbolic `HEAD` always names the commit the source repo was at
when the worker was dispatched, unaffected by anything the worker does inside its own detached
worktree. An operator-authored binding naming a **branch** does not get that guarantee: `git worktree
add` checks the branch out, so a worker's own commit advances it, and the next invocation re-resolves
the same branch ref to the worker's own commit — reporting a workspace that did real work as untouched.



**The dead-worker predicate reads a terminal RESULT, not a terminal SUCCESS (#1593 F6).**
`OutcomeClassifier.Classify`'s `isDeadWorkerWithoutResult` keys on
`CoreDispatchResult.TerminalResultObserved` — true when the worker emitted a terminal `result` record of
ANY status (success or self-reported failure), via `CoreDispatchTarget.DetectsTerminalResult` (agy's own
`IsTerminalResultLine`, wired the same way `DetectsTerminalSuccess`/`IsTerminalSuccessLine` already are).
`TerminalSuccessObserved` cannot answer this question by itself: it reads false both when no result
arrived at all (a dead worker) AND when one arrived reporting `is_error`/`FAILURE` (a worker that
finished and self-reported non-success — a contract failure, not a death, by #1622's own vocabulary).

**The claude adapter wires no terminal-result detector (N6, #1664 review) — a live asymmetry, not a
gap in this fix.** `DetectsTerminalResult`/`DetectsTerminalSuccess` are agy-only
(`git grep DetectsTerminalSuccess -- src/Baton.Vendors` returns `AgyWorkerAdapter.cs` alone); a
claude-adapter worker's `CoreDispatchResult.TerminalResultObserved` is therefore always `false`, so
`isDeadWorkerWithoutResult` is unconditionally `true` for that vendor and the untouched-workspace read
(`Workspaces.WorktreeProvisioner.IsWorkspaceUntouched`) alone decides whether a claude worker's dead
exit stays retryable `Failed` or settles `Indeterminate` — agy gets the extra terminal-result
discrimination this section describes, claude does not. Pre-existing (predates #1593), not narrowed or
widened by it; recorded here because #1664's review found it undocumented outside a response report.

**Consumer obligations, ratified with the value itself.** `baton redispatch` refuses a bare
`Indeterminate` parent outright, with a diagnosis naming the resolution verb
(`RedispatchCommand.cs`) — unlike an ordinary `Failed`/`Cancelled` parent, which redispatches with a
stderr warning. The fleet glass renders a distinct `INDETERMINATE` chip and its own always-visible
section, the same placement `"Stalled"` earned in #1513/#1582 (`tools/fleet-glass/glass.html`).
**Nothing settles FROM `Indeterminate` except an explicit, recorded conductor resolution** — never
silently, never by default. `baton resolve` (#1608, `src/Baton.Cli/ResolveCommand.cs` +
`Mutation.MutationInterface.RecordCaptureResolutionAsync`) is that resolution verb **for the
`CapturedResponse` and `ContractFailure` producers** — see §2's table for its grammar.
`RecordCaptureResolutionAsync` admits a target on `Domain.IndeterminateProducer` (F1, #1593 review), not
a bare `LatestCapturedResponseFile` null/not-null read: `CapturedResponse` admits both
`--accept-capture` and `--reject --reason <text>`; `ContractFailure` has no captured body to accept, so
only `--reject --reason <text>` admits it — the conductor's own judgement after inspecting the
workspace IS something to reject, even with nothing captured. It is *not* a resolution path for the
other two producers: a verify-failed or arrested step (which never carries a captured response, and
whose workspace was never in question the way a `ContractFailure` step's is) is refused in either
direction. Those two reopen only through a fresh dispatch — `ExecutionRequestAccepted` clears the flag,
per `StateProjector`. `baton redispatch` against the same parent room is not that fresh dispatch: its
Indeterminate-parent gate refuses unconditionally and nothing ever clears it for these two producers, so
redispatch is permanently unavailable here — only a brand-new `baton dispatch` room reopens the step,
which `RedispatchCommand`'s own refusal names by producer (`Status.WorkflowStatusStepView.IndeterminateProducerKind`)
rather than offering a verb guaranteed to throw. `baton resolve` reads the step's
`LatestCapturedResponseFile`/`LatestUnsatisfiedOutputNames`
(already surfaced on `WorkflowStatusView`/`terminal.json`/`status --json`, per the schema below);
`--accept-capture` writes the captured response (header stripped,
`Outcomes.OutputMaterializer.StripCapturedResponseHeader`) under each declared output name and settles
the step `Succeeded` — the one path ever allowed to write under a declared name from a capture,
per `OutputMaterializer`'s own ruling — while `--reject --reason <text>` writes nothing and leaves the
step resolved-but-`Failed`. Either way a `Domain.FlowEvent.CaptureResolved` room fact records which,
carrying the conductor's own justification (required for `--reject`; the accept/reject choice already
speaks for itself for `--accept-capture`). **Fact then files, not files then fact (#1608 review finding
5).** `--accept-capture` journals `CaptureResolved` *before* writing the declared output(s) it names —
the fact is durable first, deliberately accepting that a crash between the two can leave the ledger
reading `Succeeded` with an output still missing, rather than the opposite gap the reverse order left
open (a declared output honestly on disk with the room still reading `Indeterminate` and the step still
resolvable, so a later `--reject` could record a rejection while the earlier file silently stayed put).
That gap self-heals: an explicit `baton resolve --execution <id>` naming an execution already accepted
for this exact id is treated as a repair request, not an invalid target, and re-materializes any missing
declared output(s) from the still-durable captured response — a no-op if nothing is missing (the
ordinary exactly-once refusal still applies then), and a fail-closed `InvalidCaptureResolutionException`
if the captured response itself is also gone, with nothing left to re-derive from. The prose-safe/all-or-nothing rule
(`docs/dispatch.md`'s "Roles" section) is not re-derived at resolution time: reaching an unresolved
capture at all already proves `OutputMaterializer.TryCaptureFinalResponse`'s gate passed for every name
in that list, at capture time. `RetryEngine.MayRetry` refuses an unresolved capture unconditionally,
via its own explicit arm on `StepState.IndeterminateAwaitingResolution` — deliberately not by reusing
`FailureClassification.Permanent`'s semantics, since `Indeterminate` carries no classification at all;
once resolved (accepted, or rejected with retry budget remaining), the step's ordinary retry
eligibility applies again. `baton resolve` never re-drives the DAG itself, in either direction — a
rejected, retry-eligible step, *and* an accepted step that leaves a downstream step newly deliverable
in a multi-step room, both need a follow-up `baton run --room-dir` to dispatch again, the same recovery
§7 already describes for a stalled room (F4, #1608 review — the acceptance case was previously
undocumented, reading as though only rejection needed it). `baton resolve` names that follow-up
invocation on stdout whenever the state it returns is not `WorkflowStatus.Terminal`, so a harness never
has to infer it — naming `baton decide` instead when that state is `Paused` (the pause-point case below,
where `baton run` re-enters the same unfulfilled obligation and cannot move the room), and
`baton run --room-dir` otherwise. See "Consumer obligations" above for the sentinel side of the same
non-Terminal case.

**Unless the step declares a `PausePoint`.** Every claim above about `baton resolve` being the *only*
path to an unresolved `Indeterminate` step assumes the step is not also a pause point.
`Scheduling.PauseEngine.GetPauseObligations` reaches a `Failed` step with `RetryEngine.MayRetry` false
through the same round-settled check regardless of *why* retry is refused, so a step that both declares
`PausePoint` and settles `ExecutionIndeterminate` becomes `StepStatus.Paused` with
`IndeterminateAwaitingResolution` still set — and `ExternalDecisionValidator` admits any `Paused` step
to `baton decide`, unresolved capture or not. Two consequences: the room reads `Paused`, not
`Indeterminate`, while it waits (`WorkflowOutcome.Describe` checks `Status` before `DescribeTerminal`
is ever reached — expected, since `Paused` is not itself a terminal word); and a `baton decide` against
that pause leaves `IndeterminateAwaitingResolution` set with no `CaptureResolved` ever appended, so a
later Terminal read of that room still reports `Indeterminate` even though a conductor already decided
its fate through `baton decide` rather than `baton resolve`. Both are pre-existing shapes of the pause
path (the same step read `Failed(Permanent)` with `MayRetry` false before #1608, with an identical
`PauseEngine` interaction) — #1608 changed what the eventual terminal word *is*, not whether a pause
point can intercept it first. Whether `ExternalDecisionValidator` should refuse an unresolved capture
outright, or `DescribeTerminal` should let a recorded decision outrank the flag, is an open owner call,
not settled by this slice (#1655).

**`FlowEvent.StepRetryForeclosed`** (`src/Baton/Domain/FlowEvent.cs`) is the missing primitive the
quota-park symptom this section opened with rests on: before this slice, three events could clear a
step's `RetryNotBefore`/`RetryDelayMs`/`RetryScheduledForExecutionId` — `ExecutionRequestAccepted` (a
fresh dispatch), a `RetryWithRevision`-carrying `WorkflowResumed`, and `ExecutionCancelled`'s own
park-abort clear (#1563) — but none of them voids a scheduled retry *without* either dispatching a new
attempt or cancelling the execution outright. Clearing the fields alone would be wrong: an
`ExhaustedUntil` step bypasses `RetryPolicy.MaxAttempts` by design, so a cleared `RetryNotBefore` with
nothing else changed re-arms the step for immediate re-dispatch against a still-exhausted quota.
`StepRetryForeclosed` instead records the foreclosure as its own fact (`StepState.RetryForeclosed`),
which `RetryEngine.MayRetry` checks unconditionally ahead of every other bypass. Only the first two of
the three events above reopen a foreclosed step (`ExecutionCancelled` terminates the execution rather
than reopening it, so it does not clear `RetryForeclosed`) — a foreclosure is never permanent, but only
a fresh dispatch or a deliberate revision lifts one. A `Supersede` decision's own consequence dispatch is
not a third lifting path: it reopens through the same `ExecutionRequestAccepted` the first clause already
names, and a foreclosed step can never actually be the target of one in the first place —
`ExternalDecisionValidator` refuses any `Supersede` whose target's `StepStatus` is not `Succeeded`
(#271), and `StepState.RetryForeclosed` cannot be true for a step whose status IS `Succeeded`: reaching
`Succeeded` requires the `ExecutionRequestAccepted` that set the step's latest execution, and that same
event unconditionally clears `RetryForeclosedStepIds` for the step (`StateProjector`'s
`ExecutionRequestAccepted` case), independent of which retry it was dispatching. No verb in `src/`
appends this event yet either; S1 ships the primitive and its projection, replay-tested (including
the checkpoint `DeepCopy` hazard #1606 hit first for
`LatestCapturedResponseFileByStepId`/`LatestUnsatisfiedOutputNamesByStepId`), for S2's `baton settle`
to call.

**`FlowEvent.ZeroOutputsDespiteSubstantialWork`** (`src/Baton/Domain/FlowEvent.cs`) is the unconditional
tripwire the #1594 ruling's amendment 3 names: recorded independent of `OutcomeVerdict`/
`FailureClassification`, so it fires whether or not `OutputMaterializer`'s response capture
alongside it succeeded. Unlike the two vocabulary members above, **this one has a live producer in
S1** — `OutcomeClassifier.Classify` computes the evidence (`OutcomeClassification.SubstantialWorkNoOutputsEvidence`)
whenever a worker's own final usage line (read via the resolved adapter's `IWorkerUsageParser`, the
same seam `ExecutionUsageProjector` uses) reports real turns/tokens while every one of the contract's
declared outputs reads `Missing`, and `MutationInterface` appends the event from both classification
call sites — the live dispatch path and the crash-recovery `ToClassify` branch — right alongside the
outcome event, plus a loud `Console.Error` line. Scoped deliberately to the natural-exit-0,
contract-unsatisfied shape (#1594's own): a non-zero exit or a timeout never computes the evidence,
since those failures are already self-explaining and this tripwire targets specifically the case
where nothing else says why the work vanished. Diagnostic only — `StateProjector` records it durably
but it drives no `StepState`/`FlowState` consequence; it exists to be loud, not to change scheduling.

<!-- record-once-ok: #1583 src/Baton/Domain/FlowEvent.cs -->
**`FlowEvent.StepRebound`** (`src/Baton/Domain/FlowEvent.cs`) records that a step's execution was rebound
to a different adapter/model binding (#802 §3.3 / #1583). When crash-recovery resubmission encounters a
binding in `bindings.json` that diverges from the accepted request's recorded `Adapter`/`Model`, Flow
journals `StepRebound` (naming `PreviousAdapter`/`PreviousModel` → `NewAdapter`/`NewModel`) before
dispatching; `StateProjector` applies it as an override on the accepted request's `Adapter`/`Model` so
the rebind survives replay, and `ExecutionUsageProjector` re-attributes the execution's usage to the new
binding rather than silently misattributing it to the pre-crash binding. S6 extends this event (adding
`Effort` and a closed-token `Reason`, per #802 §3.3) rather than introducing a second one.

**`settledAt`/`settledBy` remain unimplemented — S2 scope, not S1's.** The proposal on #1586 §2
names two additive `terminal.json` fields (`settledAt`: ISO-8601 UTC, `settledBy`:
`"pump"`/`"settle"`/`"validation-refused"`) that let a reader tell "this room finished" from "this
room was declared finished after its pump died". Reserved here as a forward pointer only — no field
exists on `WorkflowStatusView` yet, and none should until S2 has a real writer for it.

### Engine-run verify and the token budget (#1623)

Two more producers, both ratified together (operator ruling, 2026-09-01 night, "option 3 ratified",
plus the same night's addendum on token consumption).

**The engine-run verify step.** A role may declare a `pixi run <task>` verify command (`implement` →
`gates-quiet`; `review`/`advise`/every other role → none, `WorkerRole.VerifyPixiTask`). On worker exit
0 with its output contract satisfied, the ENGINE — never the worker — runs the declared command once,
serialized against other lanes by the build lock each gate member takes for itself
(`tools/buildlock.py`); the engine holds no lock across the run (see N1 below). It runs via
`Baton.Mutation.VerifyRunner`, at the live-dispatch call site only (`MutationInterface`'s
`DispatchAndRecordOutcomeAsync`, between `OutcomeClassifier.Classify` returning `Succeeded` and the
outcome event append; deliberately not inside `Classify` itself, which also runs on the crash-recovery
replay branch against a possibly-defunct workspace). `FlowEvent.VerifyStarted`/`VerifyPassed` are
diagnostic-only; `FlowEvent.VerifyFailed` (`FailingMembers`/`Tail`, parsed from `tools/gates/gates.py`'s
own deterministic `summarise()` line) settles the step `Indeterminate` — never a blind retry, the
ruling's own wording — via the same `StateProjector.ApplyIndeterminate` helper the budget arrest below
shares. An operator cancel landing inside the verify window is the one exception: `VerifyFailedKind.Cancelled`
observed together with the caller's own cancellation token already firing means the journal *can*
decide (it holds the cancel), so `MutationInterface` appends `FlowEvent.ExecutionCancelled` instead —
room reads `Cancelled`, retry stays open, `VerifyStarted` survives as the diagnostic record of what was
running. A verify *timeout* still settles `Indeterminate` through the ordinary `VerifyFailed` path.
Worker briefs no longer ask for the full gate suite themselves; the prompt-level foreground instruction
from #1625 (`AgyWorkerAdapter.ForegroundGateInstructionText`) stays as belt (any slow command, not just
gates, should run in the foreground) now that this is the braces.

**The per-execution token budget.** Every role carries a default token ceiling
(`WorkerRole.TokenBudget`: `implement` 600,000, `review` 250,000, `advise` 150,000; every other role
none), overridable per dispatch with `--token-budget`. These figures are carried over unchanged from
before the #1623 re-review; they have not been re-derived against the new `context_level + Σoutput`
quantity below (see N2/F1 in the re-review response — nobody has yet shown, or ruled out, that 600,000
is still the right ceiling for `implement` under the new arithmetic; treat this ceiling as
unverified-but-unchanged, not as freshly justified). `Baton.Mutation.TokenBudgetMonitor` accumulates
usage from the SAME per-vendor `IWorkerUsageParser` seam `ExecutionUsageProjector` reads post-hoc, but
incrementally — `IWorkerUsageParser.TryParseIncrementalUsage` reads claude's mid-stream
`"type":"assistant"` `message.usage` and agy's DONE-state `"step_update"` `usage` (both measured
against real captures, `docs/vendor-capabilities.md` and this PR's own test fixtures respectively) —
composed onto `CoreDispatchTarget.OnStdoutLine` the same way `CoreDispatcher`'s own
`DetectsTerminalSuccess` composes onto an existing sink, never replacing one. The monitored quantity is
`context_level + Σoutput_tokens`: the output side is additive across turns, but the input side is a
*level* (`latest(input_tokens + cache_read_input_tokens + cache_creation_input_tokens)`) that each new
turn's reading replaces rather than adds to — `IWorkerUsageParser`'s own doc states why (never restated
here); `TokenBudgetMonitor` is the worked example. `context_level` is bounded above by the model's own context window (claude ~200k tokens as
of this writing; other vendors' windows are larger and not pinned here), so a runaway `implement` lane
sitting at a full 200k-token context still needs `Σoutput ≥ 400,000` to cross the 600,000 ceiling — this
spec does not show whether that is reachable inside a 90-minute lane; the re-review response records the
absence of that measurement rather than asserting either answer. The monitor reads every top-level `"type":"assistant"` line with no discrimination by
`parent_tool_use_id`. `docs/vendor-doc-audit.md` (#1623 re-review N5) is the canonical measurement:
against real `implement` rooms' captured `.stdout.log` files, a sub-agent's own turns DO appear on this
stream — and because the input side is a level the caller replaces (above), that measurably lowers the
tracked level on exactly the turns where the most work is happening, a live gap rather than merely an
unmeasured one. Not the same surface `cost.subagent-tokens-excluded` (`tools/vendor-verify/verify.py`)
measures, which is the terminal
`usage` object under `--output-format json`, not this mid-stream one.
Crossing the budget cancels the execution via a linked `CancellationTokenSource` (never the
operator-facing `CancellationRequested`/`ExecutionCancelled` pair — that's intent; this is the engine's
own) and appends `FlowEvent.ExecutionArrested` (`Usage`, `LastToolNames` — the last few tool calls
observed, from the same incremental read) instead of an ordinary outcome. Settles `Indeterminate`, same
as a verify failure. A role with no budget and no `--token-budget` override runs unwatched, same as
before this issue; a role whose resolved adapter has no registered `IWorkerUsageParser` also runs
unwatched rather than refusing to dispatch.

**The shared mechanism.** Both producers route through the one `StateProjector.ApplyIndeterminate`
helper — flag, reason text, foreclosure; the `IndeterminateAwaitingResolution` flag is what
`WorkflowOutcome.DescribeTerminal` and `RetryEngine.MayRetry` each check (one arm apiece), per the
producer table above; `StepState.IndeterminateReason` stays display-only, never itself a gate.

### Exit codes

`RunExitCode` (`src/Baton.Cli/RunExitCodeResolver.cs`), returned by `run`, `dispatch`, and
`resume` only — `cancel`/`decide`/`resolve`/`supply` keep the unchanged binary success/failure code
(`Program.cs`):

| Code | Name | Meaning |
|---|---|---|
| 0 | `Succeeded` | Every step succeeded |
| 1 | `Failed` | **Not** exclusively terminal-and-failed — see below |
| 2 | `ValidationRefused` | Provisioning/validation refused, independent of ledger state; the **sentinel write** (not the exit code) is what is conditional on `RoomLedgerProbe.HasLedger` (above) |
| 3 | `Timeout` | At least one step's failure is a timeout and none is a hard failure (`RunExitCodeResolver.ResolveFailed`) — **or** (#1378) `--wait --wait-timeout <minutes>`'s poll loop hit that bound before the room reached Terminal (`CommandResult.WaitTimedOut`); the room itself is still Paused/Running in that second case, not Terminal-and-failed — read `baton status` to tell the two apart |
| 4 | `Cancelled` | — |
| 5 | `RoomHeld` | Another Flow instance already holds this room — retry later, not a terminal outcome; no sentinel is written (`Program.cs`) |

<!-- record-once-ok: #1378 src/Baton.Cli/RunExitCodeResolver.cs -->
**Exit code 1 is not "terminal, a step failed."** `RunExitCodeResolver.Resolve` falls through to
`Failed` for **`Running` and `Paused` too** — any outcome that is not `Succeeded`, `Cancelled`, or the
resolved `Failed`/`Timeout` split (`RunExitCodeResolver.cs`, comment verbatim: *"Running or
Paused: the pump returned short of Terminal (no `--wait`, or `--wait`'s poll loop was cancelled --
e.g. Ctrl-C -- before the room settled; a `--wait-timeout` expiry is handled ahead of this and never
reaches here)... a caller that cares about 'still going' reads `status --json`'s `state` field
instead."*). Concretely: a harness runs `baton dispatch` without `--wait`, the lane reaches a gate and
pauses — the process exits **1**. Reading that as "a step failed" and abandoning a healthy, paused
room is the single most consequential misreading this table can produce, because §5's entire gate
contract depends on that paused room still being there to `baton decide` against. `Indeterminate`
(#1586 S1, above) also folds into exit code 1 — reachable since all three of §3's producers landed
(#1608's captured-response settle, #1623's `VerifyFailed` and `ExecutionArrested`), and named here
rather than left to an unlabelled wildcard, the same discipline the rest of this switch already
follows. A caller's `$?`/`%ERRORLEVEL%` branch sees `Failed`; read `state` (below) to tell it apart
from an ordinary `Failed`. What a harness reaches for once it does depends on which producer settled
it — `baton resolve` (§2) for a captured response, a fresh dispatch for a verify failure or an
arrest. The step's own failure reason (`StepState.IndeterminateReason`, mirrored onto
`LatestFailureReason` and so onto the schema's step `reason`) is what names which. **The rule: exit code
1 alone never tells you whether the room is done. Read `state` from `terminal.json` or `baton status
--json` to distinguish `Failed` from `Running`/`Paused`.** `--wait` makes `run`/`dispatch` block until
the room reaches Terminal or the wait is itself cancelled; `run`'s own `--wait-timeout` (#1378) bounds
that block and reports exit code 3 instead when it elapses first. Without `--wait`, a non-1/0 exit
code is the only signal a lane is even still going, and it is unreliable for that purpose by design.

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
      "liveness"?: "alive" | "dead" | "unknown",  // #1375/#1513: present while this step reads "Running", or "Failed" with a RetryNotBefore still pending
      "exhaustedUntil"?: string  // #1551: the ExhaustedUntil park's reset instant (ISO-8601, UTC) -- gating rule at §6 schema below
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
{ "wallClockMs": number, "tokensIn"?: number, "tokensOut"?: number, "turns"?: number,
  "cacheReadTokens"?: number, "cacheCreationTokens"?: number, "thinkingTokens"?: number }
```
(`src/Baton/Status/ExecutionUsageView.cs` declares the C# record; `WorkflowStatusView.cs` projects it). `wallClockMs` is
always present when the object is present at all — derived from recorded start/exit timestamps. The
three added by #1569 follow one vendor's own field split, not a Baton-invented one: `cacheReadTokens` is a
real field on both measured vendors' envelopes (claude: `cache_read_input_tokens`; agy:
`cache_read_tokens`); `cacheCreationTokens` is claude-only (`cache_creation_input_tokens`) — agy has
never been observed reporting one; and `thinkingTokens` (claude: nested
`usage.output_tokens_details.thinking_tokens`; agy: flat `thinking_tokens`) — each independently
absent when its vendor's line does not carry it, same doctrine as the original three.

**Not all fields are addends — on claude, `thinkingTokens` is a breakdown of `tokensOut`, not a
sibling count; on agy, the containment relationship is unmeasured.** Measured (#1569): on claude,
`thinkingTokens` is reached by descending *into* `usage.output_tokens_details`, an object nested inside
`usage.output_tokens`, so it is structurally a detail of `tokensOut`; on agy, `thinking_tokens` is
reported flat alongside `input_tokens`, `output_tokens`, `cache_read_tokens`, and `total_tokens` (where
`input_tokens + output_tokens == total_tokens`), which cannot arithmetically discriminate whether
`thinking_tokens` is a subset of `output_tokens` or disjoint from it and excluded from `total_tokens`.
Do not assume containment across vendors.

**Summation rules per vendor.** For claude, `cacheReadTokens`/`cacheCreationTokens` are true siblings
of `tokensIn`/`tokensOut` (excluded from both, per measurement), while `thinkingTokens` is a breakdown
of `tokensOut` — so `tokensIn + tokensOut + cacheReadTokens + cacheCreationTokens` is the honest burn
sum, and adding `thinkingTokens` would double-count. For agy, `cacheReadTokens` is excluded from
`total_tokens` (and `input_tokens < cache_read_tokens` rules out inclusion in `tokensIn`); because
`thinkingTokens`'s relationship to `output_tokens` is unmeasured, the exact burn sum cannot be fixed
without an additional vendor measurement (a consumer computing a lower bound sums `tokensIn +
tokensOut + cacheReadTokens`).

**This is attribution, not a complete burn figure.** §7 below rules that lane-log accumulation —
which is what every field here is — is never the reset-time source of truth; the `/usage` poll is.
Separately, `tokensOut` (and now its cache/thinking siblings) is a top-level per-execution figure that
excludes any subagent the dispatched worker itself fans out to, measured at a 22% shortfall on a
single subagent (`ClaudeUsageParser`'s own doc comment,
`src/Baton/Status/StandardWorkerUsageParsers.cs`) and growing with the tree — a gap this schema cannot close
without a field nobody has asked for.

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
`liveness` is present on a step this same projection calls `"Running"`, and (#1513) a `"Failed"` step
still carrying a `RetryNotBefore` — the identical gate `StatusCommand.FormatStepStatus` uses before
probing (a `Paused` step's engine has legitimately exited; a step with no execution yet has nothing
to probe; a `Failed` step with no pending retry has no future engine action to question) — so its
mere presence in the JSON already answers "does liveness apply here" before a caller reads its value.
`rejected` carries no reason text
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
one. One deliberate exception exists on a different plane entirely: C-11 (§11) rules in a
daemon-served, tailnet-bound, read-only drill-down page for the payloads this ruling's plane cannot
carry — the reasoning lives in that entry, and this ruling continues to govern everything reachable
from a conversation.

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
  "state"?: string,               // WorkflowOutcome, PLUS #1513's "Stalled" -- see the paragraph below
  "steps"?: [
    { "id": string, "state": string, "execution"?: string, "linkedFrom"?: string,
      "timestamp"?: string, "usage"?: ExecutionUsageView, "linkedFromUsage"?: ExecutionUsageView,
      "liveness"?: string, "attempt"?: number, "maxAttempts"?: number, "failureKind"?: string,
      "retryEligible"?: boolean, "exhaustedUntil"?: string }
  ],
  "outputs"?: [string],
  "error"?: string,
  "try"?: string,
  "rejected"?: boolean,
  "role"?: string,        // bindings.json's own key for the Running step's worker
  "adapter"?: string,     // that role's WorkerBindingConfigEntry.Adapter
  "model"?: string,       // that role's WorkerBindingConfigEntry.Model
  "effort"?: string,      // that role's WorkerBindingConfigEntry.Effort
  "timeoutMs"?: number,   // that role's WorkerBindingConfigEntry.Timeout, in milliseconds
  "label"?: string,       // #1499: the room's --label, WorkerBindingConfigEntry.Label
  "workstream"?: string   // #1619: the room's --workstream, WorkerBindingConfigEntry.Workstream
}
```
(`FleetStatusTool.cs`). Optional fields are omitted, never emitted `null`
(`JsonIgnoreCondition.WhenWritingNull` throughout `FleetRoomStatusView`/`FleetStepStatusView`);
`rejected` follows the same omit-when-uninformative convention via
`JsonIgnoreCondition.WhenWritingDefault`, so it is absent rather than emitted `false`. This is a
**third shape**, related to but not identical with `terminal.json`/`status --json` — see §3's note on
`linkedFrom` and `timestamp` for the concrete divergence; `liveness`/`rejected` themselves are
identical values across all three shapes (§3). `state` is the one field that is NOT: #1513 overrides
`fleet_status`'s own `FleetRoomStatusView.State` to `"Stalled"` under the condition in §3's #1513
paragraph above, a display word `terminal.json`/`status --json` never emit — a caller reading `state`
identically across all three shapes must special-case this one divergence, the same way it already
special-cases `linkedFrom`/`timestamp`.

**`role`/`adapter`/`model`/`effort`/`timeoutMs` (#1503, extended by #1584 and #1613 item 3)** are read from the
room's own `bindings.json` (`WorkerBindingConfigWriter`/`WorkerBindingConfigParser`,
`Baton.Vendors`). On the active-room path, scoped to whichever step this same projection currently
calls `"Running"` — never a separate probe, and never one entry per worker role the room happens to
define; `adapter`/`model` prefer the running step's recorded `ExecutionRequest.Adapter`/`.Model` values
(#1584, matching `ExecutionUsageProjector` since #1567), falling back to `bindings.json` only when no
execution has recorded them yet (pre-#1567 journals or non-process dispatches). On the **terminal-sentinel fast path** (#1613 item 3 — pre-#1613 this fast path never read
`bindings.json` for these five fields at all, so they silently vanished the moment a room went
terminal, even though the same `bindings.json` a live room reads from is still sitting right next to
`terminal.json`), the resolution is different because there is no "Running" step left to key off:
`TryResolveSoleBinding` (`FleetStatusTool.cs`) reads them only when `bindings.json` names **exactly
one** role. This is a **stated coverage limit, not an impossibility** (corrected 2026-09-01 by
review of #1613's PR): the real answer for a multi-role room lives in `flow.jsonl` — the last
`ExecutionRequestAccepted`'s `Request.Worker`, exactly what the active-room path above already
reads. The terminal-sentinel fast path exists specifically to **avoid opening the ledger at all**,
and resolving a multi-role room's binding would require doing exactly that; `Dictionary` enumeration
order also is not a contract, so even a `bindings.json`-only guess among several roles would be
arbitrary. The trade is real and worth keeping — a multi-role terminal room omits the five fields
rather than pay the ledger-read cost the fast path exists to avoid — the same fail-open-to-absent
posture the rest of this paragraph already establishes, now named as a cost rather than described
as answerless. Both
paths funnel their resolved `(role, entry)` pair through one shared projection
(`ProjectBindingFields`), so the wire shape of the five fields is identical regardless of which path
resolved them.

All five are absent together whenever no step is Running and no sole terminal binding resolves
(pending, paused between steps, or a terminal room with zero or multiple bindings.json roles),
whenever `bindings.json` is missing (a room predating bindings files) or fails to parse, or — on the
active-room path only — whenever a valid bindings file simply lacks the Running step's worker role
as a key (where `resume` treats that as a hard error, this display path degrades): fail-open for
display metadata, so one unreadable bindings file degrades this row, never the whole `fleet_status`
call. `timeoutMs` is deliberately the raw configured timeout, not a countdown — a "remaining" figure
would already be stale by the time a caller reads it. A renderer wanting remaining time pairs it
with the same Running step's own `steps[].timestamp` above, which this shape already emits;
`timeoutMs` is not duplicated there (the terminal path has no live "remaining" concept to pair it
with at all).

**`label` (#1499) is read from the same `bindings.json`, but deliberately NOT gated the way the
quartet above is.** A room's `--label` is a room-level fact stamped onto every entry at dispatch time
(`DispatchCommand.ExecuteAsync`), not scoped to one worker's Running step — so `FleetStatusTool`
reads it off the first entry whose Label is non-null regardless of whether any step is Running, on **both**
`ProcessRoomAsync` paths, including the terminal-sentinel fast path that never reads `bindings.json`
for `role`/`adapter`/`model`/`effort`/`timeoutMs` at all. Absent when never supplied, when
`bindings.json` is missing or fails to parse, or on a pre-#1499 room whose `bindings.json` predates
this field — the same fail-open-for-display-metadata convention the quartet above uses. `redispatch`
carries a room's label into its child unless overridden (§2), so a lineage of redispatches keeps
reading as the same human-named lane.

**`workstream` (#1619) is read from the same `bindings.json`, on the identical shape and gating as
`label` immediately above** — a room-level fact stamped onto every entry at dispatch time, read off
the first entry whose `Workstream` is non-null on both `ProcessRoomAsync` paths, absent under the same
conditions `label` is absent under. `redispatch` carries a room's workstream into its child unless
overridden (§2), so a lineage of redispatches keeps grouping as one workstream. Fleet Glass
(`tools/fleet-glass/glass.html`, `groupLanesHtml`) groups each state bucket's rendered lanes by this
field, alphabetically by slug, with a group heading spanning the lane grid; rooms with no workstream
render as flat, ungrouped lanes exactly as every room did before #1619 — the same fail-open-to-flat
contract `label`'s own absence already has.

**`attempt`/`maxAttempts`/`failureKind`/`retryEligible` (#1509/#1510/#1522)** are copied verbatim from
`WorkflowStatusStepView`, never re-derived here — see that record's own remarks for the gating
rules (`src/Baton/Status/WorkflowStatusView.cs`). Same presence-gated, never-fabricated convention
as `role`/`adapter`/`model`/`effort`/`timeoutMs` above: a step with no execution history omits
`attempt`/`maxAttempts` entirely, and a step that hasn't failed omits `failureKind`/`retryEligible`.
The two failure fields are gated independently of each other, not as a pair: `retryEligible` (the
scheduler's verdict) can be present while `failureKind` is absent, for a Failed step whose worker
hasn't reported a classification yet.

**`exhaustedUntil` (#1551)** is the same `StepState.RetryNotBefore` `FormatVendorQuotaParkNotice`
prints at dispatch time ("the run resumes automatically at 21:59") and `StatusCommand.FormatParkedStatus`
renders on the human `baton status` path — copied verbatim, never re-derived, ISO-8601 UTC. Gated
narrower than `attempt`/`failureKind` above: present only for a `"Failed"` step whose `failureKind`
is exactly `"ExhaustedUntil"` **and** whose reset instant was actually recorded — an un-obligated
park (`RetryNotBefore` null, the human path's "reset unknown") stays absent rather than fabricate
one, and an ordinary `Retryable` backoff never emits this field despite scheduling a
`RetryNotBefore` of its own. Nothing re-derives or clears the value once (#1513) liveness confirms
the scheduling engine dead — a Stalled room keeps reporting the exact same, now-past instant; the
glass chip (`tools/fleet-glass/glass.html`) is what renders that honestly (a relative "was due 3d
ago — no scheduler" rather than a live countdown), never this field. A far-future or already-past
reset instant (#1183, fixed) never reaches this field wholesale: `MutationInterface.GetRetryObligations`
caps an instant more than `MaxExhaustionParkHorizon` (14 days) out to that horizon, and paces an
instant less than `PastResetInstantRetryFloor` (1 second) away — already past, or legitimately
future but imminent — up to that floor, before the obligation is ever recorded as a `RetryNotBefore`
— the crash-on-dispatch bug this closes was `Task.Delay` throwing past its ~49.7-day ceiling on the
raw instant. `exhaustedUntil` is still copied verbatim from
`RetryNotBefore` per the paragraph above, but for a degenerate vendor instant `RetryNotBefore` itself
is now this engine-computed cap or floor, not the raw value the vendor reported — "copied verbatim,
never re-derived" describes this projection step, not a guarantee that `RetryNotBefore` always equals
the vendor's own instant. In practice only one vendor path ever records an obligation to gate on: the agy
duration-parse path (`Resets in …` → `AgyWorkerAdapter`) is what sets `RetryNotBefore` on an
`ExhaustedUntil` park today; claude's `credits_required` park records none. **Corrected (#1609):**
that is not "the vendor never reports a reset instant" — `claude -p "/usage"`/`/cost` reliably
report real, headless reset instants for the session and weekly windows (decision 0026,
`docs/vendor-capabilities.md`), so the claim as stated in the prior revision of this paragraph
overreached. What is actually true is narrower: `ClaudeWorkerAdapter.TryClassifyQuotaExhaustion`
recognizes only the typed `errorCode == "credits_required"` shape (#1115), and neither that shape
nor the CLI's own interactive 5-hour-window limit message has ever been captured, live, carrying a
reset field — every `credits_required` fixture this adapter is tested against is synthetic
(`ClaudeWorkerAdapterTests.cs`), and #1115's own record calls a real cap hit "unprovokable without a
real cap". Provoking one deliberately would mean burning most of a subscription window to capture an
error string, which is an operator spend decision (`CLAUDE.md` "Cost and reversibility are the
operator's call"), not a default action. So today's null `retryNotBefore` on a claude park is a
parser-scope and measurement gap, not a vendor limitation — #1115 still forbids fabricating an
instant nobody has actually observed. A claude park therefore still surfaces the
`"ExhaustedUntil"` `failureKind`, just with `exhaustedUntil` absent and the chip showing no time,
until a real capture exists to build a parser against.

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
  "timeline"?: { "entries": [ { "type": string, "timestamp"?: string, "stepId"?: string, "exitCode"?: number, "detail"?: string } ],
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

**`stepId`/`exitCode` (#1613 item 4) are ids/counts, populated only where the underlying event
carries one DIRECTLY** — `DescribeEntry`'s `FlowEventStepId` reads `ExecutionRequestAccepted`'s
`Request.StepId`, `WorkflowPaused`/`StepRetryScheduled`/`ExternalDecisionRecorded`'s own `StepId`
fields, and `RuntimePermissionAsked`'s `StepId`; `exitCode` reads only
`CoreEvent.ExecutionExited.ExitCode`. Deliberately NOT a cross-referenced lookup through an
execution-id → step-id map built from an earlier `ExecutionRequestAccepted` line (the way
`ExecutionUsageProjector` resolves a worker name for usage attribution) — that would need a first
pass over every entry before this per-entry describe step runs; this stays narrow and on-the-record
only. An entry whose event carries neither omits both fields, same never-fabricated convention as
every other optional field in this shape.

**The operator's 2026-09-01 ruling on content (issue #1613), which governs both fields above and
the mailbox additions below:** the fleet_status/room_detail surface's original content-free
construction (§6 above, `extract_timeline`'s own doc comment) is amended to **COUNTS AND IDS, NEVER
CONTENT** — step ids, exit codes, event detail counts, and live token/tool-call counts are in;
stdout text, prompts, and any other worker-output-derived string stay banned. The secret-gate
boundary this amends nothing about: `tools/fleet-glass/pusher.py`'s `extract_timeline` still reads
exactly the fields it enumerates as KEPT (now `type`/`timestamp`/`stepId`/`exitCode`) off each
`room_detail` entry and nothing else — a future `room_detail` field still never leaks through by
accident of that function failing to name it.

**The pushed mailbox payload carries two fields `fleet_status`/`room_detail` do not (#1613 items 1
and 2) — pusher-computed, not part of either MCP tool's own C# output above.** Both are read
directly off the room's already-captured `.stdout.log` or wall-clock, python-side
(`tools/fleet-glass/pusher.py`), because `Status.ExecutionUsageProjector`'s engine-side seam only
ever populates an execution that has recorded BOTH a `CoreEvent.ExecutionStarted` AND
`ExecutionExited`, and its parser contract (`IWorkerUsageParser.TryParseFinalUsage`) reads exactly
the last non-blank line of the captured stream — neither fits a still-running execution, which by
definition has no exit event yet and needs every line scanned, not just the last:
- **`rooms[].live` (item 1, extended by a 2026-09-01 review of #1613's PR)**, present only for a
  room whose pusher-displayed `state` is exactly `"Running"`:
  `{ "toolCalls"?: number, "outputTokens"?: number, "contextTokens"?: number,
    "cacheReadTokens"?: number, "lastActivityAt"?: string }`.

  `toolCalls` counts `tool_use` blocks in claude's `assistant` stream events and DONE/`tool`
  `step_update` heartbeats in agy's — both shapes measured (docs/vendor-capabilities.md's
  `#1559`/`#1088` rows, `tests/Baton.Cli.Tests/RunCommandEchoTests.cs`,
  `AgyWorkerAdapter.TryParseProgressEvent`'s own doc comment). This is two different things under
  one field name, disclosed rather than left to be inferred: claude counts tool *requests*, agy
  counts DONE tool *steps*. Both are whole-tree, including subagent turns — claude's `assistant`
  events for a subagent carry `parent_tool_use_id` but are never filtered out, deliberately (the
  mirror image of `outputTokens`'s own subagent completeness below).

  **Live tokens, claude only.** The original ruling — "token counts are deliberately never
  emitted… an absent field is honest, a summed one would re-count each turn's whole context" — was
  right about the trap and wrong about the conclusion: it correctly noted neither
  `docs/vendor-doc-audit.md` nor `python tools/vendor-verify/verify.py --list` records a
  per-assistant-message (mid-stream) usage figure, but treated that silence as a verdict rather than
  an open question still worth checking. A live capture on 2026-09-01 settles it — see
  `docs/vendor-capabilities.md`'s history table (top row) for the captured key list and the exact
  command run; every one of the four raw usage keys that row names lands on the SAME assistant
  message stream-json already flushes mid-turn, well before the lane's terminal `result` line.
  `outputTokens` sums the message's output count across every `assistant` line in the execution's
  `.stdout.log` (additive, whole-tree) — this is *more* accurate than the terminal line's own
  cumulative figure, which `docs/vendor-doc-audit.md` measures undercounting by ~22% with a single
  subagent in the tree (`usage.output_tokens` excludes subagent tokens; the gap grows with the
  fan-out). `contextTokens` (the sum of the message's fresh-input count and both its cache counters)
  and `cacheReadTokens` (the cache-read counter alone) are read off the LATEST `assistant` line only
  — a LEVEL, replaced every turn, never summed: the trap the original ruling correctly named applies
  to the fresh-input count specifically (summing it across turns re-counts each turn's whole
  repeated context), not to output or to a single turn's own level. All three fields are absent, never a substituted zero, when a
  line's `usage` object doesn't carry what is needed. agy emits none of the three: its `step_update`
  heartbeat carries no `usage` field at all (`AgyWorkerAdapter.TryParseProgressEvent`,
  `AgyWorkerAdapter.cs`) — a claude-only measurement stays a claude-only field.

  `lastActivityAt` is the stdout log's own last-write instant (a real filesystem fact, not `now()`),
  quantized to a ~90s bucket before it enters the pushed payload (2026-09-01 review finding) — see
  `pusher.py`'s `LAST_ACTIVITY_BUCKET_SECONDS` for the write-budget reasoning this closes. Quantized,
  not excluded the way `derived_at` is excluded below: a prose-only turn with no tool call in it
  would leave every OTHER field in `live` unchanged too, so excluding this one as well would freeze
  glass's rendered age on an old instant while the lane is, in fact, still going.
- **`derived_at` (item 2)**, beside `heartbeat_at` (#1486) at the top level of the pushed snapshot:
  when this pusher process's OWN `derive_snapshot_and_timelines` call last completed successfully,
  regardless of whether that cycle's content changed enough to push. `pushed_at` (worker.js's own
  receipt time) is legitimately stale on a quiet-but-healthy fleet — the #1457 change-gate skips an
  unchanged snapshot on purpose — so Fleet Glass's "Snapshot derivation may be stuck" banner
  (`tools/fleet-glass/glass.html`) keys on `derived_at` instead: a fleet that stays quiet because
  nothing changed still reads healthy, while a derivation that has been raising every cycle for
  hours (the real failure mode this exists to catch — a hung `dotnet mcp` subprocess starves
  `derived_at` too, on the same timescale it starves `heartbeat_at`, since both live in the same
  loop iteration) still alarms. Reaches the mailbox by two routes that share one KV write budget
  rather than add to it: riding inside an actual snapshot push's own body (excluded from
  `snapshot_hash` so it can never itself force a push), or via a dedicated ping on the same
  `/heartbeat` endpoint whenever a push hasn't landed one recently (`should_send_derived_ping`) —
  see `pusher.py`'s own module docstring for the write-budget arithmetic this is built around. A
  missing `derived_at` (a pusher not yet redeployed for #1613) now gets its own explicit banner
  rather than silently falling through to a clean one (2026-09-01 review finding) — mirrors the
  sibling `heartbeat_at`-absent message.

  **`pending_push_age_s`, a second field this same review pass added to the SAME `/heartbeat` ping
  body `derived_at` rides:** the gap `derived_at` alone leaves open is `pusher.py`'s own comment
  above this field's definition — a healthy derivation loop sitting behind a POST that will not
  land, for any of the ordinary transport reasons a mailbox POST can fail. Dropping the pre-#1613
  `pushed_at` staleness check removed the only signal that used to catch that; this PR's own
  terminal-timeline addition also raised the odds of hitting it, by growing the typical payload
  toward the push route's own size cap. `pending_push_age_s` is seconds since the pusher's last
  SUCCESSFUL push, present only while `should_push_snapshot` says content is actually waiting to go
  out — absent on a healthy, nothing-changed fleet, so a legitimately quiet lane never false-fires.
  Fleet Glass alarms "Push failing" once it exceeds a threshold on the same order as the
  derivation-stuck check above, independent of whether any room is Running (a failing push is not
  scoped to active lanes the way the derivation-stuck check is).

**Paging and the terminal hot-set cap (#1656).** Measured 2026-09-02: `deliverables_list` returned
292 items / 160,539 bytes in one body, big enough that the operator's MCP connector reported
"Inbox feed unavailable (upstream_error)"; `fleet_status` was 265,193 bytes / 234 rooms per push.
Both mailbox tools (`tools/fleet-glass/worker.js`'s `handleMcp`) now page:
- **`deliverables_list`** takes `limit` (default 50, max 200) and an opaque `cursor` — base64 of the
  next item's own `(pushed_at, id)` identity, so a caller round-trips it verbatim with no
  server-side per-cursor state. Response carries `items`, `count` (the total after any `room`
  filter), and `next_cursor` (`null` once exhausted). A malformed or foreign cursor degrades to the
  start rather than throwing, same posture as every other optional-field convention in this module.
  The list's order is delivery order, not a `pushed_at` sort — `handleDeliver` builds the index
  purely via `index.unshift(...)` per delivered item (`worker.js`), so "newest first" means "most
  recently delivered to the worker," not "newest `pushed_at` first." The cursor is identity-based
  (matched by `(id, pushed_at)`, not by position), so it tolerates a `/deliver` POST landing between
  two `deliverables_list` calls rather than skipping or repeating items.
- **`fleet_status`** stays a single tool (no `rooms_list` sibling — `FleetGlassReadOnlyTests` pins
  the mailbox's `TOOLS` array to exactly `fleet_status`/`deliverables_list`/`deliverable_read`) but
  grows a `page`/`limit` argument pair. With neither argument, `rooms` carries every non-terminal
  room plus only the newest `HOT_TERMINAL_CAP` (40, `tools/fleet-glass/pusher.py`) terminal ones,
  and the response gains `terminal_total` (the full terminal count). Passing `page` (0-based) pages
  over the REST of the terminal population instead. `terminal_archive` rides inside the SAME
  `"snapshot"` KV value as everything else (folded in by #1690 item 2 — previously its own KV key,
  written by a second `env.FLEET.put` on every push that had one; see "Fleet Glass write budget"
  below for why that second write mattered); a plain `fleet_status` call's response size still stays
  independent of the fleet's all-time terminal-room count because `handleMcp`'s `fleet_status`
  branch strips `terminal_archive` back out on the READ side instead of it never having been
  written together. `pusher.py`'s `split_hot_and_archive` computes the hot set
  and archive from the SAME `newest_timestamp` measure `drop_stale_rooms` already uses, so "newest"
  means the same thing everywhere in this module; `timelines` in the pushed body is filtered to the
  hot set's own paths, never the wider surviving-room set, so an archived-only terminal room's
  timeline never rides the hot push either. `tools/fleet-glass/glass.html`'s Terminal section
  fetches additional pages on demand (a "load older" link, wired to a one-shot `fleet_status(page,
  limit)` call through the same `watchTool` the periodic poll already uses) and merges them into the
  rendered Failed/Succeeded buckets, deduped by room path against whatever the hot set already
  showed.

  The cap bounds only the terminal bucket. `non_terminal` rooms — Running, Stalled, Indeterminate —
  ride the plain (no `page`) `fleet_status` response in full, uncapped; `split_hot_and_archive` never
  slices that list, and `glass.html` never pages it either. The 265 KB / 234-room measurement above
  was terminal-room-dominated; a fleet with many concurrently *active* rooms at once (an incident
  storm) can still produce an unbounded default payload, and nothing in this module measures or caps
  that case. `pusher.py` logs one line via `HOT_NONTERMINAL_WARN` (60) when the non-terminal count
  exceeds it on a push — a signal for an operator to notice, not a cap.

**`heartbeat_at` now advances on every successful push (#1656), not just on the hourly
`/heartbeat` ping.** Measured 2026-09-02: `heartbeat_at` stayed at `07:11:28Z` across pushes at
`07:32` and `07:34` even though both succeeded. Root cause: `should_send_derived_ping` (above)
deliberately skips the dedicated `/heartbeat` POST whenever an actual snapshot push already landed
a fresh `derived_at` within its own 5-minute window — correct for `derived_at` itself, but
`heartbeat_at`'s own `at` value is ONLY ever stamped by that same POST, so a fleet pushing
continuously (never idle long enough to need a dedicated ping, never quiet long enough to hit the
hourly cadence) could see `heartbeat_at` sit stale for up to an hour despite every push succeeding.
Fixed in `worker.js`'s `handleMcp` (`fleet_status`'s DISPLAYED `heartbeat_at`, not the stored KV
value) by merging in the snapshot's own `pushed_at` — the same `maxIsoOrNull` merge `derived_at`
already uses, and the same reasoning applies: `pushed_at` is stamped by this Worker's own receipt
clock (`/push`'s handler, never the pusher host's clock), the identical clock-source property
`heartbeat_at`'s `at` already has, so folding it in costs zero extra KV writes and never weakens the
"quiet fleet apart from dead pusher" distinction §7's heading above this one describes — on a quiet
fleet `pushed_at` is exactly as stale as `heartbeat_at` already was, so the merge is a no-op there.

**The false Running ⚠ (#1549, fixed by #1656).** `glass.html`'s per-room age line marked a Running
room ⚠ whenever its last JOURNAL event was more than 15 minutes old — but a healthy 30-minute lane
can have zero journal events between `executionStarted` and `executionExited` (#1549's own
measurement: 6 false STALL-shaped flags out of 6 live rooms), so every long-running tool call read
as stale. `ageLine` now keys the ⚠ on `room.live.lastActivityAt` (the `rooms[].live` field above,
itself a real `.stdout.log` mtime) when the room carries a `live` section at all, and falls back to
the journal-event age only for a Running room `live` was never attached to.

**Fleet Glass write budget (#1690).** Cloudflare's free-tier KV namespace caps at 1,000 writes/day;
the mailbox blew it TWICE (2026-09-02) because the pre-#1690 design budgeted one write per snapshot
push and sized its coalescing floor to ~960/day — i.e. it sat AT the cap before deliveries and
heartbeats were even added, and did not know `worker.js`'s `/push` handler wrote `terminal_archive`
as a SECOND, unconditional `env.FLEET.put` alongside `"snapshot"` whenever a terminal room existed.
Measured that day (`pusher.log`, 00:00–16:50 UTC): 783–1,252 writes from snapshot pushes (469, each
1–2 writes), deliver batches (120 batches, K+1 writes each), and heartbeats (17) combined. This is
the canonical record for the fix that replaced that arithmetic; `tools/fleet-glass/pusher.py`'s own
module docstring and section comments cite this entry rather than restating it.

The fix is a hard, pusher-owned daily write-budget LEDGER, not a smaller fixed interval: a
per-UTC-day counter of KV writes by producer, persisted in its own file
(`DEFAULT_BUDGET_STATE_FILE`, `write-budget.local.json`, F4 below), with a real cost per producer
that matches what `worker.js` now actually writes (the folding below is what makes these costs
small and flat, not scaling with content):
- **Snapshot push** — `SNAPSHOT_KV_WRITE_COST` (1). `terminal_archive` now rides inside the SAME
  `"snapshot"` KV value (folded in by item 2 below) instead of a second, unconditional
  `env.FLEET.put` — one write per push, full stop, never two.
- **Deliver batch** — `DELIVER_BATCH_KV_WRITE_COST` (3), flat, no matter how many items a single
  `/deliver` POST carries — down from a cost that scaled with item count before item 2's fold (see
  "Item 2" below for exactly what changed on `worker.js`'s side). The 3rd write is a conservative
  charge for the delete path (F3(a)/F5 below), not a third `put`.
- **Heartbeat or derived-freshness ping** — `HEARTBEAT_KV_WRITE_COST` (1) — unchanged; the two
  cadences already shared one write per POST (#1613 item 2) and stay mutually exclusive per cycle.

**Per-producer sub-budgets and pacing, not a shared pool (F1, 2026-09-02 review).** The FIRST shipped
version of this ledger (`KV_DAILY_WRITE_TARGET` 700 with a single `DELIVER_RESERVE` of 100 carved
out for deliverables/heartbeats) passed its own arithmetic gate and was still a worse operator
experience than the incident it replaced: because only the snapshot half had adaptive pacing, deliver
(by far the fastest producer) could spend the ENTIRE shared pool before the reserve even mattered —
23 snapshots crammed into the first two hours of the day, then 21h45m of total silence (zero
snapshots, zero deliverables, zero heartbeats) until UTC midnight. A reserve sized as a flat write
count, not a share of the day, is not a reserve against a faster producer at all. The fix: each
producer gets its OWN daily sub-budget, gated independently —
- `SNAPSHOT_DAILY_WRITES` (300), `DELIVER_DAILY_WRITES` (320), `HEARTBEAT_DAILY_WRITES` (60) — sum
  680, under `KV_DAILY_WRITE_TARGET` (700), which stays as the overall sanity ceiling the arithmetic
  gate checks the ledger's grand total against; it no longer gates any individual write.
- `snapshot_pushes_allowed`/`deliver_allowed`/`heartbeat_allowed` each check ONLY their own
  producer's counter against its own sub-budget (`pusher.py`), never `budget_left` of the combined
  total — the write that would cross a producer's own line is simply never attempted for THAT
  producer, with no effect on the other two.
- **AND its own adaptive pacing**, not just a sub-budget: `adaptive_producer_interval_s` (one shared
  formula, `adaptive_snapshot_interval_s`/`adaptive_deliver_interval_s`/
  `adaptive_heartbeat_interval_s` as its three per-producer names) widens each producer's own
  interval as ITS OWN remaining sub-budget for the rest of the day shrinks:
  `interval = max(producer_min_interval_s, seconds_left_in_day / max(1, producer_writes_left /
  producer_cost))`. A bare sub-budget without this would still let deliver (or the
  derived-freshness ping, once snapshot's own throttling stopped suppressing it via
  `LAST_PUSH_TS_KEY`) burn its whole share in the first couple of hours and go dark for the rest of
  the day — the same failure shape at a smaller scale. Deliver's own last-sent timestamp
  (`LAST_DELIVER_TS_KEY`) and `should_coalesce_producer` give it the same coalescing-floor mechanism
  snapshot already had (`should_coalesce_push`, #1538).

**The gate asserts DISTRIBUTION, not just a total (F1/F2).** A total-only check can only ever report
`<= budget` — any arithmetic that routes every write through its own enforcement functions can never
report otherwise — which says nothing about WHEN in the day those writes land, and is exactly what
let the shared-pool design pass its own gate while still going dark for 21h45m.
`simulate_worst_case_daily_writes` now returns per-producer write TIMESTAMP lists (not just the final
ledger), and the selftest asserts, for the snapshot producer at max cadence: the largest gap between
consecutive writes never exceeds 1800s (never a half-hour blind spot), and the day's last write lands
within 1800s of midnight (the day ends still serving). Both assertions are proven to discriminate: a
frozen, hardcoded reproduction of the shared-pool design this PR replaces (`pusher.py`'s
`_legacy_shared_pool_worst_case` selftest helper) passes the old total-only check (700 used == 700
target) but FAILS both distribution assertions (max snapshot gap ≈79,000s; last write ≈8,300s) — the
red half of red-then-green, committed rather than thrown away (the #1690 postmortem's own complaint
about the PRIOR gate's control arm, per F2 below). Against the shipped per-producer design both
assertions pass (max gap 300s; last write within the final 300s of the day).

**F2: a control that actually discriminates.** The first version's "(control) an impossibly low
target fails" assertion was `<= 1` — it PASSED, for the same reason the real arm passed (the ledger
clamps), exercising no path a genuine overrun would take. `simulate_worst_case_daily_writes` now
takes a `ledger_enabled=False` parameter that bypasses every gating check, plus a configurable
`snapshot_cost`/`deliver_cost` (the latter accepting a callable for a per-item shape) — feeding it
the pre-#1690 shape (`ledger_enabled=False, snapshot_cost=2, deliver_cost=lambda k: k + 1`) produces
39,768 writes/day, comfortably over 1,000, proving the gate can fail when fed a genuine overrun.

**F7: the gate now runs the config the fleet actually runs.** The prior selftest only ever drove
`simulate_worst_case_daily_writes` at its default `min_push_interval_s` (90), while the deployed
pusher runs 300 (the operator's own #1690 mitigation) — the printed "23 snapshots" described a
config nothing was running. The selftest now runs both 90 and 300 and prints each; the deployed
number is 300.

**Adaptive snapshot cadence** is `adaptive_snapshot_interval_s` (`pusher.py`'s own docstring says why
it keeps its own name rather than being called generically). Once `snapshot_pushes_allowed` goes
false (snapshot's own sub-budget is spent), the pusher sends exactly ONE more snapshot — carrying a
`pusher` block,
`{"writeBudgetExhaustedUntil": <ISO of the next 00:00 UTC>}` — and then stops snapshot pushes for the
rest of the day; deliverables and heartbeats/pings are unaffected, since each spends from its own
sub-budget. `writeBudgetExhaustedUntil` is absent on every ordinary push, same optional-field
convention as `conductor`; `glass.html`'s freshness strip reads it absent-safe and shows it ahead of
every other staleness banner. **F11 (2026-09-02 review):** the exhaustion-notice push clears
`SNAPSHOT_HASH_KEY` rather than persisting the notice body's own hash under it — the notice's content
(`notice_wrapped`, carrying the `pusher` block) differs from the ordinary snapshot hash
(`current_hash`, computed from `wrapped`), so persisting `current_hash` there left a stale
`writeBudgetExhaustedUntil` banner able to survive past the instant it named on an all-terminal, quiet
fleet at the next UTC-day rollover, suppressing every real banner beneath it. Clearing it means the
first cycle of the new day always re-pushes, regardless of content match. Never a silent stop:
`pusher.log` gets one line per hour naming the ledger regardless of which producers are still
spending (`format_budget_log_line`): `budget: used N/700 (snap a, deliver b, beat c), interval now
Xs`.

**F3(b): the ledger is charged BEFORE the POST, in all three producers.** `worker.js` returns 200
only after its `env.FLEET.put` has already committed, so a client-side timeout or a dropped
connection after that commit is a real KV write the ledger would otherwise never see if it only
counted on success — the exact silent-overshoot mode this ledger exists to close, on a flaky link
that repeats every cycle. A client cannot distinguish "the worker never wrote" from "the worker wrote
and the response was lost", so for a hard external cap the only safe posture is to charge first: this
does over-charge a genuine failure where nothing happened (DNS failure, connection refused, a 413),
and that cost is real, but under-charging costs the cap itself. This is deliberately the OPPOSITE
ordering from the hash/dedupe persistence discipline (`push_snapshot_and_record`,
`send_heartbeat_and_record`, deliver's own `mark_pushed`/`LAST_DELIVER_TS_KEY`) — those still persist
only after a successful POST, since the hash governs correctness of CONTENT while the ledger governs
a hard external LIMIT; they are different things sharing what used to be one branch.

**F4: the ledger lives in its own file, written atomically.** `write-budget.local.json`
(`DEFAULT_BUDGET_STATE_FILE`), separate from `push-state.local.json` — so a lost or reset
deliverables-dedupe state file does not also zero the day's spent budget, and vice versa. Both files
are now written via a sibling temp file plus `os.replace` (`save_push_state`) rather than
`write_text`'s truncate-then-write, since a process killed mid-write (the deploy path's
terminate-and-replace SIGTERMs the incumbent pusher on every deploy, and this file is rewritten
several times per cycle) could otherwise leave a truncated file `load_push_state` cannot distinguish
from "no file" — silently resetting the ledger to zero and re-arming the exhaustion notice.
`os.replace` is atomic on both Windows and POSIX.

**F10: a monotonic rollover guard.** `load_budget_ledger` previously keyed purely on
`utc_day_str(now_ts)`, so an NTP correction moving the clock backward across midnight (or a repeated
forward/back correction) handed the same real day a second full budget the moment the stored date no
longer matched. It now refuses to roll back: a stored date strictly LATER than what `now_ts` claims,
with real usage already recorded, is served as-is; a backward jump against an all-zero stored ledger
is harmless and re-keys onto the earlier day (nothing to double-count yet).

**The arithmetic is now a gate, not a claim, scoped honestly (F9).** `pusher.py --selftest` computes
the worst-case daily write distribution with every producer at its own maximum cadence, driven
through the SAME `snapshot_pushes_allowed`/`deliver_allowed`/`heartbeat_allowed`/
`adaptive_producer_interval_s` functions `main()` itself uses (`simulate_worst_case_daily_writes`),
and fails the selftest if the total exceeds `KV_DAILY_WRITE_TARGET` or either distribution assertion
above fails. This property holds for the three GATED producers (snapshot, deliver, heartbeat/ping)
and the costs named above — it does NOT hold for two paths outside the ledger entirely: (a)
`env.FLEET.delete` on legacy per-item eviction (`worker.js`, uncounted before F3(a); now covered by
`DELIVER_BATCH_KV_WRITE_COST`'s conservative +1, but the physical delete itself is still ungated by
any pusher-side check — it happens on the WORKER side, driven by the index's own size, not by
anything the ledger throttles), and (b) F5's refcounted orphaned-batch reclaim (same file, same
conservative +1, same caveat). Whether Cloudflare's KV free tier counts a delete against this same
1,000/day write limit, or a separate delete limit, is unverified from here (no network access to the
current limits page) — treated as a write, the conservative reading.

**Item 2, the worker-side fold, in one place:** `worker.js`'s storage-key docstring (this file's own
header) is the canonical record of exactly which KV keys exist post-fold
(`"snapshot"`/`"inbox:batch:<id>"`/legacy `"inbox:item:<id>"`) and the read-side fallback for
deliverables delivered before this change; not restated a third time here.

**F5: refcounted batch blobs.** Pre-fix, a batched entry's `inbox:batch:<id>` blob was deliberately
left orphaned once its last index reference was gone (eviction, or a re-delivery re-stamping the
same id under a new batch id) — unbounded KV storage growth with no reaper, no metric, no alarm,
whose eventual failure mode (the namespace filling, `env.FLEET.put` starting to fail) looks like
nothing in this PR months later. `worker.core.mjs`'s `computeDeliverBatch` now also returns
`orphanedBatchIds` — batch ids no remaining index entry references after this POST's eviction or
re-delivery — and `worker.js`'s `handleDeliver` deletes those blobs. Amortised to roughly one delete
per batch in steady state, which is what `DELIVER_BATCH_KV_WRITE_COST`'s conservative +1 budgets for.

**F8: `deliverable_read` distinguishes "known but not replicated" from "no such id".** Post-item-2,
resolving an id spans two reads (`inbox:index`, then `inbox:batch:<id>`) instead of one — KV is
eventually consistent across colos, so there is a real window where the index has propagated (how an
operator sees an id in `deliverables_list` at all) while its batch blob has not. `worker.core.mjs`'s
`deliverableReadOutcome` (pure, selftest-covered) tells that apart from a genuinely nonexistent id:
when the index itself names a `batch_id` for the id but neither the blob nor the legacy key resolves,
`deliverable_read` returns a distinct "known but not yet replicated — retry in a minute" message
instead of asserting non-existence for something the same request's own index says exists.

**F13: the deliver batch cap is BYTES, not item count.** Post-item-2, a `/deliver` POST costs the
SAME flat `DELIVER_BATCH_KV_WRITE_COST` regardless of item count K, so the old fixed
`DEFAULT_DELIVER_BATCH_CAP` (10 items, sized when cost scaled with K) bought nothing once the fold
landed and cost a lot: a 210-item backlog that could ship as 1 batch instead cost 21
write-amplifying ones. `gather_deliverables`/`gather_conductor_deliverables` now cap by cumulative
content bytes (`DEFAULT_DELIVER_BATCH_BYTES`, ~4MB, safely under `worker.js`'s 5,000,000-char
`/deliver` body cap) with a generous item-count ceiling (`DEFAULT_DELIVER_BATCH_COUNT_CEILING`, 2000)
as a backstop only — at least one item is always admitted even if it alone exceeds the byte budget
(fail toward one oversized batch, never toward silently dropping the only thing to show).

**Item 3, the telemetry churn gate, quantizing VALUES not the clock (F6, 2026-09-02 review).** A
Running room's `live` section (item 1 above) changes almost every cycle by construction —
`toolCalls` incrementing, tokens accumulating, `lastActivityAt`'s own 90s bucket advancing — which
would otherwise re-trigger the #1457 change-gate every `interval_seconds` regardless of the
write-budget ledger's own throttling. The FIRST shipped version of `quantize_live_for_hash` bucketed
`now_ts` (the wall clock at evaluation time) into a `LIVE_TELEMETRY_HASH_BUCKET_SECONDS` (300s)
index — which meant ANY Running room with a `live` section forced the hash to flip every 300s
regardless of whether the room's own telemetry had moved at all, guaranteeing the snapshot half
always drew its full ~288/day on any active fleet. This is what made F1 unavoidable rather than
load-dependent: the snapshot half never had a quiet-fleet case that gave the budget back. The fix
quantizes the telemetry VALUES themselves: `lastActivityAt`'s own parsed instant is bucketed to the
same 300s grain, and `toolCalls`/`outputTokens` are coarsened to their own grain
(`LIVE_TELEMETRY_TOOLCALLS_GRAIN` 5, `LIVE_TELEMETRY_TOKENS_GRAIN` 10,000) — an unchanged lane now
hashes unchanged forever, since the function takes no wall-clock argument at all; an advancing one
flips at most once per bucket/grain of REAL progress. A structural change (a different room set, a
state transition, a new or changed deliverable, error text) lives in fields this quantization never
touches, so it still changes the hash — and triggers a push, budget permitting — on the very next
cycle.

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

**No daemon reaper (#1513).** None of the kept surface above — the room-watcher, `RoomRetentionSweep`,
or the concurrency-cap apply — ever re-drives a room's own pending retry or reaps a room whose pump
has died. `MutationInterface`'s scheduling loop is the only thing that ever acts on a
`StepRetryScheduled`/`RetryNotBefore` wait: it `Task.Delay`s that wait **in-process**, inside the same
`baton run`/`baton dispatch` invocation that recorded it. If that process exits or is killed, nothing
else in the system will ever complete the room — it does not go terminal on its own. Recovery is
`baton resume`, an operator-driven action, never automatic.

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
- **#1657: throwaway repro rooms are excluded, not registered then pruned.** `RoomRegistryStore.AppendAsync`
  skips writing a room that looks like a repro rather than fleet work (one stderr line names it) —
  `IsThrowawayReproPath`'s doc comment on that type is the one place the exact rule is stated. This is
  wider than the manually-created `%TEMP%\...` repros the issue reported: a **bare `baton run` with no
  `--room-dir`** defaults to `{cwd}/.baton/{workflow}` (`RunOptionsParser`) and is caught by the same
  `.baton`-segment rule, so an ad hoc `baton run` against a workflow file is unregistered by default too,
  not only an explicit temp-dir repro. `baton run`'s `--register` flag (`RunOptions.Register`) opts a
  given room back in; `baton dispatch`/`redispatch` always pass it, since a resolved dispatch/redispatch
  room is fleet work by construction — the flag only ever matters there for an explicit `--room-dir`
  override outside `BatonPaths.Rooms`. `AppendAsync` is also a no-op when a line for the exact same (room
  path, project root) pair is already present, so re-registering an unchanged room on every pump call no
  longer grows the file — a genuine project-root change for the same room path still appends, preserving
  the last-writer-wins fold below.
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

**Compaction shipped (#1659), closing the paragraph above.** `RoomRegistryStore.CompactAsync` runs the
exact rewrite this paragraph used to describe as undone — fold to one line per room, drop entries
whose directory no longer exists, replace the file under the same `Mutex` every other access takes —
and `baton rooms prune` (below) calls it unconditionally, on every invocation, independent of its own
`--terminal` batch-delete filter. `PreviewCompactionAsync` is the read-only counterpart `--dry-run`
(the default, without `--yes`) calls instead, so the listing's reported counts never come from a write
the dry-run promised not to make.

**Deletion is the only path that removes a room (#1659).** Operator ruling, 2026-09-02: "we definitely
need a way to actually delete stuff, not just hide it from the glass." Fleet Glass's dismiss (§6) is a
per-browser `localStorage` hide — the room directory, its registry lines here, and its pushed
deliverables all persist regardless, reappearing in any other browser and in every `fleet_status`
payload. `baton room delete <room-dir>` and its batch form `baton rooms prune --terminal` are the only
verbs that actually remove a room: the directory, every matching registry line (`RemoveByRoomPathAsync`),
and — best-effort, since the CLI has no reach into the Cloudflare Worker's KV deliverables index
(`tools/fleet-glass/worker.js`'s `/deliver` route accepts no removal verb today) — a
`deleted-rooms.jsonl` tombstone (`DeletedRoomsTombstoneStore`) for the pusher to eventually forward as
a removal, unbuilt as of this paragraph. Both verbs refuse a non-terminal room (no `terminal.json`)
unless `--force`, since a live engine may still hold the room's files open — the same holder-liveness
read (`ConcurrencyGuard.ReadHolderInfo` + `EngineLivenessProbe`) `baton cancel` already uses, never a
second mechanism. `RoomRetentionSweep` (§7) may call the batch form automatically, gated behind
`DaemonSettings.RoomsRetentionDays` (default `null`, i.e. off — the ruling's "operator opts in"). A
retention prune with no `--state` filter deletes `Indeterminate` rooms too — the operator who opts
into `RoomsRetentionDays` accepts that, and `--state Indeterminate` selects them explicitly (or any
other `--state` value excludes them) if that default is unwanted.

**Standing conductor room and `baton deliver` (#1669).** A standing orchestrator room under `{BATON_HOME}/rooms/conductor/` (`role: conductor` in its `bindings.json` stub) holds deliverables authored directly by an orchestrator rather than a worker subprocess. `baton deliver <file> [--title <text>] [--room <room-dir>]` (`--room-dir` also accepted as an alias for `--room`) copies the file to `<room>/artifacts/conductor/<hash-of-source-path>-<basename>` — the destination filename, hashed off the absolute source path rather than the basename alone so two sources sharing a basename never collide on one on-disk file — and appends/replaces an entry in `<room>/artifacts/conductor/manifest.jsonl` keyed on the absolute `source_path` (`title`, `source_path`, `delivered_at`, `sha256`, `artifact_file`). The manifest is encoded as UTF-8 without BOM; readers tolerate a BOM. Re-delivery replaces the entry and updates the file in place. `pusher.py` reads the destination filename from the manifest's `artifact_file` field, never re-deriving it from the basename. The conductor room is never terminal (has no `terminal.json`), is explicitly excluded from `rooms prune --terminal` candidate discovery, from `room delete` (including `--force`), and from the stall detector — one shared check (`ConductorRoomDetector`, `src/Baton.Cli/ConductorRoomDetector.cs`) decides role for all three call sites, the same resolution `fleet_status` already used, so the definition cannot drift between them. `fleet_status` carries the conductor room's `artifacts_path` so it is visible in the Fleet Glass fleet tab with copyable text, and `pusher.py` scans `manifest.jsonl` to push items to `/deliver` with `kind: conductor` and upsert identity on `source_path`, surfacing them in the Glass inbox with a `CONDUCTOR` chip (newest first). The Fleet Glass conductor card renders a `deliverables →` link filtered to the conductor room along with the count of conductor items in the inbox index (#1677).

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
that measurement, stated rather than assumed (#1456 second reader), are now measured and recorded —
full rows, method, and wording in `docs/vendor-capabilities.md`'s "Subcommand granularity and
command-line matching extent (#1461)" subsection, cited rather than restated here. Both came back
against the read-only assertion resting on `--allowedTools` alone: an unlisted read-classified `git`
subcommand is not denied by the pattern at all (claude's own command-risk classification is what
gates it, not the grant — an unlisted *mutating* subcommand is still denied, so the practical split
holds today but not for the reason previously assumed), and the pattern is matched against the whole
command line, so a chained/piped command riding an allowed prefix does execute when it does not
itself create or modify a local file. The deny list above enumerates every known-mutating `git`/`gh`
subcommand family explicitly rather than relying on allowlist-omission alone, and the read-only
assertion rests on that explicit deny-subset plus claude's own read/mutate classification — not on
`--allowedTools` excluding unlisted reads, which it does not do. `review`'s grant relies on exactly
that: only the enumerated `Bash(git …*)`/`Bash(gh …*)` patterns are pre-approved (no bare `Bash`), and
the deny-subset above is belt-and-braces on top. What the #1461 measurement actually leaves standing
against a chained command is a *separate*, unconditional claude guard against local file writes — not
`--allowedTools`/`--disallowedTools`, whose behavior against a denied subcommand riding a chain is
unmeasured and, given the whole-command-line matching above, plausibly weaker rather than stronger.
So a non-file-mutating command chained after an allowed prefix (`git diff; echo …`) would have
executed under `--allowedTools`/`--disallowedTools` alone — the deny-subset was not, on that
evidence, what bounded chaining.

**#1459 closed that hole with a hook-side second layer, wired onto the same `PreToolUse` channel
`HookCheckCommand` already runs (#543, #649).** `ClaudeWorkerAdapter` now sets
`BATON_HOOK_SHELL_PATTERNS`/`BATON_HOOK_DENIED_SHELL_PATTERNS` — declared since #659, left unset
until now (the issue's own "dead code" finding). For a `Bash` call under a scoped grant, the hook
itself now parses the command claude actually received rather than trusting claude's own whole-line
match: see `ShellCommandPatternMatcher.EvaluateChainedCommand`'s doc comment for the exact
segmentation rule and its fail-closed set, and `docs/vendor-capabilities.md`'s #1461 subsection for
the two measured rows this closes. Both rows are regression arms in `ShellCommandPatternMatcherTests`
and `HookCheckCommandTests`.

**#1459's own PR (#1506) shipped that layer wired to only one of the two ways a shell gets scoped —
fixed in the same issue, from #1506's adversarial security review.** `ClaudeWorkerAdapter.Resolve`
derived `BATON_HOOK_SHELL_PATTERNS` exclusively from a structured `PermissionGrant`; a binding
scoping its shell through the raw `PermissionScope` escape hatch instead (`PermissionScope:
"Write,Bash(git diff*)"`, `PermissionGrant: null` — the bindings editor's "Advanced" string field)
fed `Bash(git diff*)` to `--allowedTools` as before, but the hook channel came out tagged-and-empty
(`AgyWorkerAdapter.BuildShellPatterns(null)` is empty), which `HookCheckCommand.Decide` reads as the
deliberate unscoped-shell no-op — so the #1461 chaining escape (`git diff; echo escaped`) still ran
under a raw-scope dispatch, unblocked, exactly as before this section's fix. `Resolve` now derives the
channel from whichever string actually reaches `--allowedTools` — the translated `PermissionGrant`
when one exists, otherwise `Bash(<pattern>)` clauses parsed directly out of the raw `PermissionScope`
(`ClaudeWorkerAdapter.BuildShellPatternsFromRawScope`) — so both paths populate the channel from one
source and cannot drift apart. A bare `Bash` clause (no pattern) still yields an empty channel, same
deliberate unscoped-shell reading as the structured path's empty pattern list. The raw path still
carries no denied-pattern concept (it feeds `--allowedTools` alone), so
`BATON_HOOK_DENIED_SHELL_PATTERNS` stays empty there — not a gap, since the allow-list-and-segment
check above already denies anything not explicitly allowed. With this fix, **both** ways of scoping a
claude worker's shell — the structured `PermissionGrant` and the raw `PermissionScope` string — now
populate the second enforcement layer; the opening sentence's "closed that hole" is accurate against
that full population as of this fix, not only the structured path #1459's original PR measured against.

**#1506's re-review found a second way to reach that same tagged-and-empty shape — fixed in the same
issue.** The naive top-level split this section's fix used could itself be defeated by a
plausible-looking advanced scope, silently reopening the just-closed bypass. `BuildShellPatternsFromRawScope`
now parses that shape correctly and refuses (`PermissionGrantUnsupportedException` at `Resolve`) rather
than degrading to empty; its own doc comment is the canonical record of the parsing rule and what
distinguishes a genuinely-absent grant from a malformed one.

**Round 4 of that same re-review tightened the rule further, to categorically fail-closed** (a
whole-scope balance gate ahead of a fifth swallowed-grant shape, and refusing rather than honoring a
comma-list inside one clause once #1514 found that reading unmeasured against claude's own parser) —
`BuildShellPatternsFromRawScope`'s doc comment is again the canonical record; nothing here restates it.

**Round 5 found that "categorically" had a gap: a balanced fusion of a `Bash(` grant into a
clause the loop would drop.** The balance gate cannot see it, because the string balances. A fusion
gate closes it with a conservation count — `BuildShellPatternsFromRawScope`'s own "Fusion gate:"
inline comment is the canonical record of the count and what it refuses.

**Round 5's re-review found one gap left even past the fusion gate: an explicit but empty `Bash()`
clause still cleared both gates and only degenerated to the same no-op shape at the per-clause
trim.** `BuildShellPatternsFromRawScope`'s own per-clause throw is the canonical record of the
refusal and why it applies only to an explicit-but-empty grant, not to the bare-`Bash`/no-`Bash(`
no-ops above.

**One asymmetry against the denied-tools channel is worth flagging here rather than only in code:**
`HookCheckCommand.Decide` reads an absent or wrong-vendor pattern channel as an unscoped grant, not a
denial — the opposite of how it reads a missing denied-tools list (#600). See that method's own
remarks for the full reasoning; in short, `--allowedTools`/`--disallowedTools` already ran and settled
whether `Bash` is reachable at all before this check is even reached, so a hard denial on its own
absence would have broken every already-shipped unscoped shell role the moment this landed. An explicitly
unscoped grant reads the same way, matching `AgyHookCheckCommand`'s existing treatment of an empty
pattern list on that vendor. Scoped to claude for now; the evaluator lives in vendor-neutral
`Baton.Vendors` so agy can adopt it later, but agy's own `run_command` gate does not segment today.
`PermissionGrant.ShellCommandsAreReadOnly`
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

**`agy` now expresses this too, by deferring to the hook rather than refusing (#1387).**
`AgyWorkerAdapter.TryTranslatePermissionGrant` used to refuse `RunShellCommands` without
`NetworkAccess` outright, reasoning that agy has no scoped-shell-without-network flag. That reasoning
still holds for the *vendor flag* — `--dangerously-skip-permissions` is still all-or-nothing — but
#1387's second probe measured that AER's own `PreToolUse` hook (`AgyHookCheckCommand`, the same one
that already enforces the pattern allow/deny lists on the wire) narrows the `run_command` channel
correctly on six probed commands: launched under `--dangerously-skip-permissions` with
`BATON_HOOK_SHELL_PATTERNS`/`BATON_HOOK_DENIED_SHELL_PATTERNS` set to `review`'s own allow/deny
lists, a write was denied, a push was denied (the DenyAlways channel), `curl` was denied, a
non-git/gh read was denied by the same allowlist-shape mechanism as the write —
`docs/vendor-doc-audit.md`'s dated entry states the precise reason and the qualifier it carries, not
restated here — `git status`/
`git log` were allowed, and a hook deny did not cancel the run. Reads are bounded by tool grant, not
by path: `view_file` is granted whole for this role (`ReadFiles: true`), the hook only bounds a path
for the write-family tools, and `HOME`/`USERPROFILE` are not redirected for shell-granted workers, so
a granted read tool can reach the operator's real home — this is pre-existing and identical on claude
and `advise`, not something this probe measured or bounded. Unprobed: the subagent/`manage_task`
tools (denied outright rather than narrowed, #1387 review F1) and the allow/deny lists' own
prefix-collision defects fixed by #1679 (closed) — `docs/vendor-doc-audit.md`'s dated entry names the
full unprobed population, not restated here. So a grant with `RunShellCommands`, `NetworkAccess: false`, and a non-empty
`ShellCommandPatterns` now resolves to `--dangerously-skip-permissions` and lets the hook do the
narrowing; a grant with shell but no patterns still refuses, because nothing would bound it. A hook
that cannot start reads as an allow on this vendor, so for `review` specifically a broken hook widens
the role to an unscoped shell rather than merely losing narrowing — guards for that are tracked in
#1680, not built here. `review`'s tier still defaults to `claude`
(`WorkerTiers.json`'s `frontier` entry), so a default dispatch is unaffected; an operator who
overrides `--adapter agy` on `review` now starts rather than hitting
`PermissionGrantUnsupportedException` at bind time. This is the same #529 coherence rule §9 already
enforces everywhere else, applied to a grant that #1355 had previously kept flat specifically to avoid
it; #1456 shipped claude's real scoped shell first and accepted the then-open agy-side refusal as the
honest cost of not declining both vendors to keep their capability artificially identical — #1387 is
what closed that gap on the agy side, so the two vendors converge on the same grant shape rather than
staying deliberately unequal.

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
  "something remote learns what happened," not a client-pairing model. The tailnet drill-down page
  (C-11, §11) is not this: no pairing state, no client registry — that entry records the distinction
  and the narrow listener it prices back in.
- **A resident orchestrator that decides on a human's behalf.** There is no room-resident presence;
  the harness is the decider, always (§5, §7).
- **Remote *dispatch* triggering — closed, orchestrator-only.** Settled, not open: remote dispatch
  already exists as "talk to your harness from the phone" — a Claude Code mobile session (or any
  other agent that can run CLI verbs and read `terminal.json`/`fleet_status`) driving `baton dispatch`,
  which keeps one set of hands on the workers. A direct phone-to-worker control path would be a
  second interaction surface outside the orchestrator, which the one-surface design retires. C-11
  (§11) does not reopen this: the page it rules in may eventually **arrest** (cancel,
  redispatch-unchanged) but never originate — the originate/arrest line is drawn in that entry.
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

**Installation and versioning (#1645, side-by-side per-commit installs #1668).** `baton` ships as a self-built,
unpublished `dotnet tool` — README's *Installing `baton`* section owns the user-facing command index. Its release
version is one value, `Directory.Build.props`'s `<Version>` under `src/Baton.Cli`, read by `VersionInfo` at build time
(`baton --version`) and by `InstalledVersionDrift` at dispatch time; nothing else in the tree carries a second copy of it.

**Layout and launcher (#1668).** Installs sit side-by-side under `{BATON_HOME}/tools/<sha>` (one directory per commit SHA,
installed via `dotnet tool install baton --tool-path {BATON_HOME}/tools/<sha> --add-source bin/pack`), so refreshing the
tool never touches a directory a running lane loaded from. The currently active version is named by `{BATON_HOME}/tools/current`
(a one-line pointer file holding the commit SHA, written atomically via temporary file and replace). `baton` on PATH is a thin
launcher shim pair in `~/.dotnet/tools` (`baton.cmd` + `baton.ps1`, alongside a POSIX `baton` wrapper) that resolves `current` at
process start and executes that directory's `baton.exe` with the original arguments and exit code. A missing, empty, or garbled
pointer fails closed with exit code 1, printing an error naming `pixi run tool-refresh`.

**Pruning (#1668).** After a successful pointer flip, `tool-refresh` prunes `{BATON_HOME}/tools/<sha>` directories beyond the
newest 3 that no live room references. A room is live when it has no `terminal.json`; dispatch records `ToolSha` in each room's
`bindings.json` so the pruner preserves any directory a running lane was dispatched from even if it falls outside the top 3. A
live room with no recorded `ToolSha` protects nothing under this check — for such a room the newest-3 cushion is the only guard
against pruning the directory it actually runs from.

**Tool refresh.** Refreshing is `pixi run tool-refresh` (`tools/tool-refresh/refresh.py`): packs the checkout, installs into the
new `{BATON_HOME}/tools/<sha>`, verifies `--version` and `templates --json` directly from that directory's binary, flips `current`
atomically, installs/updates the launcher (uninstalling any legacy global tool in `~/.dotnet/tools` to prevent executable
collision), rebuilds `src/Baton.Cli` Debug for the Fleet Glass pusher, restarts the `fleet-glass-pusher` scheduled task, and prunes
old unreferenced tool directories. It requires no drain wait and writes no drain marker.

**Manual drain marker.** Draining is retained solely as an operator-invoked stop: an explicit `{BATON_HOME}/draining.json`
marker causes `baton dispatch`, `baton redispatch`, and `baton resume` to refuse with `ValidationRefused` (2) fail-closed;
`pixi run tool-refresh --abort` clears it. `InstalledVersionDrift` continues to warn on stderr when the installed version is behind
a discoverable checkout.

### C-11 — The tailnet drill-down plane (glass v2.5)

Ratified with the operator 2026-08-31, out of the glass v2 design session (#1502). This entry is the
record §6's own tripwire demanded before any page could be built — written first, as the epic
requires, and written honestly: the thing being ruled in **is** a maintained page, and this entry
amends the ruling's reach rather than pretending the page slips under it.

**The decision.** Observability splits into two planes by what the bytes are, not by preference. The
**mailbox plane** (pusher → Worker KV/MCP → artifact, §6) owns the fleet row: small, curated,
change-gated, secret-gated, reachable from a Claude conversation, working while the machine sleeps.
The **tailnet plane** — a page served by the existing daemon (§7), bound to the tailnet/loopback
interface only, never `0.0.0.0` — owns drill-down: live stdout tail, full timeline, room artifacts.
Neither is a fallback for the other.

**Why the mailbox cannot carry drill-down — the constraint that forced a second plane.** Two hard
walls, not taste. The secret gate: the deliverables path exists to guarantee the mailbox never
carries `prompt.txt` or `.stdout.log` — only declared outputs through a fail-closed denylist — and a
live stdout tail is precisely the uncurated stream that design refuses, on a public repo. The write
quota: Cloudflare's free KV tier caps at 1,000 writes/day; a live tail at the pusher's cadence is
~3,456/day — the #1457 change-gate exists because even the *fleet row* brushes this ceiling. On the
operator's own tailnet both walls vanish: the bytes never leave the network, and no third-party
quota is in the path.

**What §6's "never a second application" still governs, and what it no longer does.** That ruling
stands, un-softened, for the mailbox plane: drill-down reachable from a conversation is `room_detail`
in the same MCP host, and no page grows there. This entry rules in exactly one additional surface —
a read-only diagnostic page on the private plane — because the mailbox physically cannot carry its
payload. The tripwire this entry inherits from §6 is restated for the new plane: the page is a
**diagnostic**, not an application. It renders what the room record already says; v2.5 ships it
read-only. The only interactions it may ever gain are the two **arresting** reflexes — cancel and
redispatch-unchanged — behind confirm, executed through the same engine verbs as any terminal and
recorded as room facts, so every observer sees the transition through the room record. The
conductor/orchestrator remains the only **originator** of work: dispatch-new-lane, amended re-briefs,
and gate approvals stay closed from the page (§10's remote-dispatch ruling, unamended). If the page
grows an origination affordance, this entry has been violated, not extended.

**Why this is not the pairing infrastructure §10 archived.** `PairedClientsStore`, the WebSocket
broadcast, and the tsnet sidecar existed to give a *paired remote client* a registry, reassignment,
and zero-config reach. A bookmark on a tailnet holds no pairing state, has no client registry, and
needs no reassignment — the network is the authenticator. What #1420 deleted is not what this entry
adds; what it adds back is one HTTP listener on the daemon, priced and narrowed to this purpose.

**Transport: SSE out, plain HTTP `POST` for the eventual arrest verbs — WebSocket considered and
rejected.** The live view is one-directional; `EventSource` gives reconnect and `Last-Event-ID`
resume for free, which matters because the primary client is a phone that sleeps constantly. The
arrest verbs, when they arrive, are rare, discrete, and want request/response semantics — a status
code, per-request auth, a log line — not a frame on a stream; routing them over `POST` is the better
design even where a socket already exists. A bidirectional channel earns its machinery only under
chatty two-way traffic, and the steering model settled alongside this entry (arrest + rehire;
corrections travel as briefs through `redispatch --spec`, #1495/#1381) guarantees there is none.
Revisit only if a genuinely interactive surface is ever ruled in — which §10's mid-run-steering
ruling currently forbids.

### C-12 — Gate receipts: one passing run per tree, CI is the independent one

Measured 2026-09-01: `.githooks/pre-push` ran `gates-fast` under the shared build lock
(`tools/buildlock.py`) on every push, even seconds after a dispatched lane had already run
`gates`/`gates-quiet` — a strict superset — on the identical tree. With several lanes queued on the
lock, a push could sit for tens of minutes redoing work already done, and CI then ran everything a
third time regardless. `tools/gates/gates.py` now writes a receipt (`<git-dir>/baton-gate-receipt`,
one per worktree) on every PASS, recording the tree hash, a hash of the uncommitted diff, which mode
passed, and a timestamp; a FAIL deletes it. The pre-push hook (`pixi run gates-check-receipt`) skips
its own run only when the receipt's tree hash and dirty-hash still match `HEAD^{tree}` and it is
under six hours old — any mismatch falls through to a real `gates-fast` run. This narrows what the
hook re-verifies, not what CI verifies: CI remains the one platform-independent run and is never
skipped by a local receipt.

**Scope, stated plainly: tracked content only.** The dirty-hash is `git diff HEAD`, which does not
see untracked files. A tree that was already dirty when its receipt was written, and then gains an
untracked file before the next push, still matches -- the receipt does not re-verify content `git
diff HEAD` cannot see. A clean tree gaining any file `git status --porcelain` reports (tracked or
untracked) is still caught, because that flips the dirty bool itself -- a `.gitignore`d file is not
reported by `--porcelain` either, so it is not caught by that path or any other.

**Measured 2026-09-02 (#1648):** git exports `GIT_DIR`/`GIT_INDEX_FILE`/etc. to every hook, and
`gates.py`'s own selftest fixture spawned `git init` in a temp dir without scrubbing them, so a
push under `.githooks/pre-push` re-initialized the pushing repo itself instead of the fixture's
temp dir -- `.githooks/pre-push` now `unset`s the `GIT_*` keys before invoking anything, and
`gates.py` scrubs them from its own process environment and passes an explicit scrubbed `env=` to
every git subprocess its fixtures spawn.

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

