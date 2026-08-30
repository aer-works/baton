# AER Core — Implementation Plan

The behavioral spec (`spec/aer-core-behavioral-spec-v1.1.md`) is authoritative for what the system must guarantee. This document is authoritative for how we are getting there: which milestones exist, what is in scope for each, and where we currently stand.

---

## Milestones

### M1: Deterministic Spawn & Lifecycle Events ✓
Process spawn, `Started` / `Exited` events, state machine (`Created → Running → Exited`).

### M2: Timeout & Kill Escalation ✓
Configurable timeout; graceful → forceful kill escalation.

### M3: Process Tree Cleanup ✓
Kill the entire process tree, not just the root process. Windows Job Objects; Unix `setsid` + `killpg`.

### M4: Observation Tier & FFI Boundary ✓
`StdoutChunk` / `StderrChunk` events. On-demand cancellation via `CancelHandle`. C-compatible ABI (`aer.h`) with `aer_task_new`, `aer_task_run`, `aer_task_free`, `aer_cancel_new`, `aer_cancel_free`, `aer_cancel_request`.

### M5: .NET Binding ✓
*P/Invoke wrapper over the M4 C FFI. Prerequisite for AER Flow.*

| Issue | Title | Depends on |
|---|---|---|
| #59 | Project scaffold & raw P/Invoke layer | — |
| #60 | Safe handles | #59 |
| #61 | Callback marshalling | #60 |
| #62 | High-level managed wrapper & cancellation integration | #61 |
| #64 | Integration tests & docs | #62 |

**Complete** (PR #65 ✓ #59, PR #69 ✓ #60, PR #70 ✓ #61, PR #91 ✓ #62, PR #92 ✓ #64).

**Acceptance criteria — met:** CI passes 100% on Windows and Linux for all five issues; AER Flow can reference `Aer.Core` and call `AerTask` without any direct P/Invoke.

### M6: Python Binding
Deferred — no consumer exists yet.

---

## Completed Milestones

M1, M2, M3, M4, M5.

---

## Open Questions

None. (M5's original "the C ABI is frozen and mechanical" assumption did not survive contact: the 2026-07 review found Flow requires env/cwd control, added to the ABI in #77 before the managed wrapper froze its surface.)
