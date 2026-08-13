# 0054 — Participants, turns, and addressing: the multi-worker room's nouns

Status: accepted
Date: 2026-08-13

## Context

A room is about to hold more than one worker (M27, J11–J14/J18 — #493), and three shipped
decisions lean on the word "turn" without it being defined for that world:
[0019](0019-consulting-is-not-deciding.md) hands a consulted worker "the raising turn and its
attachments verbatim"; [0022](0022-permission-ladder-and-denial-is-an-answer.md) §5 kills a
pending gate "with its turn"; and the daemon serializes on a per-directory turn lock held for the
whole of a turn's execution. Today's `SessionTurn` is one human message plus one assistant
response, single vendor. That shape cannot express a two-worker exchange, and a gate-blocked turn
holding a room-wide lock would make 0019's consultation deadlock — the blocker #493 names.

Every derived worker label also fails as identity: a room with two workers of the same vendor at
the same model tier is a normal room (#493's scope, via [0017](0017-vendor-model-effort-are-three-choices.md)),
so neither "claude" nor "claude · sonnet" identifies a participant.

This record was drafted, adversarially grilled by the operator (the addressing default, the cost
of a room-wide beat, same-vendor identity, and removal-vs-interrupt were each challenged), revised,
and ratified 2026-08-13 — the exchange is on #493.

## Decision

**1. A room holds participants, and the participant is the identity.** Room-scoped, unique,
auto-named on join (first "claude", then "claude-2"), user-renamable. Vendor, model, and effort
are **mutable properties** of a participant — 0017's three choices attached to the right noun.
Chips and transcript cards show the participant name; model is secondary text. The journal's
existing `WorkerId` is the seam. Swapping a participant's model does not change who the
transcript says was talking — the participant is the continuity; the properties are not.

**2. A turn is one prompt plus what it provokes, with identity, not a number.** 0019 needs "the
raising turn" as a reference; 0022 §5 needs to know which turn's end kills a gate — both consume
identity, neither a sequence. The transcript is already a timestamp-merge of streams (turns,
permission answers, dormancy transitions); any ordinal is display sugar derived at render time,
never a journaled domain fact.

**3. Serialization is per participant; the room never blocks.** Each participant processes one
turn at a time; the room is as busy as its members. The room-wide turn lock's jobs migrate to a
per-participant turn queue plus a lock held strictly around metadata writes (the
lock-and-re-read-inside pattern the #1179 review mandated). An operator talks to one participant
while another works.

**4. Addressing is tagging — participants are members, like a chat channel's.** A tagged message
is that participant's turn. An untagged message is posted *to the room*, and the room's
structural answerer is the orchestrator — [0032](0032-room-orchestrator-is-mandatory.md)'s own
definition, "where an otherwise ambiguous routing choice is credited." What the orchestrator does
with it (including handing it to the participant who owns the conversational thread) is worker
intelligence, where intelligence belongs; Flow inspects no content (Architecture Rule 1). The
composer shows the current tag as a sticky, visible chip: who receives a message is never a
surprise.

**5. A consultation is the consulted participant's own turn, carrying a raised-by reference.** It
queues on the consulted participant like a question to a busy colleague; the raising turn stays
open awaiting the result. A gate raised mid-consult dies with the *consult* turn (0022 §5's rule,
applied to the turn that actually raised it), never destroying the raising participant's work.
**Named open point:** with the room unblocked, mutual consults (A consults B while B consults A)
can deadlock on each other's queues — the consultation slice must ship a cycle rule (bounded wait
or refuse-on-cycle) with a red-proof before 0019's behaviour is built.

**6. The orchestrator is a room-object property.** Implicit first assignment at creation (0032 —
no gesture; there is no one else to choose), atomic reassignment thereafter (#592 builds the
control), authority carried as an ordinary auto-bound skill
([0033](0033-skills-attach-directly-no-persona.md)). Exactly one, always; a participant holding
the role cannot be removed without reassigning first.

**7. Removal is interrupt plus a departure record.** No graceful-stop protocol exists to lean on
— the vendor CLIs have none; what exists is the M4 cancel handle, and crash reconciliation
already heals a hard cancel (the idempotent gate revoke that turn-end, scheduled expiry (#1113),
and startup reconcile all reuse, pinned by `RestartGateRepresentationTests`). Removal =
cancel any in-flight turn, revoke that participant's pending gates (`WasRevoked`), journal the
departure. History keeps the departed participant's name — present or absent like a person in a
thread means the thread keeps the messages of the person who left. Membership is episodic:
re-adding creates a new membership that may reuse the name; no deeper continuity is claimed.

## What this deliberately does not decide

The consultation behaviour itself (0019's command surface, [0024](0024-one-command-surface.md))
and the multi-worker composer UX beyond the sticky tag chip — separate issues. This record fixes
the nouns they build on.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Nothing consumes a room-global turn sequence as a domain fact | 0019/0022 read as identity references; the transcript renders a timestamp merge of streams (#1142–#1185); `TurnIndex` is per-session display metadata | a total order must be journaled and per-participant concurrency needs a sequencer |
| Per-participant concurrency does not break metadata safety | the metadata write is guarded by lock-plus-re-read, not by the beat (PR #1182's reviewed fix) | writes need a coarser lock and the room partially re-serializes |
| A hard cancel leaves healable state | the turn-end/expiry/reconcile paths share one idempotent, journaled gate revoke (#1113 reuses it; `tests/Aer.Daemon.Tests/RestartGateRepresentationTests.cs` pins it, red-proven) | removal needs its own reconciliation design before shipping |
| `SessionTurn` tolerates additive evolution | five trailing-optional additions in one week (`ErrorMessage`, `IsDormancyAnswer`, `IsExhausted`, `ExhaustedUntil`), old metadata loading unchanged each time | attribution/contribution shape needs a versioned metadata migration |

## Consequences

**Easier.** J11–J14 and J18 gain their object model; #1163 (two-worker phone transcript) and #592
(reassignment) unblock; the dormancy turn's "Swap orchestrator…" affordance gains its mechanism;
0019's consultation stops being deadlocked by construction.

**Harder.** Retiring the room-wide turn lock is a concurrency change to the daemon's most
carefully serialized path — it inherits every lesson the #1179 review taught about last-writer-
wins metadata, and slice 2 must re-prove them under per-participant interleaving. The consult
cycle rule is new obligatory design.

**Build slices (each its own measured issue):** (1) participant object model — identity, naming,
properties, journal events, projection/wire/chips; (2) turn identity + per-participant
serialization; (3) addressing — tag chips, sticky tag, untagged-to-orchestrator; (4) #592
reassignment control; (5) removal; (6) 0019 consultation behaviour including the cycle rule.

Related: #493 (the umbrella and the ratification exchange), [0013](0013-room-is-the-user-facing-noun.md)
(participants live inside the one room noun), [0037](0037-permission-answers-never-share-the-turn-lock.md)
and [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) (unchanged by this record, verified
against it).
