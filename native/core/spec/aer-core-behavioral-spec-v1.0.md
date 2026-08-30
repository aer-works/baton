> **Superseded.** This is the v1.0 spec covering M1–M3. The current authoritative document is
> [`aer-core-behavioral-spec-v1.1.md`](aer-core-behavioral-spec-v1.1.md), which adds the
> Observation Tier (M4b), ExitReason (M4c), on-demand cancellation (M4c), and the FFI boundary (M4).

---

# AER Behavioral Specification — v1.0

This document is the authoritative definition of what AER guarantees. Code is derived from this; this is not derived from code.

---

## 1. State Machine

```
Created ──spawn──▶ Running ──exit──▶ Exited
```

**Rules:**
- Transitions are strictly one-directional. No backward transitions, no self-transitions.
- `Created` is the initial state of every task execution.
- `Exited` is the only terminal state. No transitions out of `Exited` are valid.
- Invalid transitions are explicit errors, not silently ignored.

| From | To | Trigger | Valid |
|---|---|---|---|
| Created | Running | OS confirms spawn | ✓ |
| Running | Exited | OS confirms process termination | ✓ |
| Any | Any other | — | ✗ error |

---

## 2. Event Model

Events are the observable output of a task execution. The state machine is internal; events are the external contract.

| Event | Trigger | Fields | Guaranteed ordering |
|---|---|---|---|
| `Started` | Immediately after OS confirms spawn | `pid: u32` | Always before `Exited` |
| `Exited` | After OS confirms process termination | `code: i32` | Always after `Started` |

### Exit code mapping

| Condition | `code` value |
|---|---|
| Normal exit | OS exit code (0–255 on Unix; 0–4294967295 on Windows, stored as i32) |
| Killed by signal (Unix) | `-1` (sentinel; future milestones may use `-signal_number`) |
| Killed by timeout | `-1` |
| OS provides no exit code | `-1` |

---

## 3. Ordering Invariants

These invariants are enforced by the state machine and validated by integration tests. All are required to hold in every milestone.

1. **Started precedes Exited.** `Started` is always the first event; `Exited` is always the last.
2. **Exactly one Started per run.** A successful `Task::run()` emits `Started` exactly once.
3. **Exactly one Exited per run.** A successful `Task::run()` emits `Exited` exactly once.
4. **No events on spawn failure.** If the OS refuses to spawn the process, neither `Started` nor `Exited` is emitted and `run()` returns an error.
5. **Exited is terminal.** No event is emitted after `Exited`.
6. **Exited fires even on timeout.** If the process is killed due to a timeout, `Exited { code: -1 }` is still emitted before `run()` returns `Err(TimedOut)`.
7. **No descendant process survives `run()` returning.** After `run()` returns (for any reason — natural exit, kill, or timeout), all processes that were part of the spawned process tree are dead.

---

## 4. Execution Semantics

- **Single-shot only.** One `Task::run()` call = one process execution. No reuse.
- **Synchronous.** `run()` blocks until the process exits.
- **Byte-level I/O.** stdout/stderr are captured internally to prevent pipe-buffer deadlock. They are not surfaced to callers in M1 or M2.
- **No PTY/terminal emulation.**
- **Optional timeout.** Set via `Task::with_timeout(Duration)`. When not set, `run()` blocks indefinitely (M1 behavior).

---

## 5. Timeout Semantics (M2)

### Configuration

```rust
let task = Task::new("my-program", vec![])
    .with_timeout(Duration::from_secs(30));
```

`with_timeout` is a builder method. Tasks without it behave identically to M1.

### Kill sequence

When the timeout elapses and the process has not yet exited:

| Platform | Sequence |
|---|---|
| Unix | SIGTERM → wait 5 seconds → SIGKILL |
| Windows | `TerminateProcess` immediately |

The 5-second grace window on Unix gives the process a chance to handle SIGTERM and exit cleanly. SIGKILL is sent unconditionally after the grace period regardless of whether the process responded to SIGTERM. On Windows there is no reliable graceful kill for arbitrary console processes; `TerminateProcess` is used directly.

### Return value on timeout

`run()` returns `Err(AerError::TimedOut)` after emitting `Exited`. The `Started → Exited` invariant is preserved even when the process is killed.

### New error variants (M2)

| Variant | Meaning |
|---|---|
| `TimedOut` | Process was killed because the timeout elapsed |
| `KillFailed(io::Error)` | The kill attempt itself failed (rare; process may have already exited) |

---

## 6. Process Tree Semantics (M3)

After `run()` returns — regardless of whether the process exited naturally, was killed by a timeout, or was killed explicitly — **no descendant process of the spawned root shall remain alive.**

This guarantee is transparent to callers: no new API is required.

### Platform implementations

| Platform | Mechanism |
|---|---|
| Windows | Job Object created at spawn; `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` flag ensures the entire tree dies when the job handle is closed. On timeout, the monitor thread calls `TerminateJobObject` (kills all processes in the job atomically, closing inherited pipes and unblocking `wait`). |
| Unix | `setsid()` called in the child before exec, making the child the process group leader (pgid == pid). On kill, `killpg(pgid)` broadcasts the signal to the entire group. |

### Why TerminateJobObject is required on Windows (not TerminateProcess)

Grandchildren inherit the root's stdout/stderr pipe handles. `wait_with_output()` waits for EOF on those pipes. If only the root process is killed via `TerminateProcess`, grandchildren keep the pipes open and `wait_with_output()` hangs forever. `TerminateJobObject` kills the entire tree simultaneously, which closes all inherited pipe handles and immediately unblocks the wait.

---

## 7. Milestone Definitions

| Milestone | Adds | Status |
|---|---|---|
| M1 | Core scaffold, state machine, STARTED/EXITED events, single-shot execution | ✓ Complete |
| M2 | Configurable timeout, kill escalation (SIGTERM → SIGKILL / TerminateProcess) | ✓ Complete |
| M3 | Process tree cleanup (Job Objects on Windows, setsid on Unix) | ✓ Complete |
| M4 | FFI boundary (C-compatible ABI) | Pending |
| M5 | .NET binding (P/Invoke wrapper) | Pending |
| M6 | Python binding (ctypes/cffi wrapper) | Pending |

---

## 8. Behavioral Invariants (design targets for future milestones)

The following invariants are not yet enforced but the code must be structured to eventually enforce them:

- No event is emitted after the terminal state (already structurally guaranteed by M1 state machine).
- No duplicate terminal events per task (already structurally guaranteed by M1 state machine).
