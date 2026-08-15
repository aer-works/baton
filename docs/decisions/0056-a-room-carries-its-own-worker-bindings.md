# 0056 — A room carries its own worker bindings

Status: accepted
Date: 2026-08-14

## Context

Deciding a paused step resumes the room, which needs to know the room's workers. Until #1230 the
daemon learned them from **one user-global slot**, `BindingsPathHolder.BindingsFilePath`, filled by
whichever run, session start, or `/api/rooms/open` happened last.

That produced two failures, one after the other:

- **Empty slot.** The decision was accepted with a 200 and silently dropped — the phone showed
  "Approved review" over a room that never moved. Closed by #1227, which refuses instead.
- **Wrong slot.** A slot holding *some* readable bindings passes that check whether or not the file
  has anything to do with the room being decided. Two rooms with different workers, decided in either
  order, both resolve against whichever path was set last, so a workflow is **dispatched to the wrong
  workers with no refusal and no signal at all**. That is #1230, and it is worse than the case #1227
  closed: a refusal is visible, a wrong dispatch is not.

The posture that produced this — "bindings are never persisted in a room directory" — is recorded as
an M14 Phase 2 note in `docs/milestone-history.md`. Three things are true of it:

**It has no authority.** `CLAUDE.md`'s repo map labels that file, in as many words, "provenance, never
authority." There is no ADR on the question. This record is the first authority-grade one, not a
reversal of one.

**The tree already abandoned it, four times over.** `/api/templates/run` writes
`Path.Combine(roomDirectoryPath, "bindings.json")`; `aer dispatch` does the same;
`RuntimePermissionGrantAmender` — the shipped persistence for
[0022](0022-permission-ladder-and-denial-is-an-answer.md)'s ladder — **requires** the room's own
`bindings.json` and returns `CouldNotPersist` without it, so a room's standing permissions already
live in that file; and `/api/sessions/send` already assigns the global slot
`Path.Combine(directoryPath, "bindings.json")` before dispatching a turn, which is this record's rule
applied to chat and only chat. A posture four shipped paths contradict is drift, not a decision.

**Its rationale does not reach this case.** The note's content was that bindings are *asked for on
every Run, never inferred* — a rule about the Run dialog. This record preserves that exactly: Run
still asks, and the room's copy is the **record of the answer**, not an inference. Deciding-resumes-
the-room postdates M14 entirely.

## Decision

**1. A room keeps its own `bindings.json`, and that copy is the register.** Run copies the
operator-chosen file into the room, overwriting any prior copy — re-binding stays an explicit per-run
choice, made in the dialog, recorded in the room.

**2. A decision resolves the room's own file and only that.** `BindingsPathHolder` comes off the
decide path. It survives solely as the Run dialog's pre-fill convenience — "the file you chose last
time", which is what it was always good for.

**3. A room with no bindings of its own is refused, and the refusal names a real remedy.** Run the
room once, choosing its workers, and it carries them from then on. #1227's message named no remedy
because there was none; this record builds one, so the message can.

**4. The unstick path is a client supplying the file on the decision itself.** `DecideRoomRequest`
gains an optional `BindingsFilePath`, consulted **only** for a room that has none of its own: it
materializes the room's copy and then decides. A room that already has bindings ignores it entirely,
so a stale or wrong path can never redirect a room that knows its own workers. This is the contract
`aer decide` has always had, where `DecideOptions.BindingsFilePath` is required — so the CLI was never
subject to the defect this record fixes.

*Stated plainly: **no HTTP client populates that field today.*** `RoomClient.DecideAsync`'s daemon
branch constructs the request without it, and desktop and phone both route through that method. The
field is the mechanism, not yet a path anyone walks — so for a daemon-mediated client the working
remedy is the refusal message's, decision 3's. Teaching `DecideAsync` to populate it (from the same
last-used store `/api/rooms/open` already reads) is a separate, small piece of work, and saying so
here is what keeps this decision from reading as more shipped than it is.

**5. Rooms made before this heal on first use** rather than needing a migration: the first decision
from a client that knows the file gives the room its copy.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The M14 Phase 2 note is not authority | it lives in `docs/milestone-history.md`; `CLAUDE.md`'s repo map calls that file "provenance, never authority" | this record must be written as an amendment to a real decision, and the case for it argued against that decision's own reasoning |
| Four shipped paths already put bindings in the room | `/api/templates/run` (`Program.cs`); `DispatchCommand.cs:50-52` writes `<roomDir>/bindings.json` before running; `RuntimePermissionGrantAmender` requires it and returns `CouldNotPersist` otherwise; `/api/sessions/send` assigns the per-room path before a turn | the posture is live rather than drifted, and centralising on the room is a change of direction rather than a completion of one |
| The room's copy is enough to dispatch | it is the same file `aer run`/`aer decide` take on the command line, byte for byte a copy of what Run was given | a room needs more than the bindings file to resume, and the copy is necessary but not sufficient |
| A per-run overwrite does not lose anything | re-binding is an explicit choice made in the Run dialog; the prior copy described the prior run, which has already happened | rebinding needs history, and the room needs versioned bindings rather than one file |

## Consequences

**Easier.** #1230 closes. A room becomes self-describing: what it is, what happened in it, and now who
runs it, all in one directory. A phone "Run it again" becomes possible later for the same reason: the
room knows its own workers, so a client that cannot supply a bindings file no longer needs to.

**One gating condition removed, not two.** An earlier draft of this record claimed rooms started
through `/api/rooms/run` "gain the ability to persist a 0022 ladder answer at all". That was wrong,
and #1230's second reader caught it. Two conditions gate `RuntimePermissionGrantAmender.AmendAsync`:
the room's `bindings.json` must exist — which this record fixes — **and** it must contain an entry
under the worker name the amender looks up. That name is hardcoded to
`InteractiveSessionMaterializer.DefaultWorkerName` (`"chat-worker"`), never the worker that actually
asked, so a workflow room whose bindings key their own step workers still returns `CouldNotPersist`.
This record removes the first condition and does not touch the second.

**Harder.** A room directory now contains an operator-authored file that AER copies, so the room is no
longer purely AER-owned output — deleting a room deletes a copy of something the operator wrote, which
is already true of the three paths that did this before. And a bindings file edited after a run is not
picked up by an already-bound room until it is run again; that is the same "the copy records the
answer" property that makes the decision correct, but it will surprise someone who edits the source
file expecting a live link.

**Not covered.** Whether the global slot should survive at all for the Run dialog's pre-fill (it does,
unchanged), and how a standing permission is revoked ([0055](0055-an-authority-grant-is-not-a-standing-permission.md),
#1238).

Related: #1230 (the measurement), #1227 (the empty-slot refusal this completes),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (whose persistence needs the room's copy),
[0055](0055-an-authority-grant-is-not-a-standing-permission.md) (the standing permission that lives in
this file), [0013](0013-room-is-the-user-facing-noun.md) (the room as the thing that holds its own
state).
