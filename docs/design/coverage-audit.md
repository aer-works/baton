# Coverage audit — what the corpus holds, and where each item went

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
| A **screen** or affordance | the UI spec replacement (`#367`) | it describes a surface |
| A **feature** or fix | GitHub issue | it is a unit of work |

A single call can land in two places — the rule in a record, the demonstration as a journey. That is
correct, not duplication: the record says *why*, the journey says *what you get*.

## 1 · The centrepiece

| Call | Source | Status |
|---|---|---|
| **Consulting is not deciding.** Put a question to anyone — including a worker not yet in the room — and it joins to answer. The gate stays open the whole time; only you close it. Asking three still decides nothing. | 03, 07 | ✅ **landed** — [0019](../decisions/0019-consulting-is-not-deciding.md). The corpus calls this *"the single most important behaviour in the room model"* and *"if only one thing survives contact with implementation, it should be this."* |
| **Routing is a control.** You choose who answers and what they see. The product never reads the conversation to decide who should respond. | 03 | ✅ **landed** — 0019 §4. CLAUDE.md Architecture Rule 1 surfacing in the UI, and what keeps cross-examination from becoming inference. |
| **Adding a worker = asking a question.** No separate participant-management surface. | 03 | ✅ **landed** — 0019 §1. |
| **What a newly-added worker sees:** a room summary + the raising turn and its attachments verbatim + the ability to query for more (`#424`). Always disclosed before sending, every item removable. | 06 | ✅ **landed** — 0019 §3; the disclosure *UI* still routes to `#367`. |

## 2 · Permissions

| Call | Source | Status |
|---|---|---|
| **The scope ladder** — allow once / this command here / anything here — offered **at the moment of asking**, never buried in settings. | 04, 07 | **→ extends [0004](../decisions/0004-permission-scopes.md).** 0004 has the *scopes*; the ladder-at-point-of-ask is the affordance and is absent. |
| **Denial is an answer.** A refused worker is told and continues — it does not silently retry and does not die. | 04, 07 | **→ same.** Absent, and it is the difference between a safety feature and a dead end. |
| **A pending permission dies with its turn**, everywhere at once, and the transcript says why. | 06 | **→ same.** |
| **A queued message does not send into a blocked worker.** The queue waits on the whole turn, permission included, and says what it is waiting for. "Send now" interrupts — which here means denying the permission — and the control says so. | 06 | **→ same**, affordance to `#367`. (The general in-flight-message queue itself now exists — `#1074` — but it waits on a *running turn*; waiting on a *permission* gate, and "Send now", ride the `#367` gate surface.) |

## 3 · State, errors and freshness

| Call | Source | Status |
|---|---|---|
| **One state machine.** Every surface renders the room's state; none derives its own. | 02 | ✅ **landed** — [0020](../decisions/0020-one-state-machine.md) (the record that generalises `#467`/`#468`), enforced by the one `RoomCardViewModel.DeriveStatus` every surface reads; `StatusDerivationTests` pins the task-headline↔card parity so the shell cannot disagree with itself about "running". |
| **Errors are content.** A failure shows what broke, in the room, with the worker that failed right there to be asked about it. Not a status word with the reason behind a drill-in. | 02, 03 | ✅ **landed** — the rule rides [0020](../decisions/0020-one-state-machine.md); the surface is `#482` (RoomView's failed-step banner shows the error inline) + `#404` (the per-step drill-in shows every outcome's output/detail, not just failures). |
| **Stale, not blank.** Refreshing never empties a list; previous content stays and is marked stale. | 03 | ✅ **landed** — [0018](../decisions/0018-attention-is-the-primary-signal.md)'s freshness amendment. Arrived independently via the power-cut analysis. |
| **Success collapses, failure opens.** A passing command shows one line; a failing one opens itself. Status and duration stay visible either way. | 03, 07 | **◐ partial** — `#267` (open `#750`). The **markdown/code rendering** half is landing per surface, governed by [0051](../decisions/0051-markdown-rendering-is-a-defined-subset-parsed-per-platform.md): **desktop** via `#1076` (Markdig → a token-set renderer in the chat transcript; no remote content), **mobile** via `#1080` (`flutter_markdown_plus`, same subset, no remote content). The failure-opens half exists as the `#482` failed-step banner; the **success-collapse** half is still absent. |
| **Readiness up front.** Which vendor CLIs were detected, at first run and in Settings — the most likely first failure is the least self-evident. | 02 | ✅ **landed** — `#478`. Shown at first run (the ▤ front door's Workers line, `#1071`) and in Settings (`#1069`), both from one derivation (`NewWorkflow.VendorReadinessLines`). |

## 4 · Workers, models, effort

| Call | Source | Status |
|---|---|---|
| Vendor / model / effort are three choices, all on the chip | 04 | ✅ **landed** — [0017](../decisions/0017-vendor-model-effort-are-three-choices.md). |
| **Effort is named by behaviour** — quick / standard / careful / exhaustive — never a token budget or a vendor's flag name. | 04, 05 | **→ extends 0017.** The axis landed; the *naming rule* did not. `#472` confirmed both CLIs expose a real `--effort`, so this is now implementable. |
| **Models are offered by purpose** — deep / balanced / fast. Nobody should need this month's model string. | 04 | **→ extends 0017.** Absent. |
| Two workers may share a vendor at different models/efforts — a normal room, not an edge case | 04, 07 | ✅ **landed** — 0017. |
| **Context is per worker**, and running out is offered as a choice before it becomes an event. | 04 | **→ `#395`** — scope extended: per-worker headroom, and the choice offered before the event. |
| **Limits, not dollars.** Spend shown against the subscription's own limits. | 04 | **→ `#751`** (umbrella). The unit this product actually runs on. What each CLI reports about quota is still unprobed. |

## 5 · Commands and skills

| Call | Source | Status |
|---|---|---|
| **Commands are namespaced** — room commands, then each vendor's own. Resolves an ambiguity single-agent tools never face. | 04 | **→ extends [0010](../decisions/0010-skills-and-advisor.md).** |
| **`/ask-all`** — one question to every worker, answers side by side. | 04, 07 | **→ same**, plus a journey. |
| **No slash palette on a phone** — the same commands become an Actions sheet from the room header. | 04 | **→ `#367`.** |
| **Canonical skills under Room, native skills under their vendor, marked as such.** | 06 | **→ extends 0010** / `#386`. |
| **An advisor is a preset, not a new noun.** Roles are instructions. | 06 | **→ `#385`**, which currently proposes it as a participant kind. |

## 6 · Rooms, files and shapes

| Call | Source | Status |
|---|---|---|
| Room owns memory; shared across vendors; proposed, never inferred | 06 | ✅ **landed** — [0016](../decisions/0016-memory-is-room-owned.md). |
| Shapes are an ordered list that renders as a graph | 02 | ✅ **landed** — [0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md). |
| **"Ask me first" is a property of a step, not a node type** — one switch is the entire mental model for human oversight. | 02 | **→ extends 0014.** |
| **A step's instruction is its body**; previous output flows in implicitly; no template language. | 06 | **→ extends 0014.** |
| **Artifacts are files** — vendor-neutral, versioned, attributed, explicitly attached, diffable between vendors. | 03, 07 | **→ new decision record.** Only partially implied today; `#377` covers the viewer, not the model. |
| **Saving a working document into the project: diff-and-choose, never overwrite by default**, and flag divergence since derivation. | 06 | **→ same record.** |
| **Documents stay, plumbing goes.** One file list; the only distinction is "in your project" or not. Execution directories are never surfaced. | 04 | **→ same record.** |
| **Child rooms** nest in the list and report back as a turn; the parent never blocks. | 06 | **→ `#340`** — scope extended with the non-blocking parent, and its interaction with `#480`'s directory lock. |
| **Two rooms on one folder**: serialised already (verified — the turn lock is keyed on directory path). Surface the wait, name the holder, warn on a duplicate room. | 06 | **→ `#480`.** The engine behaviour is verified and correct; only the *surfacing* is absent. |
| **Gates render inline**, in the conversation that produced them, reachable from a "needs you" filter and the phone. The separate decision surface goes away. | 02 | Inline gates + phone entry point **→ `#367`**. Desktop "needs you" filter: ✅ **landed** — `#1072` (a filter on the switcher; needs-you rows expand in place to their paused steps, decision 0007's middle level, and the Home decision inbox retired into it). |
| Rooms are the front door on both surfaces; "needs you" is a filter, not the landing screen | 02 | ✅ **landed** — 0018 + `#337`; the desktop rail is now the three icon-only destinations 02-screens draws (`#1071`), with Home folded into the ▤ rooms front door. |

## 7 · The nine claims and their demonstrations

The corpus states these are *"journey-shaped on purpose"* — each is a claim plus the condition under
which it counts as demonstrated. **All nine → `spec/journeys.md` + the registry**, as a coordinated
change under `ReconcileTests`. None exists today.

| Claim | Demonstrated when |
|---|---|
| Cross-examination | At a live gate, a worker not previously in the room is asked, answers, contradicts the first — and the gate is still open |
| Two subscriptions | A room where both vendors act, on plan auth, no key configured anywhere |
| Shared memory | A fact established by one vendor is used by a different vendor later in the same room |
| Two of one vendor | Two chips, same vendor, different model and effort, both answering |
| Files with receipts | One document authored by one vendor and edited by another, with a diff between their versions |
| Work outside the UI | Quit the desktop app mid-run; answer the permission on the phone; reopen and find it continued |
| Scoped permissions | Grant "allow in this room", see it not asked again, find and revoke it in settings |
| Shapes as lists | Author a four-step template on a phone, start it on the desktop, watch it run |
| Ask everyone | One question, two answers side by side, disagreeing |

## 8 · The eight delights

Not differentiators alone; collectively the difference between a tool you tolerate and one you like.
**All eight are tracked — three shipped (`#482`/`#463`/refresh-rule), one partial (`#462`'s never-block half, `#1074`); the other four remain unbuilt.**

| Delight | Status |
|---|---|
| `y`/`n` for permissions, never bound to Enter, so a reflex cannot approve | **◐ desktop built** (`#481`/`#390`, source 2026-08): the runtime permission gate now renders inline in chat with the scope ladder, and a bare `y` allows / `n` denies, never on Enter and never with a modifier (`MainWindow.PermissionAnswerFor`, `PermissionGateKeystrokeTests`). Config-time `AuthorView` flags still exist alongside it. **Phone gate pending; live-drive visual confirmation outstanding.** |
| Typing never blocks; the queue is visible, with interrupt and remove | **◐ partial** — `#462` (open `#750`). The **never-block + visible + removable queue** half landed via **`#1074`**: the desktop composer no longer disables mid-turn; a send joins a visible, removable FIFO that drains one message per turn-completion. **Interrupt** ("Send now" cancels the running turn) is the deferred half — a distinct mechanism, its own slice. |
| Failures offer the fix, with the worker that failed already holding the context | ✅ **shipped** — `#482` (desktop verified: `RoomView`'s failed-step banner shows the error text inline with **Try again** / **Ask ⟨worker⟩ to fix it** / **Show full output**) |
| Jump to the last decision via an event rail | **→ `#459`** — grouped into open `#750`, **unbuilt** (the earlier "(exists)" was wrong): no event rail or keyboard nav to a decision exists anywhere |
| Status readable without colour | ✅ **shipped** — `#463` |
| *"Thought for 12s"* reported after the fact, never a live counter | **→ `#483`** — closed, **unbuilt** (source checked 2026-08; not in the `#750` set): no duration / thinking-time rendering exists |
| Success collapses, failure opens | **→ `#267`** — grouped into open `#750`. The **markdown/code-rendering** half of `#267` is landing per surface (desktop `#1076`, [0051](../decisions/0051-markdown-rendering-is-a-defined-subset-parsed-per-platform.md)) — canonical status in the §7 row above. The two halves this row names: failure-open exists via `#482`'s inline banner; **success-collapse is still absent** |
| Refresh never blanks | ✅ **landed** as a rule — [0018](../decisions/0018-attention-is-the-primary-signal.md); built with the rebuild |

## 9 · Deliberate drops

| Item | Why |
|---|---|
| Curved Bézier DAG edges, hover tracing | ⛔ The corpus itself marks this *"obsolete — close rather than implement"*: it is polish for the freeform canvas 0014 rejects. **Done:** `#266` was split and closed, `#208` folded in, and the surviving half — vendor brand marks on workers plus status motion and skeletons — carried to **`#476`** in M30. |
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
| Two workers, a gate | ◐ | ❌ | The spine gap narrows: the **conversational gate as a turn** now renders on desktop for the permission kind (`#390`, 0022). Still unbuilt: add-worker + multi-worker chips (M27); the consult/escalation kinds and the phone surface (§1 cross-examination is decided in 0019; its UI is `#367`). |
| When it fails | ✅ | ◐ | Desktop `#482` inline failed-banner matches the screen; phone shows failed card state, not the full inline fix-affordance set. |
| Starting from a template | ✅ | n/a | `TemplatePickerWindow` + guided flow (phone template use out-of-scope, 0041). |
| Drawing a shape (editor) | ◐ | n/a | `AuthorView` list-not-canvas + DAG preview (0014); the per-row **"ask me first" gate toggle** and named-blocker fan-out UI are the thin part (§6 "ask me first" → extends 0014). |
| Settings | ✅ | — | Workers readiness (`#1069`), pairing, appearance. |
| Phone (rooms/chat/gate/notif) | — | ◐ | Rooms root, needs-you filter, chat (`#1080`), **working step-review inbox** (Approve/Reject/Supersede, `#1049`). The *conversational* gate rides `#367`; notifications-inform is a §8 item. |
| M27 · skill attach on chip | ❌ | ❌ | No skill-attach UI either surface (0033). |
| M27 · skill creation drawer | ❌ | n/a | Unbuilt (0031/0033). |
| M27 · orchestrator + add/remove worker | ❌ | ❌ | No orchestrator pin, add-worker, or removal guards (0032). |
| M27 · workflow toggle-off | ❌ | — | Unbuilt (0001). |
| Resident · spend controls | ◐ | ◐ | `RoomTurnHostBanner` **displays** meter+values (`#994`) but read-only; the editable-in-place fields + used-this-hour bar are the gap. |
| Resident · dormant | ✅ | ◐ | Desktop dormant banner + Wake + escalation text in transcript (`#994`); phone card state present, drawer partial. |
| Resident · waiting on a lock | ✅ | ◐ | `WaitingOnLockBanner` (`#618`) names holder+try-again; names the *path* until the lock grows a room-name field (`#752`/`#480`). |
| Resident · escalation is a gate | ❌ | ❌ | It *is* the runtime gate `#367`; only the dormancy escalation *text* renders today. |

**The distinction the tables above blur:** the *step-review* gate (0007 pause → Approve/Reject/Supersede)
**is** built on both surfaces — desktop `PausedStepViewModel`, phone `inbox_screen._decide`. The
*conversational* gate is now **partly** built: its **permission** kind renders inline on desktop with
the scope ladder (`#390`/`#445`, 0022) — a worker asking mid-turn for a command, answered where it was
raised. What remains absent (`#367`): the **phone** surface of it, the **consult / escalation** kinds,
and "Ask someone…" (0019). That gate is the single highest-leverage gap — the spine of "two workers, a
gate", the phone gate, escalation, and cross-examination (0019, the corpus's stated *"single most
important behaviour"*); the desktop permission slice is the first piece of it to land.

## What is left to route

The issue-shaped and centrepiece items above are done. Three kinds of work remain, tracked by `#474`.

**New decision records.** **One state machine** is now **recorded and landed** —
[0020](../decisions/0020-one-state-machine.md): every surface renders the room's state from one shared
derivation (`RoomCardViewModel.DeriveStatus`), pinned by `StatusDerivationTests`, so the general form of
`#467`/`#468` makes "no room open while running" *impossible* rather than merely fixed, and it carries
"errors are content" with it. That leaves one concept with no record and no parent: **artifacts are files**
(vendor-neutral, versioned, attributed, diffable — plus diff-and-choose on save, and "documents stay,
plumbing goes").

**Records that amend existing ones.** Four items extend a record. The repo's rule is *"never edit a
decision **to change its meaning**"* ([`decisions/README.md`](../decisions/README.md)) — which is not the
same as never editing it. The established pattern, from 0013 amending 0001, is **three parts**: the new
record, a dated **amendment blockquote added to the top of the original** pointing forward to it and
saying precisely what still stands, and the index row noting the amendment. The original's body is left
as written, because the transition is itself part of the record.

Do all three. A new record with no back-pointer from the record it amends is undiscoverable from the
place a reader actually starts. They are: the permission **scope ladder** at the moment of
asking plus **denial is an answer** (amends [0004](../decisions/0004-permission-scopes.md)); **effort named
by behaviour** and **models offered by purpose** (amends
[0017](../decisions/0017-vendor-model-effort-are-three-choices.md)); **namespaced commands**, `/ask-all`,
and canonical-vs-native skills (amends [0010](../decisions/0010-skills-and-advisor.md)); **"ask me first"
as a property of a step** and **the instruction is the step's body** (amends
[0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md)).

Every new record is at minimum a **three-file change** — the record, `docs/plan.md`'s table, and
`decisions/README.md` — or the plan gate's three-way set comparison fails the build. An amending one is
four, counting the record it amends.

**The nine claims → journeys.** §7's table becomes journeys in `spec/journeys.md` plus the registry, as
one coordinated change under `ReconcileTests`. Note the ordering constraint: the plan gate extracts every
`J<n>` reference from `docs/plan.md` and requires a matching heading in the spec, so the journeys must
land **before** anything cites them.

**The screens** (§ across 02) go to `#367`, the UI spec replacement — the largest single body of untransferred
material, ~350 lines across eight screens on two surfaces.

## Still genuinely open

Not gaps — questions the corpus deliberately left unresolved. They need answers before the work they
block can be scoped.

- **Whether permissions can be raised at all** — answered since, by `#472`: yes, on both vendors, via
  a blocking MCP tool. The corpus predates that probe. Feeds [0015](../decisions/0015-three-kinds-of-needs-you.md).
- **Does a room live in one folder forever?** — `#472` also found `--add-dir` on both CLIs, so
  disjoint folders are feasible at the vendor level. Reopened rather than closed; see `#443`.
