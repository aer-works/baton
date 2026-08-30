# 0016 — Memory belongs to the room, not the worker

Status: accepted
Date: 2026-07-24

## Context

[0012](0012-what-aer-flow-is.md) makes a specific promise it must be able to demonstrate: *a fact
established by one vendor is used by a different vendor later in the same room.* Nothing in the
product delivers that today, and the reason is architectural — memory is implicitly the worker's.
Each vendor CLI keeps its own resumable session ([0013](0013-room-is-the-user-facing-noun.md)), and a
fact `claude` learned lives in `claude`'s session, invisible to `agy` in the same room.

This is also the unanswered half of the owner's own question during the design pass: *are the
codebase's documents and AER's own documents the same kind of thing to the operator, or do we
abstract AER's away and just let the workers "interact"?* #442 tracks it. The two questions are one
question — "whose memory is it, and does the operator see it" — and answering it for cross-vendor
sharing answers it for visibility too.

## Decision

**The room owns its memory. Participants read and write a shared, room-scoped memory; no worker
privately holds the room's context.** A fact one vendor records is, by construction, available to
every other participant in that room — which is what makes
[0012](0012-what-aer-flow-is.md)'s cross-vendor demonstration true rather than aspirational.

**Room memory is an ordinary working document — visible, versioned, editable by the operator.** It is
not a hidden store the product infers behind the scenes. You can open it, read what the room believes,
correct it, and delete from it, the same as any file the room works on.

**Workers propose additions; the product never infers them.** A worker can *suggest* "remember that
the auth module owns token refresh," and that suggestion is a proposal the operator (or the room's
rules) accepts — Flow does not read conversation content and silently extract memories from it. This
is CLAUDE.md Architecture Rule 1 again: discipline in Flow, intelligence in Workers. Inferring
memory from content would put Flow in the business of understanding output, which it must never do.

### Two kinds of document, kept distinct

The owner's question resolves into a rule about what the operator sees:

- **Working files** — the codebase, and the room's own memory document — are *the operator's*. They
  are shown, opened, diffed and edited as first-class content. Room memory sits in this class
  deliberately: it is a document about the work, so it lives with the work.
- **AER's bookkeeping** — the event log ([0008](0008-runtime-streaming-over-append-log.md)), session
  metadata, vendor session ids, the record store — is *the engine's*. It is never surfaced as a
  "file" the operator manages. It may appear in diagnostics; it is not part of the working set.

So AER's documents are **not** abstracted entirely away — room memory is promoted *out* of
bookkeeping into a visible working document precisely because the operator has a stake in it — while
the genuinely internal machinery stays out of sight. "Abstract it all away" and "show it all" were
both wrong; the line is *whether the operator has a reason to act on it.*

## Consequences

**Easier.** The multi-vendor promise becomes a property of where memory lives, not a synchronization
feature to build per pair of vendors. And "what does this room know?" has an answer the operator can
open and read, which is also the answer to "why did it just do that?"

**Harder.** A shared writable document with multiple worker writers needs a conflict story —
[0001](0001-two-nouns-workflow-and-session.md)'s floor-passing means only one participant acts at a
time within a room, which bounds it, but concurrent *proposals* and operator edits still need an
order. And "workers propose, operator disposes" needs a real accept/reject surface, or proposals pile
up unread.

**Obliges us to** give room memory a concrete storage location distinct from any single vendor's
session, decide how a proposal is surfaced and accepted (it is a fourth thing a room can ask of you,
adjacent to [0015](0015-three-kinds-of-needs-you.md)'s three — though a *proposal* is lower-stakes
than a *pause* and should not block the run), and keep the memory document inside the room's retention
boundary ([0009](0009-session-lifecycle-and-retention.md)).

**Relates to** [0011](0011-token-based-context-management.md): that record governs *when* context is
compacted (token thresholds); this one governs *whose* it is and *whether it is visible*. Compaction
operates on room memory; it does not change who owns it.

Related: #442 (the room-memory question), #445 (proposal/permission surfacing may share a mechanism).
