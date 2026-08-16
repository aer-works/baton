# 0020 — One state machine: every surface renders the room's state, none derives its own

Status: accepted; **amended 2026-08-14 (#1219) — tenth state `stopped`, derived with the room's §15 lock; amended 2026-08-16 (#1296) — eleventh state `waitingToStart`, derived from the concurrency cap's in-memory queue**
Date: 2026-07-24

## Context

The M25 manual run produced two defects that look unrelated and are the same bug:

- **#467** — the shell reports *"no task open"* while a task is open and running.
- **#468** — the Conversation tab says *"no conversation recorded"* directly above the conversation.

Both were filed as UI bugs. Neither is. In each case a surface computed its own answer to a question
the room had already answered, and computed it differently. The header derived "is anything open?"
from one thing, the runner from another; the tab derived "is there a conversation?" from a check that
disagreed with the renderer sitting immediately below it.

The corpus names the general form, in
[`01-definition.md`](../design/01-definition.md#a-rooms-life) — written, it says, *"because the
last run produced two surfaces disagreeing about which state a room was in"*:

> One source of truth per room. Every surface — switcher row, header, inbox, phone — renders this
> state and nothing derived independently. **Cancelled and Failed are states, not absences**: a
> stopped room must never read as "Finished."

[`02-screens.md`](../design/02-screens.md) states the consequence as a call: *"Every surface renders
the room's state machine and none derives its own — which is what makes 'no task open' while running
**impossible rather than merely fixed**."*

That last clause is why this is a decision rather than two bug fixes. Fixing #467 and #468
individually leaves the mechanism that produced them intact, and the mechanism is *permission for a
surface to hold an opinion about state*. This is the same failure shape the whole milestone
diagnosed — something could disagree silently because nothing checked — and the correction has to be
a rule, not a patch.

A worked example of the cost: `docs/milestone-history.md` records that a **cancelled** task rendered
as **"Finished"** because cancellation has no `WorkflowStatus` and the card's derivation fell
through. No test caught it. A derivation with a missing case does not fail; it produces a confident
wrong answer.

## Decision

**A room has exactly one state machine. Every surface renders it. No surface derives state.**

**The canonical set is `design/tokens.json`'s `status` block**, not a list restated here. Nine today:
`idle`, `working`, `needsInput`, `readyForReview`, `finished`, `failed`, `cancelled`, `queued`,
`unavailable`.

> **Amendment, 2026-07-24 (#489).** This record originally listed six — `Idle`, `Working`,
> `NeedsInput`, `Finished`, `Failed`, `Cancelled` — copied from
> [`01-definition.md`](../design/01-definition.md)'s state diagram. That diagram predates
> [0015](0015-three-kinds-of-needs-you.md)'s split of the pause kinds and
> [0018](0018-attention-is-the-primary-signal.md)'s quiet band, so it under-counted:
> `readyForReview`, `queued` and `unavailable` were already CI-required marks with **no state in this
> record**, which under rule 1 meant a surface had something to render and no state to render it
> from. The reverse hole was worse — `idle` was listed here and had **no token at all**, so Avalonia
> drew `Icon.Dot` for it, Flutter could not draw it, and the drift gate was blind to both because it
> only walked token → toolkit. Naming the token file as the source rather than duplicating a list is
> the fix: **one place, machine-checked, in both directions.**

> **Amendment, 2026-08-14 (#1219).** A tenth state, `stopped`: **the run halted because its process
> died** — not finished, not failed, and nobody stopped it. There was no value for this, and rule 2
> says absence is not a state, so the switcher fell through to `working` and turned a spinner over
> rooms where nothing had been happening for days. #1215 made it visible rather than causing it: that
> slice put an offer on such a room's transcript reading "This room stopped mid-run", and the row
> beside it still said "Working — implement". The shell disagreeing with itself about "running" is the
> defect in this record's own opening paragraph, arrived at from a new direction.
>
> The mechanism is worth recording, because it is the reason this took a decision rather than a patch.
> **No predicate over the journal can produce this state.** `WorkflowStatus.Running` is defined, in its
> own summary, as "at least one step's latest attempt is still in flight *or* Flow crashed before
> recording its outcome" — a live room and a dead one are the same recorded state by construction. The
> answer is the room's §15 lock: `ConcurrencyGuard` holds a kernel-level file lock for the whole of a
> pump, and the OS releases it the instant the holder exits, crashed or not. So `DeriveStatus` takes it
> as an argument. That is a genuine extension of rule 1 rather than an exception to it — the rule says
> that when a surface needs an answer the room does not expose, the fix is *to expose it from the
> room*, and this exposes it once, in the one derivation, rather than letting each surface probe.
>
> Two consequences worth naming. `stopped` is deliberately distinct from `cancelled` on the same
> grounds `cancelled` is distinct from `failed`: "it died" is not "you stopped it", and a person is
> owed the difference. And its arm is ordered ahead of every other `Running` arm, including the
> permission one — an orphaned ask on a dead room must not headline "Permission requested" for a
> worker nothing is left to release, which is a hazard the permission arm's own comment already named
> for the `Paused`/`Terminal` case and could not detect until the lock was consulted.
>
> Note also that `01-definition.md`'s "a stopped room must never read as 'Finished'" predates this and
> uses "stopped" in the general sense of *halted without finishing* — it covers `cancelled`, `failed`
> and now `stopped` alike. The state name matches what a person is shown, per
> [0002](0002-one-vocabulary.md); the older sentence is unchanged and means what it always meant.

> **Amendment, 2026-08-16 (#1296).** An eleventh state, `waitingToStart`: the daemon's global/per-
> vendor concurrency cap (#448, ratified by Fable — 3 global / 2 per vendor, ephemeral by design) has
> FIFO-queued this room's turn dispatch and it has not started yet. Not `queued` — `design/
> tokens.json`'s own prose already records that `queued` is a step-level state (#1132), and this is a
> room-level one; reusing the token would contradict a recorded, verified ruling.
>
> The mechanism follows `stopped`'s own precedent exactly, one level further: **no predicate over the
> journal can produce this state, because an unstarted turn may have no journal entry at all yet.**
> Where `stopped` reads a kernel-held file lock that outlives its own process, `waitingToStart` reads
> an in-memory daemon fact — `ConcurrencySlotGate`'s wait queue — that does not outlive the daemon's
> own process. `DeriveStatus` takes it as a second caller-supplied argument, the same extension of
> rule 1 `isFlowLockHeld` already established: the fix for "a surface needs an answer the room does
> not expose" is to expose it from the room, once, in the one derivation.
>
> Ephemeral is a deliberate choice, not a gap. A daemon restart drops every queued waiter, and a
> previously-queued room correctly reverts to showing its true not-started state — nothing durable
> would ever have started it, so a durable `SessionQueued`-style journal record would just be a lie in
> the log the moment the daemon restarts, without a durable dispatcher behind it to make it true again.
> Ruled explicitly so a future reader does not mistake "queue does not survive a restart" for a defect
> to fix.
>
> Ordering: `waitingToStart` is checked FIRST, ahead of every other arm — including `stopped`'s own,
> which itself already runs first among the `Running`-scoped ones. A room waiting on a slot has no
> journal or projection facts yet for any other arm to misread, the same reasoning `stopped`'s own
> amendment gives for its position relative to the permission arm.

> **Amendment, 2026-08-16 (#1299).** A twelfth state, `waitingOnLock`: the state this record's own
> #616 amendment already named — "Waiting on another room's lock", ratified on #495 — arrives with a
> correction. Room identity is directory-keyed (#495/#1296), so two *rooms* cannot share a folder as
> distinct rooms; opening a folder twice reopens the same room. The name and the design corpus's
> original "names the room that holds this folder, linked" language both presuppose a room-vs-room
> collision that the architecture cannot produce. Fable's ruling on #480: the real, remaining
> collision is process-vs-room — a bare `aer run` pump, the memory-proposal sweep, or a second Baton
> instance holding the directory's `flow.lock` while this room's own daemon-tracked machinery has
> nothing to say about it. There is no other room to link to, so "linked" is retired; the state names
> the holder process (description + how long it has held the lock) and offers nothing to navigate to.
>
> Mechanism, same shape as `stopped`: `isFlowLockHeld` (rule 1's existing extension,
> `ConcurrencyGuard.IsHeld`) already carries the needed signal — no new caller-supplied argument.
> `DeriveStatus` reads `waitingOnLock` off `WorkflowStatus.Running` when the lock is held, no step of
> this room is itself `Running`, and nothing has failed or been rejected: a room with real
> failure/exhaustion information keeps showing that, since it is more actionable than a lock note.
> Scoped to `Running` only — a `Paused`/`Terminal` room already has a well-defined, more urgent status
> (`NeedsYou`, `Failed`, `Finished`, `Cancelled`) that a transient external lock hold must not preempt.
>
> **The originally-scoped "warn before creating a duplicate room" half of #480 is ruled out entirely**,
> not deferred: once `waitingOnLock` is canonical, opening a folder whose lock is externally held
> shows the wait immediately, named, before any action — that IS the "knowing choice" #480 asked for,
> delivered by the state machine rather than a confirmation dialog. No duplicate-room registry exists
> or is needed.

Three rules govern how the states are consumed:

**1. Rendering is a projection, never a computation.** A surface may map a state to a mark, a word, a
colour, or a layout. It may not *decide* the state. "Is anything running?", "does this have a
conversation?", "did this finish?" are answered by the room, once. If a surface needs an answer the
room does not expose, the fix is to expose it from the room — never to compute it locally.

**2. Absence is not a state.** `Cancelled` and `Failed` are values, not the lack of a value. A
derivation that reaches its end without matching must be a compile-time or test-time failure, not a
fallback. The `Finished`-for-`Cancelled` defect is exactly what a silent fallback produces, and
CLAUDE.md's rule against swallowing exceptions is the same principle one layer down.

**3. One object, several entry points — never several copies.** A gate rendered inline, in the
"needs you" filter, and on the phone is *one* piece of state seen three times. This is what makes
[0019](0019-consulting-is-not-deciding.md)'s consultation coherent (the gate you consult about is the
gate you answer) and what makes a stop propagate: the corpus's rule that a pending permission *"dies
with its turn, everywhere at once"* is only expressible when there is one object to kill.

### Errors are content, which this record carries

The corpus states this separately, but it is the same decision applied to the failed state — a
failure is a *value the room holds*, so it renders where the room renders:

> A failure shows what broke, **in the room**, with the worker that failed right there to be asked
> about it. Not a status word with the reason behind a drill-in.
> — [`02-screens.md`](../design/02-screens.md), *the calls made here*

Concretely, from the same document's failure screen: the error text is the turn's content, the first
few lines are on screen unasked, full output is one click away, and the affordances are *Try again ·
Ask claude to fix it · Show full output*. The corpus's reasoning: *"a failure that says only 'failed'
forces a hunt through logs; the first few lines of what actually broke are almost always enough to
know whether it is your problem or the agent's."*

This belongs here rather than in its own record because "the reason lives behind a drill-in" is a
derivation in disguise — the surface deciding that a failure is a *status* with detail attached,
rather than rendering what the room holds.

## Consequences

### The interaction states every surface must handle

The corpus calls writing these down *"the actual drift protection"*, because the last rebuild drifted
by deciding them **per screen, late, by whoever was implementing**.

> **Amendment, 2026-08-04 (#616).** This section originally reproduced the state table rather than
> citing it, with stated reasoning: a rule living only in `docs/design/` lived in a directory whose
> own README disclaims authority. The reasoning was sound and the conclusion was wrong — the copy
> drifted exactly as copies do: it stayed at **ten** while the corpus reached **thirteen** (Gate
> unverified was added to the corpus the day after this record and never arrived here; Waiting on
> another room's lock and Dormant were ratified on
> [#495](https://github.com/aer-works/baton/issues/495) and had nowhere here to land). The fix is
> the same one this record's own #489 amendment already articulated for the status population:
> **name a machine-checked file as the source rather than duplicating a list.**
>
> **The authoritative register is [`design/interaction-states.json`](../../design/interaction-states.json).**
> It generates the `InteractionState` enum surfaces consume (`pixi run tokens`, drift-gated by
> `Aer.Architecture.Tests`) and the corpus's table in
> [`03-interaction-depth.md`](../design/03-interaction-depth.md) (`pixi run gen-states`, checked by
> `audit-completeness`), and carries each state's coverage — rendered today by named artifacts that
> must exist, or an explicit pointer to the work that will render it, enforced by
> `Aer.Architecture.Tests`. Absence is not a state, and now absence of a rendering is not silent
> either.

Two of the states have obligations elsewhere that are easy to lose: **Archived** presumes search exists,
which the corpus's stress test promotes from *"not yet"* to **required** at a hundred rooms and which
is currently scoped nowhere; and **Disconnected** requires the composer to keep accepting input while
offline, which is the same queue [0019](0019-consulting-is-not-deciding.md) and `#462` depend on.

**Easier.** #467 and #468 stop being bugs and become impossible. A new surface — a third client, a
future widget — inherits correct state by construction rather than by re-deriving it correctly. And
the interaction states above are decided once, centrally, rather than per screen.

**Harder.** This is a real constraint on the daemon's contract, not only on the UI. Every question a
surface wants to ask has to be answerable from the projected state, which means the projection grows
and each addition is a protocol change (#446). The tempting shortcut — a surface computing something
"just for display" — is exactly the thing forbidden, and it will be tempting precisely when the
protocol change feels disproportionate. It also interacts with staleness: a surface rendering a state
it fetched a second ago must mark it stale rather than blank it
([0018](0018-attention-is-the-primary-signal.md)), because "I don't know yet" is not one of the
states.

**Obliges us to** expose state rather than let surfaces infer it; make an unmatched state a build or
test failure rather than a fallback; keep one object behind every entry point to a gate; render a
failure's reason as content in the room, with the failing worker offered as the first way to fix it;
and treat any new "is it …?" predicate on a surface as a smell to be pushed back into the room.

**Relates to** [0018](0018-attention-is-the-primary-signal.md) — that record orders the list by
state, which presupposes exactly one state to order by. [0019](0019-consulting-is-not-deciding.md)
depends on a gate being one long-lived object rather than a modal per surface.
[0015](0015-three-kinds-of-needs-you.md)'s three kinds are properties of that one object, and its
ask-time persistence obligation is what keeps the object alive across a crash.

Related: `#467`, `#468` (the two defects this generalises), `#404` (drill-in shows full detail for
every outcome, not only failures), `#446` (per-session subscription — the protocol surface this
grows), `#482` (a failure offers the fix, with the worker that failed there to be asked).
