# 0001 — Two nouns: workflow and room

Status: accepted
Date: 2026-07-22 (room model added 2026-07-22)

## Context

One object is called four things, and two of them are navigation destinations sitting next to each
other in the same rail. Observed in a single sitting on the running product:

```
desktop rail    Task (singular) and Tasks (plural) — two separate destinations
desktop Chat    "No room open."  /  "Start new chat"
mobile home     "No task is open on the host yet."  /  "Browse recent tasks"
on disk         ~/.aer/sessions/   and   ~/.aer/tasks/
engine + spec   workflow
```

A rail carrying both *Task* and *Tasks* as distinct destinations is the clearest single symptom that
the vocabulary was never one thing. Underneath, a room already **is** a task: same single-step
supersede chain, same event store, same projection loader. It just gets a second metadata file and a
separate root, which is why `isSession` special-casing (the code-level name; see
[0013](0013-room-is-the-user-facing-noun.md) for why the code name and the product noun differ) is
scattered through daemon and UI alike.

## Decision

**Workflow** — the authored shape you edit and reuse.
**Room** — a running instance of one, a multi-participant conversation against a directory.

**"Task" is deleted from the product's vocabulary entirely.** It was the redundant third name that
made "room" and "task" read as interchangeable. ("Session" narrows to a different, more specific
concept — the vendor CLI's own resumable thread — rather than being deleted;
[0013](0013-room-is-the-user-facing-noun.md) draws that line.)

A chat is a room whose workflow is the conversation shape. A review run is a room whose workflow is an
authored pipeline. See [0003](0003-templates-collapse-to-three-shapes.md).

### The plan is a definition; its execution is a room

In code the two halves carry distinct identifiers, and the split is load-bearing: `WorkflowDefinition`
and `WorkflowTemplate*` name the authored **plan** (the reusable shape); bare `Workflow*` names the
**execution** of one, bound to a room. There is deliberately **no "workflow run" noun** — a running
workflow *is* the room (per the Decision above), so the running instance is never named a second time.
A surface or record that reintroduces "workflow run" has split the one running-instance noun back in
two, which is the drift [0002](0002-one-vocabulary.md) forbids.

### A room has participants

The two nouns hold, but the running instance was underspecified — a design pass over #312 (J2: how a
chat expands into a workflow) showed a room is not a single you↔worker thread. It is a **room with
participants.** You and every worker in it are participants, and two things vary independently:

- **Participants** — solo (you + one worker), or several workers in the same room.
- **Structure** — free conversation, a defined turn order, or a gated pipeline.

"Add a worker," "have two workers talk to each other," and "spin off a review" are not different
objects — they are settings on those two dials over the one primitive. A review run is a room with an
agenda and a gate; a chat is the same room with neither. Worker-to-worker dialogue works by
participants **passing the floor explicitly** (a tool-return: "over to you" / "done") — the engine
facilitates and enforces the budget and the gate, but never reads the content to decide who speaks
next (this is "Flow carries discipline, Workers carry intelligence" restated). The person always has
a seat: exchanges stream live and can be interrupted.

**Rooms form a tree.** A room can spawn **child rooms** — a review it kicks off, or an agent one of
its workers delegates to. Spawning is **one primitive regardless of who asks:** you spawn a child the
same way a worker does — the worker emits it as a tool-return (the Claude Code *Task* pattern), and
the engine creates and runs the child. "Who spawned it — you or a worker" is a field on the child, not
a different mechanism. Children are **clearly marked as children** wherever they appear, and resolve
both inline in the parent and in the global inbox.

**The conversation you started is the top-level unit.** Everything spawned nests under it; you
accumulate the rooms you opened, not the tree beneath them. How that tree is retained and bounded is
[0009](0009-session-lifecycle-and-retention.md); how a turn runs inside a room is
[0008](0008-runtime-streaming-over-append-log.md).

## Consequences

**Easier.** Every list, empty state and status line has one word available for the thing it is
showing. The `Task` / `Tasks` rail split stops being defensible, which is a feature — it forces the
IA question rather than letting it hide behind vocabulary. And "add a worker," "spin off a review,"
and "a worker delegates" collapse to one thing to build and reason about, not three.

**Harder.** Two storage roots (`~/.aer/tasks`, `~/.aer/sessions`) encoded the distinction being
deleted, and a third exists for authored workflows under `OneDrive\Documents\AER Flow`. #443 unified
the first two into `~/.aer/rooms` and deleted the copy-only migration — there was no user data to fold. The room model adds real engine work — multi-participant
turns, floor-passing, and child-room lineage need new event types — which **retires the estimate that
the multi-task daemon (#335) needs zero `Aer.Flow` changes.**

**Obliges us to** rename in code as well as UI — see [0002](0002-one-vocabulary.md) — update the
behavioural spec's normative terms, and bound worker-initiated spawning with a depth/count ceiling
([0009](0009-session-lifecycle-and-retention.md)) rather than leaving it open.
