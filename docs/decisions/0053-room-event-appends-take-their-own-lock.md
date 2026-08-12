# 0053 — Room-event appends take their own lock, not the flow engine's

Status: accepted
Date: 2026-08-12

## Context

`ConcurrencyGuard` protects a single kernel file lock per room directory (`flow.lock`), and today
**every** writer takes it: the flow engine's execution path (`session.RunAsync`, which holds it for
the entire run) *and* every `RoomMutationInterface` method, which only ever appends `RoomEvent`s to
`room.jsonl` and never touches `flow.jsonl`.

The live drive of #1109 (2026-08-12, recorded on the issue) measured the consequence: during an
interactive turn the engine holds `flow.lock` for the whole execution, so the doorbell's fail-fast
`RaisePermissionAsync` can never journal the pending-permission ask mid-turn. The ask never enters
the room projection, no surface ever shows the gate, and the worker always blocks to the 180-second
timeout and is denied. The terminal cleanup shipped in #1102 works; the interactive answer path is
unreachable by construction. A retry cannot fix this — no bounded wait outlives a lock held for a
whole turn.

The two logs are already independent by contract: spec §178 requires per-log atomic line-boundary
appends, and §186 forbids replay from ever depending on cross-log file order or timestamps —
matching is by `ExecutionId`. Spec §15's single-writer requirement says explicitly that "the
mechanism itself is not part of the contract and may change."

## Decision

**Room-event appends are serialized by their own per-directory lock (`room-events.lock`), not by
the flow engine's `flow.lock`.** Every writer of `room.jsonl` — all of `RoomMutationInterface`, and
anything added later — moves onto the room-events lock together; the flow engine's execution and
`flow.jsonl` writers keep `flow.lock`. A component that writes neither log takes neither by
default and gets an explicit classification (e.g. `ArtifactPruner` stays on `flow.lock`, because
what it guards against is a live artifact pump, i.e. the executing engine).

This is the smallest change that makes the mid-turn ask journalable: the single-writer guarantee
each log needs (spec §15) is preserved per log, and since replay may never depend on cross-log
ordering (§186), nothing is entitled to the accidental cross-log serialization the shared lock was
providing.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The engine holds the room's `ConcurrencyGuard` for the whole interactive turn, so a fail-fast room append mid-turn always fails | **measured** — #1109 live drive, 2026-08-12: real ask, `WorkflowLockedException` on the doorbell path, worker denied at the 180s timeout | a bounded retry would suffice and no lock split is needed |
| Replay never depends on cross-log order between `flow.jsonl` and `room.jsonl` | spec §186 (matching by `ExecutionId`), and `StateProjector`/`RoomProjector` are log-disjoint, read directly | splitting the lock could reorder events replay depends on, and the shared lock was load-bearing |
| `RoomMutationInterface` writes only `room.jsonl` | **measured** — every method builds a `RoomEvent` for `IRoomEventLogWriter`; none touches `flow.jsonl` (read directly, recorded on #1109) | moving it off `flow.lock` would leave a `flow.jsonl` writer unserialized against the engine |
| The locking mechanism is not part of the behavioral contract | spec §15: "the mechanism itself is not part of the contract and may change" | this change would need a spec amendment, not just an ADR |

## Consequences

**Easier.** A pending permission raised mid-turn journals immediately, enters the projection, and a
human can see and answer the gate while the worker is still waiting — the path #1109 proved
impossible today.

**Harder.** Two locks per room directory instead of one, so every future writer must be classified:
writes `room.jsonl` → `room-events.lock`; writes `flow.jsonl` or executes the engine → `flow.lock`;
neither → explicit decision, not a default.

**Obliges us to.** Keep the classification honest when adding writers, and prove the fix with the
red-first test this record was created for: hold `flow.lock` via `Acquire`, call
`RaisePermissionAsync`, and require it to succeed (it throws `WorkflowLockedException` today).

Relates: [0037](0037-permission-answers-never-share-the-turn-lock.md) (the same deadlock shape one
layer up — an answer path must never need a lock the pending turn holds; that record governs the
`SessionTurnLocks` semaphore, this one the file lock), [0029](0029-the-gate-is-three-mechanisms.md)
(the gate whose interactive half this unblocks).
