# 0057 — How a room's bindings register is written

Status: accepted
Date: 2026-08-15

## Context

[0056](0056-a-room-carries-its-own-worker-bindings.md) made a room's `bindings.json` the register of
who runs it, and said nothing about how that register is written. Five writers have since
accumulated, and the question surfaced from three directions at once while shipping #1249 and #1246.

**The writer is not atomic.** `WorkerBindingConfigWriter.SaveToFileAsync` goes through
`File.WriteAllTextAsync`, which truncates before it writes. Four daemon writers — the per-turn
rewrite, the session-mode update, the permission amend, the permission revoke — take
`ConcurrencyGuard.AcquireRoomEventsWithin` before calling it, and #1249's read route takes the same
lock on the grounds that the write it races truncates.

**That lock never covered the failure it was credited with.** A process killed between the truncate
and the write leaves a truncated register *while holding the guard*. A lock serializes live parties;
it is powerless against a crash. This is the exposure #1230's second reader named for
`MaterializeRoomBindings`, and it applies today to all four guarded writers. The guard was being read
as the consistency mechanism when it is only the serialization mechanism.

**One writer takes no lock and needs none.** `MaterializeRoomBindings` stages to a per-call temp file
and `File.Move`s it over. It is already the shape the rest should have.

**One writer takes neither, and writes a live room.** `BindingsEditorViewModel.SaveToFileAsync`,
reached from the desktop's Edit Bindings / Save, calls the writer directly at a path typed freely
into a text box. Nothing stops that path being a live room's register (#1257). Its call site's
comment says bindings are "a UI/CLI input, never durable room state" — a claim 0056 made false, and
which cites an M14 Phase 2 note 0056 explicitly dethroned.

**And one window has no answer at all.** Two decides against the same un-bound room can both pass the
`File.Exists` check and both materialise (#1262), newly reachable since #1246 gave the field a
caller.

Separately, [0053](0053-room-event-appends-take-their-own-lock.md) obliges a component that writes
neither log to take neither lock by default and receive **an explicit classification**. The four
bindings writers classified themselves onto `room-events.lock` in code comments. No record made that
classification. This record discharges that obligation.

## Decision

**Four rules, and they answer different questions — conflating them is how the guard came to be
credited with work it cannot do.**

**1. The register is written atomically.** `WorkerBindingConfigWriter.SaveToFileAsync` serializes,
stages to a uniquely-named sibling, and `File.Move`s over the target — the mechanics
`MaterializeRoomBindings` already carries. This is the floor every reader stands on, including
readers that cannot enlist in any lock and readers that arrive after a crash. It also completes that
method's own documented contract: "write nothing on failure" held only for serialization failures
before, and holds for I/O failures after.

**2. Read-modify-write writers take the room-events lock — the 0053 classification.** The per-turn
rewrite, the session-mode update, the permission amend and the permission revoke read the register,
change part of it, and write it back. Atomicity does not help them: two atomic writes still lose one
update. `bindings.json` writes neither log, and it goes on `room-events.lock` rather than `flow.lock`
because what they must not interleave with is each other and the room's own event appends, not the
executing engine.

**3. The first bind wins, and no lock is involved.** `MaterializeRoomBindings` gains a mode: overwrite
for Run, which 0056 rule 1 makes an explicit per-run choice, and first-bind-only for the decide
unstick path, expressed as `File.Move(overwrite: false)`. The move *is* the check, so the
check-then-write window stops existing rather than being guarded. A loser proceeds against the room's
copy, which is exactly what 0056 rule 4 already prescribes for a room that has bindings.

**4. A live room's register is rewritten only through the room's own surfaces.** The bindings editor
refuses to save over one, and says what to do instead: edit the source file and Run the room, or use
the room's permission surfaces for grants. Opening one read-only stays allowed — inspection is not
the hazard.

Detection is local evidence, never a path prefix: room directories arrive as free `DirectoryPath` on
every request and can live anywhere, so "under `AerPaths.Rooms`" would be both over- and
under-inclusive. The test is that the file is named `bindings.json` *and* its directory carries room
evidence (`room.jsonl`, `flow.jsonl`, `snapshot.json`, `flow.lock`).

### Why the editor is refused rather than made safe

Making the editor take the guard was the obvious third option and it is rejected. It costs the most
and fixes the least.

Acquiring the room-events lock calls `Directory.CreateDirectory` — #1249's read route checks the
bindings file's existence *before* guarding for exactly this reason — so an editor guarding an
arbitrary typed path would materialise room scaffolding for paths that are not rooms. What a person
would see is an authoring surface failing with a room-lock error about a room they never said they
were editing. And after paying that, rule 1's exposure is still open, because a lock cannot make a
truncate-write survive a crash.

Atomicity alone is also not enough, which is why rule 4 exists beside rule 1. A clean atomic rewrite
of a live register is still an out-of-band re-bind that 0056 confines to the Run dialog, and still a
lost update against the read-modify-write writers: an editor Save landing between the amend's read
and its write silently discards a standing permission — or resurrects a revoked one, atomically. A
revocation that silently un-revokes is the case this rule exists for.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `SaveToFileAsync` truncates before writing | `File.WriteAllTextAsync`'s documented behaviour, and the reason #1249's read takes the guard at all | rule 1 is unnecessary and the guard was always sufficient |
| A crash mid-write leaves a truncated register while the guard is held | a lock is released by process exit; the bytes are not restored | the guard already covers durability and rule 1 buys only concurrency |
| Stage-and-move is atomic on the platforms we ship | argued and accepted for `MaterializeRoomBindings` when it shipped; `File.Move(overwrite: true)` maps to `MoveFileEx`/`rename` | rule 1 relocates the window rather than closing it, and the register needs a different durability story |
| An atomic replace can lose to a reader on Windows, and a truncate-write cannot even start | **measured** — #1264's own test: with a reader looping `File.ReadAllText`, the replace raises `UnauthorizedAccessException` and the truncate-write raises `IOException` on its open. Neither happens on POSIX, where the reader instead observes a torn file | the bounded retry in `ReplaceWithRetryAsync` is unnecessary ceremony, and rule 1 costs writers nothing |
| A room with bindings ignores a supplied path | [0056](0056-a-room-carries-its-own-worker-bindings.md) rule 4, and `/api/rooms/decide` gates on `!File.Exists` | rule 3's loser is silently wrong rather than correctly ignored, and first-wins needs to report |
| The decide path dispatches against the room's own copy | `/api/rooms/decide` resolves the room's file inside the turn lock | the race decides which bindings *run*, not only which are recorded, and rule 3 is urgent rather than corrective |
| Room directories can live anywhere | `DirectoryPath` is free text on every room endpoint | rule 4's detection could be a path prefix, which would be simpler |
| `bindings.json` is neither `room.jsonl` nor `flow.jsonl` | it is worker setup, not an event log | 0053's default applies and no classification was owed |

## Consequences

**Easier.** #1257 and #1262 both close. Every reader of the register — including `aer run` on the
command line, a future second client, and anything reading after an unclean shutdown — sees one whole
file or another, with no coordination required and nothing to enlist in. #1249's read guard survives
but shrinks: its justification is no longer "the write truncates" but the narrower, Windows-specific
one that `File.ReadAllText` opens without `FILE_SHARE_DELETE`, so an atomic replace overlapping an
unguarded read raises a sharing violation. **That comment must be rewritten in the change that lands
rule 1**, or it spends an interval stating a reason that is no longer true.

**Harder, and measured rather than predicted.** Atomicity does not remove the contention a
truncate-write had — it moves it from the reader to the writer. On Windows `File.ReadAllText` opens
without `FILE_SHARE_DELETE`, so a replace landing while anyone is mid-read fails outright, which
`ReplaceWithRetryAsync` absorbs with a bounded retry rather than surfacing. That is the better place
for the contention: a failed write is reported to someone who can act on it, and a torn read is not.
The daemon's own readers take the room-events lock and never reach that path; it exists for the ones
that cannot, like `aer run` on the command line.

**Harder.** The bindings editor loses a capability it was never meant to have but did have. Someone
who has been hand-editing a live room's register — to fix a model name, say — now gets a refusal and
has to Run the room again. The refusal names that path, and 0056's "the copy records the answer"
property is what makes it the correct one, but it is a real loss for whoever was doing it.

**A cost rule 3 accepts deliberately.** The losing decide gets a 200 and no signal that its supplied
file was ignored. That is consistent — it is the same silence a decide one second later would get
under 0056 rule 4 — but it means "I supplied bindings and the room bound to something else" is
unobservable. Making it observable needs a response field, which is a wider change than this record.

**Not covered.** Whether a room's register should be versioned rather than overwritten per run
(0056's own "Harder" raised it and left it), and whether an unparseable register should fail with a
message rather than an unhandled exception (#1258).

Related: #1257 (the unguarded editor write), #1262 (the first-bind race), #1249 (the read guard whose
justification this narrows), [0053](0053-room-event-appends-take-their-own-lock.md) (whose
classification obligation this discharges), [0056](0056-a-room-carries-its-own-worker-bindings.md)
(which made the register a register and left concurrency open),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (whose standing permissions live in this
file, and whose revocation rule 4 protects).
