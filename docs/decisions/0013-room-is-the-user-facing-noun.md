# 0013 — Room is the user-facing noun; session is the vendor's

Status: accepted
Date: 2026-07-24

Amends [0001](0001-two-nouns-workflow-and-session.md). It **renames; it does not remodel** — 0001's
room-with-participants model, floor-passing and child tree are unchanged and this record is built on
them.

## Context

[0001](0001-two-nouns-workflow-and-session.md) deleted "task" and settled on two nouns, then amended
itself to say the session **is** a room. That left the product with a word doing two jobs, and a
ground-up design pass over the whole UX made the collision unavoidable: the owner, reviewing it,
said plainly *"I don't know that it makes sense to split out session and room into two different
nouns either."* Introducing "room" as a **third** noun alongside session would have re-run exactly
the mess 0001 cleaned up.

But the collision is not between "room" and "session" as synonyms. It is that **"session" already
names three different things in the running code**, one of which is genuinely not ours:

| In code | What it is |
|---|---|
| `SessionMetadata.SessionId` (`src/Aer.Daemon/Program.cs:1113`, `:1448`) | AER's id for the thing you opened |
| `CurrentVendorSessionId` (`src/Aer.Daemon/Program.cs:1298`, `:1618`, `:1903`) | the **vendor CLI's** resumable thread |
| `invocation.SessionId` → `--resume` (`src/Aer.Adapters/ClaudeWorkerAdapter.cs:133-143`) | what gets handed to `claude` on the command line |

Two of those live in the same file under near-identical names. And the vendor one is not even
uniform across vendors: `claude` gets a **client-minted GUID** before the process starts
(`Program.cs:1290`, `:1618`), while `agy` has none until one is **scraped back out of its log file**
(`Program.cs:1851-1859`). That is a vendor implementation detail with a vendor's failure modes —
`--resume` of an id the vendor never established is rejected outright (`Program.cs:1614`, `:1962`).

Meanwhile the phone tells the operator **"Started session $startedSessionId"**
(`src/Aer.Mobile/lib/inbox_screen.dart:402`) using AER's id. So the one place the word is shown to a
person uses a different referent from the one the CLI flag uses.

## Decision

**The user-facing noun is *room*.** A room is the conversation you opened: it has participants, it
has a directory, it holds its own history, and it is the thing that appears in a list, gets a
notification, and can be left and returned to.

**"Session" narrows to the vendor's resumable session** — the thread a particular CLI can be asked
to continue with `--resume`. It is **one per participant per room**, it is an adapter concern, and
**it is never presented as the thing you opened.** It may surface in diagnostics; it may not appear
in a list, an empty state, or a notification.

This satisfies [0002](0002-one-vocabulary.md) rather than reopening it. 0002 forbids *two words for
one thing*. Room and session are **two words for two things** — one is a conversation with people in
it, the other is a vendor process's resumable transcript, and their lifetimes already differ in the
code: a handoff mints a brand-new vendor session id while the room continues unbroken
(`Program.cs:1908`). The old usage failed 0002 precisely because it used one word for both.

**Workflow is unchanged.** [0001](0001-two-nouns-workflow-and-session.md)'s first noun stands as it
is; see [0014](0014-shapes-are-a-list-not-a-canvas.md) for how an authored shape is presented.

### A room is bound to one directory

The room's directory is the working root every participant is pointed at. It is **not required to be
a repository** — the owner's own daily use is a folder sitting *above* two repositories, and that
must keep working. This is already how the engine behaves rather than a new constraint: turns
serialise on a lock keyed by directory (`SessionTurnLockKey(directoryPath) => AerPaths.RecordKey(...)`,
`Program.cs:1513`), so the directory is the real unit of exclusion whether or not the UI says so.

**A room spanning several disjoint directories is explicitly deferred, not rejected.** Nothing here
forecloses it; it is simply not designed, and the directory-keyed lock would need rethinking first.

## Consequences

**Easier.** Every list, empty state and notification has a word whose referent is a person's mental
object. The three-way `SessionId` collision above becomes a naming bug with an obvious fix rather
than an ambiguity to reason around each time it is touched.

**Harder.** This is a rename across the daemon, both clients, the specs and the storage layout —
`~/.aer/sessions/`, `session.json`, `SessionMetadata`, the `/api/sessions` routes and the mobile
strings. Larger than 0001's "task" deletion, because unlike "task" this word is **kept** with a
narrower meaning, so every site must be *classified* (ours → room, vendor's → session), not
find-and-replaced. A blind rename would corrupt exactly the `--resume` path that is hardest to test.

**Obliges us to** keep `CurrentVendorSessionId` named as the vendor's, audit every operator-visible
string for the old referent (starting with `inbox_screen.dart:402`), and extend
[0002](0002-one-vocabulary.md)'s vocabulary table with both entries so the distinction is enforced
rather than remembered.

**Does not change** anything decided in [0001](0001-two-nouns-workflow-and-session.md),
[0009](0009-session-lifecycle-and-retention.md) or [0008](0008-runtime-streaming-over-append-log.md)
about *behaviour*. The tree, the retention rule and the streaming model are as recorded there; only
the word facing the operator moves.
