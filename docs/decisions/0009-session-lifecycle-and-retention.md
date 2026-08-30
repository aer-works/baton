# 0009 — Session lifecycle & retention: a tree you count the top of

Status: accepted
Date: 2026-07-22

## Context

The room model ([0001](0001-two-nouns-workflow-and-session.md)) makes sessions form a tree, and lets
a worker spawn children autonomously (the Claude Code *Task* pattern). Left unbounded, that is a
proliferation problem: a night of working through issues — with workers delegating to sub-agents that
delegate again — could accumulate thousands of sessions and an ever-growing pile of event logs. The
owner named this directly: *"I don't want to accumulate many thousands of those over a night."*

The append log ([0008](0008-runtime-streaming-over-append-log.md)) is durable per turn — but
"durable" must not be read as "keep every child's every turn forever," or the sound engine becomes
the thing that buries the user.

## Decision

**Hierarchy, retention, and a bound — so you count the top of the tree, not the tree.**

- **The top-level unit is the conversation you started.** Everything spawned — a worker you added, a
  review you spun off, an agent a worker delegated to — nests under it. You accumulate the sessions
  you *opened*; a night of work is a handful of them, however deep the tree beneath. This mirrors how
  a Claude Code conversation feels: one session, however many subagents it fired off inside.

- **Children are ephemeral by default.** When a child finishes, its **result is retained in the
  parent's record**; its full turn-by-turn transcript **collapses and is auto-archived** — recoverable
  if you go looking, out of the active view otherwise. The append log **compacts/archives completed
  subtrees**; retention is explicitly *not* keep-everything-forever. An explicit **keep** promotes a
  child to durable when it matters.

- **Worker-initiated spawning is bounded** by a depth/count ceiling — a looping or confused worker
  cannot explode the tree or the bill. This is the same family of rail as the worker-dialogue turn
  ceiling ([0001](0001-two-nouns-workflow-and-session.md)), and it doubles as a safety rail: the
  accumulation worry and J6's "a worker can't do what it shouldn't" are the same bound.

## Consequences

**Easier.** The room model scales to autonomous delegation without burying anyone. "Return after a
day and find what needs you" (J3) stays legible because you scan the sessions you started, not a
thousand children. Storage stays proportional to *kept* work, not to *all* work.

**Obliges us to.** Give the append log ([0008](0008-runtime-streaming-over-append-log.md)) a
compaction/archival step for completed subtrees — a real engine addition, not a UI filter.
Child-session lineage must carry its parent, its spawner (you or which worker), its depth, and its
kept/ephemeral state. The ceiling is enforced by the engine (discipline), since the request to spawn
comes from a worker (intelligence).

**Leaves open (build details).** Exact retention windows and archive location; whether an archived
transcript is compacted to a summary or merely moved; the default ceiling values. None are decided
here — they are tuned during the build.

Relates: [0001](0001-two-nouns-workflow-and-session.md) (the tree, spawning),
[0008](0008-runtime-streaming-over-append-log.md) (append-log compaction), #335 (multi-task daemon),
J3, and J6.
