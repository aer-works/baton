# 0044 — Memory belongs to the room, and changes only by decision

Status: accepted
Date: 2026-07-30

## Context

AER kept no orchestrator memory: findings survived only because a human wrote them down (#672).
The minimal form was settled in a live operator + orchestrator conversation on 2026-07-30,
recorded as a comment on #672 ("Minimal-form design … — the record for M26"); this record
promotes that conversation's three commitments into the register. The conversation is the
provenance; this file is the authority.

## Decision

1. **Memory's lifetime is coupled to the room directory, never to any conversation.** The place
   is the record; everything that runs inside it is episodic. Conversations, vendor sessions,
   and chapters end freely — memory carries because it was never theirs. Archiving a room
   shelves it and deletes nothing; only the operator deletes memory. **Forbidden coupling,
   recorded so nobody builds it:** memory must never be reconstructed from transcripts or
   "wrapped up" into an ending episode. This keeps both room models open — the operator's own
   forever-room-per-project pattern and rooms-that-end — under one rule. (The same argument as
   #778's vendor-portability case, one level up: there the vendor session couldn't be the
   record; here the conversation can't be either.)

2. **Form: a `memory/` directory of small fact files with a one-page index, in the room.**
   Memory is the curated belief set, not the transcript — its size tracks what the room
   currently believes; each fact is corrected in place or deleted and diffs cleanly on its own.
   The single-file form was rejected for the forever-room ("the ROOM.md file would get huge").
   The orchestrator reads the index at every turn start. Curation is a standing job: the
   janitor concept extends to memory — sweep for stale/contradicted/superseded facts and
   propose deletions through the same escalation surface as everything else.

3. **Proposal channel: a structured MCP tool; nothing writes memory but an approved decision or
   the operator's own editor.** The operator chose the clean solution over an interim one:
   build the AER MCP server (0029's measured-viable, never-built channel) with memory-edit
   proposal as its first tool, wiring both vendors. Cost accepted knowingly — one new component
   on the critical path, bought once; 0035's `aer yield`, permission requests, and every future
   structured worker→AER signal ride the same rails. Proposals escalate as held work and wait
   for that decision.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The operator's live pattern is one forever-room per project, with rooms-that-end kept possible | **stated by the operator** in the #672 design conversation, 2026-07-30 | the lifecycle rule could couple memory to something episodic and lose nothing |
| A structured worker→AER channel exists and is vendor-portable | **measured** — 0029's mechanism table and 0035's shipped `aer yield` host (#585); the memory-proposal tool itself shipped in #801 | proposals need a prose-parsing channel, which Architecture Rule 1 forbids the engine to read |
| Proposals can reach a person through existing escalation | **measured** — #801's `MemoryProposalEscalation` dispatches room held work via the unmodified `RoomMutationInterface`; room-attribution for multi-room capture is open as #833 | the tool captures into a void and the "only a decision writes memory" rule has no enforcement point |

## Consequences

**Easier.** Facts survive orchestrator sessions, vendor swaps, and conversation ends without a
human transcribing them; every memory edit has a decision trail; curation uses the machinery
that already exists (janitor, held work, decisions).

**Harder.** Memory writes gain latency (a proposal must be decided, not just written), the MCP
server is a new critical-path component, and the `memory/` reading/writing half plus the
curation sweep remain to be built (#672's other halves; capture attribution per #833).
