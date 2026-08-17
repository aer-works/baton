# Coverage audit — what the corpus holds, and where each item went

Repointed to the consolidation umbrellas 2026-08-13 (#1149).

**Purpose: nothing in the corpus may disappear silently.** Every settled call below either names its
destination or is listed as a deliberate drop with a reason. This is the check that was missing when
the decision records were first written from these artifacts — seven records were produced, reviewed
for quality, and never compared against their source.

Status column: **✅ landed** (in the repo now) · **→ routed** (has a destination, not yet written) ·
**⛔ dropped** (deliberate, reason given).

Coverage was tested mechanically — each call grepped against `docs/decisions/`, `spec/journeys.md`
and `docs/plan.md` — not judged by reading. **16 of the first 18 tested were absent.**

## How items are routed

| Kind of item | Destination | Why |
|---|---|---|
| A **rule** that constrains future work | numbered decision record | it is a choice with alternatives and a cost |
| A **promise** to a person | `spec/journeys.md` + registry | it is demonstrable end to end |
| A **screen** or affordance | the UI spec replacement (`#474`) | it describes a surface |
| A **feature** or fix | GitHub issue | it is a unit of work |

A single call can land in two places — the rule in a record, the demonstration as a journey. That is
correct, not duplication: the record says *why*, the journey says *what you get*.

## 1 · The centrepiece

| Call | Source | Status |
|---|---|---|
| **Consulting is not deciding.** Put a question to anyone — including a worker not yet in the room — and it joins to answer. The gate stays open the whole time; only you close it. Asking three still decides nothing. | 03, 07 | ✅ **landed** — [0019](../decisions/0019-consulting-is-not-deciding.md). The corpus calls this *"the single most important behaviour in the room model"* and *"if only one thing survives contact with implementation, it should be this."* |
| **Routing is a control.** You choose who answers and what they see. The product never reads the conversation to decide who should respond. | 03 | ✅ **landed** — 0019 §4. CLAUDE.md Architecture Rule 1 surfacing in the UI, and what keeps cross-examination from becoming inference. |
| **Adding a worker = asking a question.** No separate participant-management surface. | 03 | ✅ **landed** — 0019 §1. |
| **What a newly-added worker sees:** a room summary + the raising turn and its attachments verbatim + the ability to query for more (`#424`). Always disclosed before sending, every item removable. | 06 | ✅ **landed** — 0019 §3; the disclosure *UI* still routes to `#474`. Re-confirmed: `#494` ("a gate you can ask anyone about") closed as *grouped* into umbrella `#751`, not shipped — no consult-gate UI (`AskAnyone`, `ConsultCommand`, or similar) exists anywhere in `src/`. |

## 2 · Permissions

**Re-measured — this section was stale in the dangerous direction.** All four rows were marked
absent/routed; all four have code behind them, landed under decision
[0022](../decisions/0022-permission-ladder-and-denial-is-an-answer.md) (accepted 2026-07-24, amends
[0004](../decisions/0004-permission-scopes.md)) — a record this section never cited before.

| Call | Source | Status |
|---|---|---|
| **The scope ladder** — allow once / this command here / anything here — offered **at the moment of asking**, never buried in settings. | 04, 07 | ✅ **landed** — [0022](../decisions/0022-permission-ladder-and-denial-is-an-answer.md); cross-room rung held by [0052](../decisions/0052-the-ladder-ships-without-the-cross-room-rung.md). Desktop: `src/Aer.Ui/Views/ChatView.axaml:237-262` renders the ladder inline in the transcript (own comment: *"the scope ladder stays VISIBLE here rather than buried in settings"*). Mobile: `src/Aer.Mobile/lib/chat_screen.dart:1373-1452` (`PermissionGateCard`). Both surfaces offer every rung except the cross-room one, which 0052 deliberately withholds pending a project-scoped store. |
| **Denial is an answer.** A refused worker is told and continues — it does not silently retry and does not die. | 04, 07 | ✅ **landed** — 0022 §3. `src/Aer.Mcp.Host/PermissionGateTool.cs:163-224` (`BuildAnswerResult`): a denial returns a non-error `behavior: deny` result to `claude`, or an error-flagged tool result carrying the reason to `agy` — either way the vendor CLI's own turn loop receives it and continues. No session-kill, no retry path. |
| **A pending permission dies with its turn**, everywhere at once, and the transcript says why. | 06 | ✅ **landed** — 0022 §5. `src/Aer.Daemon/Program.cs:2888-2899` revokes every pending gate for the room, unconditionally, in the turn's `finally` block (`executionIdFilter: null`; its own comment cites *"0022 §5"*). The transcript says why: `src/Aer.Flow/Projection/RoomProjector.cs:232-239` turns the revocation into `PermissionAnswer(..., revoked.Reason, ..., WasRevoked: true)`, rendered by `ChatViewModel.cs:882-891` as *"Expired unanswered — turn ended."* |
| **A queued message does not send into a blocked worker.** The queue waits on the whole turn, permission included, and says what it is waiting for. "Send now" interrupts — which here means denying the permission — and the control says so. | 06 | ◐ **partial.** The wait-half is landed: `src/Aer.Ui.Core/ChatViewModel.cs:558` (`CanDrainQueue`) and `:570` (`SendJoinsQueue`) both gate on `HasPendingPermission`, wired to the real send/drain paths at `src/Aer.Ui/MainWindow.axaml.cs:1064` and `:1565` (#1167). The row's other promise — **"Send now" interrupts a blocked worker by denying the permission** — is absent: no `Cancel`/`Abort`/interrupt-turn affordance exists anywhere in `ChatViewModel.cs` or `Aer.Daemon`; a message typed while a gate is open only enqueues. That half stays routed to `#474`/`#751`. |

## 3 · State, errors and freshness

| Call | Source | Status |
|---|---|---|
| **One state machine.** Every surface renders the room's state; none derives its own. | 02 | ✅ **landed** — [0020](../decisions/0020-one-state-machine.md), enforced by the single derivation `RoomCardViewModel.DeriveStatus` (`src/Aer.Ui.Core/HomeViewModel.cs:167`). The task headline (`RoomStepViewModels.cs:114`) and the room switcher/fleet row (`RoomsViewModel.cs:633`) both delegate to it rather than deriving their own status; `StatusDerivationTests.cs:79` (`The_task_headline_and_the_home_card_are_one_derivation`) pins the parity so the shell cannot disagree with itself about "running." |
| **Errors are content.** A failure shows what broke, in the room, with the worker that failed right there to be asked about it. Not a status word with the reason behind a drill-in. | 02, 03 | ✅ **landed** — the rule rides [0020](../decisions/0020-one-state-machine.md); the surface is `#482` (`RoomView.axaml:131-165`'s failed-step banner shows the error inline with **Try again** / **Ask ⟨worker⟩ to fix it** / **Show full output**) + `#404` (the per-step drill-in). "Ask the worker" is a real path, not a viewer: `MainWindowViewModel.cs:362-390` switches to Chat, selects the failed step's own adapter, and drafts `"Step '{stepId}' failed: {reason}"` into the input. |
| **Stale, not blank.** Refreshing never empties a list; previous content stays and is marked stale. | 03 | ◐ **partial** — weaker than previously stated. The *not-blank* half is landed: `RoomsViewModel.RefreshAsync` (`src/Aer.Ui.Core/RoomsViewModel.cs:90-138`) only clears `Items` after a successful fetch, leaving prior items untouched on failure; mobile mirrors it (`rooms_screen.dart:118-144`). The *marked stale* half [0018](../decisions/0018-attention-is-the-primary-signal.md) actually describes — content visibly flagged as unable to vouch for its own currency — has no code behind it: no `IsStale`, `LastHeard`, or freshness timestamp exists on either surface. What survives a failed refresh is old content plus a generic error banner, not stale-marked content. |
| **Success collapses, failure opens.** A passing command shows one line; a failing one opens itself. Status and duration stay visible either way. | 03, 07 | **◐ partial** — `#267` (closed as grouped; tracked on umbrella `#750`). The **markdown/code rendering** half is landing per surface, governed by [0051](../decisions/0051-markdown-rendering-is-a-defined-subset-parsed-per-platform.md): **desktop** via `#1076` (Markdig → a token-set renderer in the chat transcript; no remote content), **mobile** via `#1080` (`flutter_markdown_plus`, same subset, no remote content). The failure-opens half exists as the `#482` failed-step banner; the **success-collapse** half is still absent — confirmed: no `IsExpanded`/status-conditional collapse exists anywhere in `Aer.Ui`/`Aer.Mobile`, only an unconditional `Expander` for record detail (`RoomView.axaml:268`). |
| **Readiness up front.** Which vendor CLIs were detected, at first run and in Settings — the most likely first failure is the least self-evident. | 02 | ✅ **landed** — `#478`. `VendorReadinessLines` (`src/Aer.Ui.Core/NewWorkflowViewModel.cs:40`, populated by `RefreshVendorReadiness` at `:145` from a real probe, `VendorCliPresence.Probe`, not stubbed) is bound at first run (`HomeView.axaml:69`, `#1071`), in Settings (`SettingsView.axaml:20`, `#1069`), and in `AuthorView.axaml:26`. |

## 4 · Workers, models, effort

| Call | Source | Status |
|---|---|---|
| Vendor / model / effort are three choices, all on the chip | 04 | ◐ **partial**, and the split has moved — re-measured 2026-08-17, correcting *"effort renders on no chip anywhere"*, which `#1318` falsified the day it merged. On the **workflow-room** chip all three now render: the vendor word plus depth and effort as marks (`RoomView.axaml:107-121`, `TierMeter` fed by `StepItemViewModel.DepthTier`/`EffortTier`), per [0058](../decisions/0058-model-and-effort-are-marks-not-words.md)'s scope ruling. What remains absent is the **session** chip — `ChatHeaderView.axaml:70-72`'s `Chat.WorkerChipText`/`Chat.WorkerModelText` still show vendor and model only, so the two chip surfaces now disagree with each other. That gap, not the original one, is what a follow-up must close. |
| **Effort is named by behaviour** — quick / standard / careful / exhaustive — never a token budget or a vendor's flag name. | 04, 05 | **→** the rule is now **landed** as [0023](../decisions/0023-effort-and-models-are-named-by-behaviour.md) (accepted 2026-07-24; `#472` confirmed both CLIs expose a real `--effort`), with [0058](../decisions/0058-model-and-effort-are-marks-not-words.md) layering presentation (achromatic marks, not words) on top. The mapping is measured and registered (`docs/vendor-capabilities.md`, `vendor-verify` sentinels). ◐ **partial** — re-measured 2026-08-17, correcting *"no `quick`/`standard`/`careful`/`exhaustive` string exists in `src/`"*. The canonical words are now the engine's own: `EffortTierMapping` translates canonical → each vendor's raw flag **at dispatch** and fails closed on an incoherent pairing, and `EffortTierParsing` is the UI's single canonical-word map, deliberately failing to parse a raw vendor value so the chip renders absence rather than a guess. The mark reaches the workflow-room chip (`#1318`). Still absent: the **picker** — nothing lets a person choose an effort by these words (`#498`'s remit, closed noting it "remains unbuilt and unsliced"), so the vocabulary is spoken by the engine and read by the chip, but never offered. |
| **Models are offered by purpose** — deep / balanced / fast. Nobody should need this month's model string. | 04 | **→** the rule is the same [0023](../decisions/0023-effort-and-models-are-named-by-behaviour.md); [0058](../decisions/0058-model-and-effort-are-marks-not-words.md) assigns it to the chip's depth mark. ◐ **partial** — re-measured 2026-08-17, correcting *"the mapping itself is not yet measured"*. `#1339` registered it: `docs/vendor-capabilities.md`'s "canonical model-purpose mapping" section carries the claude column (`opus`→deep, `sonnet`→balanced, `haiku`→fast), `DepthTierMapping.TryResolve` reads it, and the depth mark renders from it on the workflow-room chip. Two honest gaps, both deliberate: **agy's column is still unmeasured** and reads *"not recorded"* rather than a guess (`#1342` — it needs a live run against that vendor's catalogue), and `DepthTierMapping` therefore has **no fallback tier** for agy, so its chip renders absence. The session chip still shows the raw model string (see the vendor/model/effort row above). |
| Two workers may share a vendor at different models/efforts — a normal room, not an edge case | 04, 07 | ◐ **partial**, not fully landed as previously stated. The object model is real and shipped: `Participant` (`src/Aer.Flow/Domain/Participant.cs:12-18`) carries per-participant `Vendor`/`Model`/`Effort`, and `ParticipantNaming.NextName` already produces `"claude-2"` for a second same-vendor worker (`#493`, closed 2026-08-16 — the object-model blocker this row previously cited). But nothing can *reach* it yet: no add-worker/add-participant endpoint exists in `Aer.Daemon`, and the UI says so directly — `ChatHeaderView.axaml:45-46`, *"'+ Add worker' and multi-worker chips are M27"* (current as of `#1224`, 2026-08-15). A two-worker room is structurally supported, not yet buildable by a person. |
| **Context is per worker**, and running out is offered as a choice before it becomes an event. | 04 | **→** the rule is **landed** — [0011](../decisions/0011-token-based-context-management.md), with [0027](../decisions/0027-context-is-per-worker.md) as its code-level grounding (neither was previously cited here). `SessionMetadata` still carries one flat `TurnCount`/`SafetyCeiling` with no participant dimension; `CompactSessionAsync` (`RoomClient.Sessions.cs:218`) is a manual, after-the-fact `/compact`, not proactive pressure detection or an offered choice. **→ `#395`** for the implementation — scope extended: per-worker headroom, and the choice offered before the event. |
| **Limits, not dollars.** Spend shown against the subscription's own limits. | 04 | **→ `#751`** (umbrella). The unit this product actually runs on. What each CLI reports about quota is still unprobed. Do not conflate with `RoomTurnHostBannerViewModel` (`RoomStepViewModels.cs:393-408`), which already ships a *different* meter — AER's own self-imposed automation throttle, "machine turns N/cap this hour," sourced from `turn-throttles.json` — not the vendor subscription's own limit. |

## 5 · Commands and skills

**Re-measured.** [0024](../decisions/0024-commands-are-namespaced.md) (accepted 2026-07-24, amends
[0010](../decisions/0010-skills-and-advisor.md)) already states the rule for the first four rows below
in full; this section previously routed them only as "extends 0010," which understated how settled the
rule already is. Implementation status is unchanged — still absent, confirmed by journey **J18**
(`Coverage.Pending`, `tests/Aer.Journeys.Tests/Journeys.cs:219-222`, own comment: *"no command palette,
no namespacing and no multi-worker room to broadcast into yet"*).

| Call | Source | Status |
|---|---|---|
| **Commands are namespaced** — room commands, then each vendor's own. Resolves an ambiguity single-agent tools never face. | 04 | **→** rule landed as [0024](../decisions/0024-commands-are-namespaced.md). No namespace/prefix logic exists anywhere in `src/`. |
| **`/ask-all`** — one question to every worker, answers side by side. | 04, 07 | **→ same** — 0024 §5, plus journey **J18** in `spec/journeys.md` (already exists, `Status: Fails`). Absent in code. |
| **No slash palette on a phone** — the same commands become an Actions sheet from the room header. | 04 | **→ `#474`.** 0024 §6 is the rule. `chat_screen.dart:577` has a generic bottom sheet, not a namespaced Actions sheet fed from a shared command set — there is no shared set yet (see row above). |
| **Canonical skills under Room, native skills under their vendor, marked as such.** | 06 | **→ extends 0010** / `#386` (under `#758`), rule now stated in full by 0024 §3-4. Only the vendor-native tier is built (`IWorkerAdapter.cs:11-24`'s `WorkerCapabilityItem`, `ClaudeWorkerAdapter.cs:918-946` reading `.claude/skills`); no Room tier and no "marked as such" UI exist — 0024 itself states this in future tense ("needs the Room tier populated"). |
| **An advisor is a preset, not a new noun. Roles are instructions.** | 06 | **→ `#385`** (under `#758`), whose body still proposes "a first-class advisor participant" — in tension with both 0010's preset framing and [0033](../decisions/0033-skills-attach-directly-no-persona.md) (accepted 2026-07-26), which separately killed a named "Advisor" preset object outright. No advisor code of either shape exists: `Participant` (`Aer.Flow/Domain/Participant.cs:12-18`) has no `Kind` field, and no participant-kind enum exists anywhere. |

## 6 · Rooms, files and shapes

| Call | Source | Status |
|---|---|---|
| Room owns memory; shared across vendors; proposed, never inferred | 06 | ✅ **landed** — [0016](../decisions/0016-memory-is-room-owned.md). `src/Aer.Mcp.Host/MemoryProposalTool.cs:13-16`: *"This tool never writes `memory/`... it merely proposes"* — room-scoped (`MemoryProposalResolution.cs:57`). |
| Shapes are an ordered list that renders as a graph | 02 | ✅ **landed** — [0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md). `AuthorView.axaml`'s advanced editor: an ordered `ItemsControl` (`StepsEditorList`, line 156) plus a re-laid-out `TemplateEditorDagCanvas` (lines 224-226). |
| **"Ask me first" is a property of a step, not a node type** — one switch is the entire mental model for human oversight. | 02 | ✅ **landed**, corrected from "extends 0014" — the rule was written as [0025](../decisions/0025-a-step-is-an-instruction-with-a-gate-toggle.md) (accepted 2026-07-24, amends 0014), and it is implemented. The guided authoring flow (`AuthorView.axaml`'s primary path, M19 Phase 4) has one per-step `CheckBox Content="Review gate" IsChecked="{Binding HasReviewGate}"` (line 58), wired through `WorkflowTemplateComposer.cs:122-125` (`AskFirst` → `PausePoint`). The advanced/legacy editor carries the same toggle as `HasPausePoint` (line 192). |
| **A step's instruction is its body**; previous output flows in implicitly; no template language. | 06 | ✅ **landed**, same correction — [0025](../decisions/0025-a-step-is-an-instruction-with-a-gate-toggle.md) §1-2. The guided flow's per-step `Prompt` textbox (`AuthorView.axaml:77-78`, placeholder *"What should this step's runner do?"*) is the step's body; `WorkflowTemplateComposer.cs:118` (`Inputs: blockerOutputs, // the single blocker's output flows in (0025)`) confirms implicit flow, with no interpolation syntax anywhere in `Aer.Ui.Core`. Edit-time validation exists (`NewWorkflowViewModel.cs:418-420`, rejects an empty prompt at edit time). The advanced/legacy editor (`StepEditorViewModel`) has no instruction field of its own — the guided flow is where 0025 is realized. |
| **Artifacts are files** — vendor-neutral, versioned, attributed, explicitly attached, diffable between vendors. | 03, 07 | **→** the rule is now **landed** as [0021](../decisions/0021-artifacts-are-files.md) (accepted 2026-07-24 — this row previously said "new decision record," not yet written; it has been). ◐ **partial** — re-measured 2026-08-17, correcting *"implementation is absent... no issue tracks building the model itself"*. `#1344` shipped the model as a **projection** over facts already durable — `RoomFiles`/`RoomFile`/`FileVersion`, built by `RoomFilesProjector` from the worker each `ExecutionRequest` already recorded, so existing rooms got versioned attributed files with no migration. Identity is the file **name** within the room, matching how the engine already chains a handover; versions order by event-log position, not timestamp, because `WriterUtcTimestamp` is nullable and a missing one renders as absent rather than invented. Desktop has the Files list. **Still absent**, and now routed as slices 3–8 on `#1340`: the wire and mobile, version diffing (`#377`'s scope, which never had a model under it until now), explicit attachment, session-write capture, and save-into-project. |
| **Saving a working document into the project: diff-and-choose, never overwrite by default**, and flag divergence since derivation. | 06 | **→ same record**, now [0021](../decisions/0021-artifacts-are-files.md) §3. Absent in code — no `diff-and-choose`/save-conflict UI anywhere in `src/`. |
| **Documents stay, plumbing goes.** One file list; the only distinction is "in your project" or not. Execution directories are never surfaced. | 04 | **→ same record**, now [0021](../decisions/0021-artifacts-are-files.md) §2. ◐ **partial** — re-measured 2026-08-17, correcting *"absent"*. The **one file list** now exists: `#1344`'s desktop Files section is per-room rather than per-execution, and its rows carry author and version instead of the execution ids §2 forbids. The **plumbing** half was closed by `#1347`, which found — by driving the app, not by a test — that `.stdout.log` was being presented as a first-class deliverable on every surface; stream logs are now filtered at `ArtifactLineageProjector`, the single point where an execution's output directory is read. **Still absent:** the distinction §2 actually names — *"in your project" or not* — which nothing in `src/` draws, and which is only meaningful once save-into-project (slice 7 of `#1340`) exists to put a file there. |
| **Child rooms** nest in the list and report back as a turn; the parent never blocks. | 06 | **→ `#340`** (closed as grouped; tracked on umbrella `#756`) — scope extended with the non-blocking parent, and its interaction with `#480`'s directory lock. Re-confirmed absent: no "child room"/"derived session" mechanism exists anywhere in `src/`. |
| **Two rooms on one folder**: serialised already (verified — the turn lock is keyed on directory path). Surface the wait, name the holder, warn on a duplicate room. | 06 | ◐ **partial**, corrected from "the surfacing is absent." `#480` (under `#752`), engine behaviour verified and correct. The surfacing has since landed: `WaitingOnLockBanner` (`RoomView.axaml:29-45`, comment citing `#1299 (#480)`) names a holder and offers **Try again** — but `HolderText` (`RoomStepViewModels.cs:252-260`) traces to `ConcurrencyGuard.DefaultHolderDescription()`, a *process* description (`"{processName} (pid {pid})"`), not a room name, matching this row's own caveat about the lock growing a room-name field. No "warn on a duplicate room" affordance found. |
| **Gates render inline**, in the conversation that produced them, reachable from a "needs you" filter and the phone. The separate decision surface goes away. | 02 | Inline gates + phone entry point **→ `#474`/`#751`**. Desktop "needs you" filter: ✅ **landed** — `#1072` (a filter on the switcher; needs-you rows expand in place to their paused steps, decision 0007's middle level, and the Home decision inbox retired into it). Re-confirmed: the permission gate itself renders inline in the chat view (`MainWindow.axaml:243-246`), not a separate decision screen. |
| Rooms are the front door on both surfaces; "needs you" is a filter, not the landing screen | 02 | ✅ **landed** — 0018 + `#337`; the desktop rail is now the three icon-only destinations 02-screens draws (`#1071`), with Home folded into the ▤ rooms front door. Re-confirmed on mobile: `main.dart:91` lands a paired device directly on `RoomsScreen`, no separate needs-you landing page. |

## 7 · The nine claims and their demonstrations

The corpus states these are *"journey-shaped on purpose"* — each is a claim plus the condition under
which it counts as demonstrated. **This row was stale.** All nine are **landed as journeys** —
`spec/journeys.md` J10–J18, added by `#488` on 2026-07-24, thirty-nine minutes after this section's own
"None exists today" was written (`git blame`), and never revisited since. Each carries its own `Status`,
`Passes when`, and `Today` gap analysis, tracked by `ReconcileTests` and linked from `docs/plan.md`
("eighteen are defined"). Every one currently reads **`Status: Fails`** — the journey exists as a spec
and a registered test target; the behaviour it demonstrates does not exist yet, consistent with each
claim's own routing elsewhere in this document.

| Claim | Demonstrated when | Status |
|---|---|---|
| Cross-examination | At a live gate, a worker not previously in the room is asked, answers, contradicts the first — and the gate is still open | **J10**, `Status: Fails` |
| Two subscriptions | A room where both vendors act, on plan auth, no key configured anywhere | **J11**, `Status: Fails` |
| Shared memory | A fact established by one vendor is used by a different vendor later in the same room | **J12**, `Status: Fails` |
| Two of one vendor | Two chips, same vendor, different model and effort, both answering (multi-worker room model blocked structurally by `#493`) | **J13**, `Status: Fails` |
| Files with receipts | One document authored by one vendor and edited by another, with a diff between their versions | **J14**, `Status: Fails` |
| Work outside the UI | Quit the desktop app mid-run; answer the permission on the phone; reopen and find it continued | **J15**, `Status: Fails` |
| Scoped permissions | Grant "allow in this room", see it not asked again, find and revoke it in settings | **J16**, `Status: Fails` |
| Shapes as lists | Author a four-step template on a phone, start it on the desktop, watch it run | **J17**, `Status: Fails` |
| Ask everyone | One question, two answers side by side, disagreeing | **J18**, `Status: Fails` |

## 8 · The eight delights

Not differentiators alone; collectively the difference between a tool you tolerate and one you like.
**All eight are tracked — five shipped (y/n `#481`, `#482`, `#463`, refresh-rule, and now "Thought for
Ns"), one partial (`#462`'s never-block half [closed as grouped; tracked on umbrella `#750`], `#1074`);
the other two remain unbuilt.**

| Delight | Status |
|---|---|
| `y`/`n` for permissions, never bound to Enter, so a reflex cannot approve | **✅ both surfaces built** (`#481`/`#390`, source 2026-08): the runtime permission gate renders inline in chat with the scope ladder ([0022](../decisions/0022-permission-ladder-and-denial-is-an-answer.md)). **Desktop** — a bare `y` allows / `n` denies, never on Enter and never with a modifier (`MainWindow.PermissionAnswerFor`, `PermissionGateKeystrokeTests`); config-time `AuthorView` flags still exist alongside it. **Phone** — the same gate card (`chat_screen.dart` `PermissionGateCard`) with tap rungs (no keyboard shortcuts on mobile). Both **live-driven 2026-08-09**: the full scope ladder renders per `04`'s mockup and the cross-room rung is absent (0052); the drive exercised the **Allow once** rung, which clears the gate (a `runtimePermissionAnswered` turn is recorded) — desktop from the daemon's `/api/rooms/open` response, phone from the WS projection push. The other rungs render but were not individually clicked. Scope is the **permission** kind only; the consult/escalation kinds remain absent (`#474`/`#751`). |
| Typing never blocks; the queue is visible, with interrupt and remove | **◐ partial** — `#462` (closed as grouped; tracked on umbrella `#750`). The **never-block + visible + removable queue** half landed via **`#1074`**: the desktop composer no longer disables mid-turn; a send joins a visible, removable FIFO that drains one message per turn-completion. **Interrupt** ("Send now" cancels the running turn) is the deferred half — re-confirmed absent, no `Cancel`/`Abort`/`Stop`-turn method exists anywhere in `ChatViewModel.cs` or `Aer.Daemon`. |
| Failures offer the fix, with the worker that failed already holding the context | ✅ **shipped** — `#482` (desktop verified: `RoomView`'s failed-step banner shows the error text inline with **Try again** / **Ask ⟨worker⟩ to fix it** / **Show full output**) |
| Jump to the last decision via an event rail | **→ `#459`** (closed as grouped; tracked on umbrella `#750`) — **unbuilt**, re-confirmed: no event/decision rail exists anywhere in `src/Aer.Ui`; the only "rail" hits are the app's unrelated left navigation rail. |
| Status readable without colour | ✅ **shipped** — `#463` |
| *"Thought for 12s"* reported after the fact, never a live counter | ✅ **landed** — corrected from "unbuilt (`#483`)." `ChatViewModel.cs:128-141` (`ThinkingTimeText`, set once at turn completion, gated on a 10s threshold, its own comment citing `#483` and decisions 0018/0006 Quiet) rendered at `ChatView.axaml:39-41` (`IsVisible="{Binding Chat.HasThinkingTimeText}"`). |
| Success collapses, failure opens | **→ `#267`** (closed as grouped; tracked on umbrella `#750`). The **markdown/code-rendering** half of `#267` is landing per surface (desktop `#1076`, [0051](../decisions/0051-markdown-rendering-is-a-defined-subset-parsed-per-platform.md)) — canonical status in the §7 row above. The two halves this row names: failure-open exists via `#482`'s inline banner; **success-collapse is still absent**, re-confirmed. |
| Refresh never blanks | ✅ **landed** as a rule — [0018](../decisions/0018-attention-is-the-primary-signal.md); built with the rebuild |

## 9 · Deliberate drops

| Item | Why |
|---|---|
| Curved Bézier DAG edges, hover tracing | ⛔ The corpus itself marks this *"obsolete — close rather than implement"*: it is polish for the freeform canvas 0014 rejects. **Done:** `#266` was split and closed, `#208` folded in, and the surviving half — vendor brand marks on workers plus status motion and skeletons — carried to **`#476`** (under `#757`) in M30. |
| The pixel-level styling of the mockups | ⛔ Superseded by [0006](../decisions/0006-visual-direction-quiet.md) and the shipped token set, which are normative. The mockups are kept for layout and state, not colour values. |
| Anything in the corpus contradicted by a later correction | ⛔ Records win over corpus — see [`README.md`](README.md). Notably: notifications never carry a verdict, rooms rather than "needs you" as the phone's landing screen, and the playful status verbs are **kept** (an earlier pass proposed removing them). |

## 10 · Screen realization — every 02-screens shape, both surfaces

The tables above route the *calls*; this one is the *screen*-level cut — built/partial/absent per
surface, for setting priority. Verdicts key on source read 2026-08 (`Aer.Ui/Views`, `Aer.Mobile/lib`);
each row cites the issue tracked elsewhere in this doc rather than restating it (record-once).
**✅ built · ◐ partial · ❌ absent.**

| 02-screens shape | Desktop | Phone | What's missing (cite) |
|---|---|---|---|
| First run | ✅ | ✅ | Front door + readiness (`#1071`/`#478`); phone pairing+QR. |
| The daily driver | ✅ | ✅ | Switcher-landing (`#1046`/`#1071`), state-grouped list (`#1072`), single worker chip, markdown chat (`#1076`/`#1080`). |
| Two workers, a gate | ◐ | ◐ | The spine gap narrows: the **conversational gate as a turn** now renders on **both** surfaces for the permission kind (`#390`, 0022) — desktop and phone, both live-driven 2026-08-09. Orchestrator pin/reassign also shipped since (`#1317`) but is invisible pending a second participant. Still unbuilt: add-worker itself — `#493`'s object model landed 2026-08-16, but no add-worker endpoint exists (§4's "two workers may share a vendor" row) — and multi-worker chips; the consult/escalation kinds (§1 cross-examination is decided in 0019; its UI is `#474`/`#751`). |
| When it fails | ✅ | ◐ | Desktop `#482` inline failed-banner matches the screen; phone shows failed card state, not the full inline fix-affordance set. |
| Starting from a template | ✅ | n/a | `TemplatePickerWindow` + guided flow (phone template use out-of-scope, 0041). |
| Drawing a shape (editor) | ◐ | n/a | Split across two editors, neither complete alone: the guided flow (`AuthorView.axaml`'s primary M19 Phase 4 path) has the per-step instruction, gate toggle, and named-blocker selection (0025 — §6 rows now landed) but no DAG preview; the advanced/legacy editor has the DAG preview (`TemplateEditorDagCanvas`) and the gate toggle but no instruction field. The graph and the instruction don't yet coexist in one surface. |
| Settings | ✅ | — | Workers readiness (`#1069`), pairing, appearance. |
| Phone (rooms/chat/gate/notif) | — | ◐ | Rooms root, needs-you filter, chat (`#1080`), **working step-review inbox** (Approve/Reject/Supersede, `#1049`), and the **permission** conversational gate inline in chat (`#390`, live-driven 2026-08-09). The consult/escalation kinds of the conversational gate ride `#474`/`#751`; notifications-inform is a §8 item. |
| M27 · skill attach on chip | ❌ | ❌ | No skill-attach UI either surface (0033). |
| M27 · skill creation drawer | ❌ | n/a | Unbuilt (0031/0033). |
| M27 · orchestrator + add/remove worker | ◐ | ◐ | Corrected from ❌/❌ — orchestrator pin/reassignment **shipped** (`#1317`, commit 75ed55ac): desktop `ChatHeaderView.axaml:78-98` (per-participant "Make {name} orchestrator," `ReassignOrchestratorCommand`), phone `chat_screen.dart:384-394,925-1034`. Gated `Participants.Count > 1` (`ChatViewModel.cs:198`), so wired but invisible until a room can hold two workers. Add-worker and removal guards remain unbuilt — `ChatHeaderView.axaml:45-46` still marks "+ Add worker" as M27 (0032). |
| M27 · workflow toggle-off | ❌ | — | Unbuilt (0001). |
| Resident · spend controls | ◐ | ◐ | `RoomTurnHostBanner` **displays** meter+values (`#994`) but read-only; the editable-in-place fields + used-this-hour bar are the gap. |
| Resident · dormant | ✅ | ◐ | Desktop dormant banner + Wake + escalation text in transcript (`#994`); phone has the same inline bubble and a working Wake button (`chat_screen.dart:754,857-858`). The remaining gap is desktop's **Swap orchestrator…** candidate buttons on the dormancy message (`ChatView.axaml:209-212`, `#1317`), which mobile has no equivalent for. |
| Resident · waiting on a lock | ✅ | ◐ | `WaitingOnLockBanner` (`RoomView.axaml:29-45`, comment citing `#1299`/`#480`) names the holder and offers **Try again** on desktop; holder text traces to `ConcurrencyGuard.DefaultHolderDescription()`, a process description, not a room name, until the lock grows a room-name field (`#752`). Phone has only a padlock status glyph (`status_mark.dart:157-166`, `rooms_screen.dart:25,47`) — no banner, no holder text, no try-again action exists there yet. |
| Resident · escalation is a gate | ❌ | ❌ | It *is* the runtime gate (`#474`/`#751`); only the dormancy escalation *text* renders today. |

**The distinction the tables above blur:** the *step-review* gate (0007 pause → Approve/Reject/Supersede)
**is** built on both surfaces — desktop `PausedStepViewModel`, phone `chat_screen.dart`'s `_decideStep`/
`_decideStepWithReference` (moved from the now-deleted `inbox_screen.dart` in `#1228`, 2026-08-14 — this
row's citation was stale). The *conversational* gate is now **partly** built: its **permission** kind
renders inline on **both surfaces** with the full scope ladder (`#390`/`#445`, 0022) — a worker asking
mid-turn for a command, answered where it was raised. Live-driven on desktop and phone 2026-08-09 via
the Allow-once rung (which clears the gate); the remaining rungs render but were not individually
exercised. What remains absent: the **consult / escalation** kinds, and "Ask someone…" (0019) — their
*spec transfer* is `#474`'s untransferred-screens work, and their *implementation* is tracked under
umbrella `#751` (member `#494`); the doc's earlier `#367` cites meant the docs-scrub issue that gated the
spec rewrite, which closed without the rewrite happening, so `#474`/`#751` are the live trackers
everywhere this doc routes gate UI. That gate is the single highest-leverage gap — the spine of "two
workers, a gate", escalation, and cross-examination (0019, the corpus's stated *"single most important
behaviour"*); the permission slice, now on both surfaces, is the first piece of it to land.

## What is left to route

The issue-shaped and centrepiece items above are done. What remains is narrower than this section
previously described — every decision record it called for has since been written.

**New decision records.** All done. **One state machine** landed as
[0020](../decisions/0020-one-state-machine.md). **Artifacts are files** — the one concept this section
previously named as having "no record and no parent" — has since been written as
[0021](../decisions/0021-artifacts-are-files.md) (2026-07-24). Its implementation **is now routed and
under way** — corrected 2026-08-17 from "not routed": `#1340` carries an eight-slice plan, slice 1
(the model plus the desktop Files list) landed as `#1344`, and the rows above name which halves of
each promise remain. `#377` is still only a diff viewer, but it now has a model to sit on.

**Records that amend existing ones.** All four have also been written, each also dated 2026-07-24: the
permission **scope ladder** and **denial is an answer**, as
[0022](../decisions/0022-permission-ladder-and-denial-is-an-answer.md) (amends
[0004](../decisions/0004-permission-scopes.md)) — and both are now landed in code (§2); **effort named
by behaviour** and **models offered by purpose**, as
[0023](../decisions/0023-effort-and-models-are-named-by-behaviour.md), with
[0058](../decisions/0058-model-and-effort-are-marks-not-words.md) (2026-07-27) layering the chip's
presentation on top — still unimplemented (§4); **namespaced commands**, `/ask-all`, and
canonical-vs-native skills, as [0024](../decisions/0024-commands-are-namespaced.md) (amends
[0010](../decisions/0010-skills-and-advisor.md)) — still unimplemented (§5); **"ask me first"** and
**the instruction is the step's body**, as
[0025](../decisions/0025-a-step-is-an-instruction-with-a-gate-toggle.md) (amends
[0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md)) — and this one **is** landed, in the
guided authoring flow (§6).

**The nine claims → journeys.** Also done, and has been since 2026-07-24 (`#488`) — thirty-nine
minutes after this section's own "None exists today" was written (`git blame`), and never revisited.
`spec/journeys.md` carries J10–J18, one per claim, each with its own `Status`, `Passes when` and
`Today`; every one currently reads `Status: Fails` (§7). `docs/plan.md` already links them ("eighteen
are defined") and `ReconcileTests` already tracks them — nothing here is unrouted, only undemonstrated.

**The screens** (§ across 02) go to `#474`, which tracks the untransferred corpus — the largest single
body, ~350 lines across eight screens on two surfaces.

## Still genuinely open

Not gaps — questions the corpus deliberately left unresolved. They need answers before the work they
block can be scoped.

- **Whether permissions can be raised at all** — answered since, by `#472`: yes, on both vendors, via
  a blocking MCP tool. The corpus predates that probe. Feeds [0015](../decisions/0015-three-kinds-of-needs-you.md).
- **Does a room live in one folder forever?** — `#472` also found `--add-dir` on both CLIs, so
  disjoint folders are feasible at the vendor level. Reopened rather than closed; see `#443`.
