# AER Core Behavioral Specification — v1.1

This document is the authoritative definition of what AER Core guarantees. Code is derived from this; this is not derived from code.

This revision renames the project from "AER Runtime" to **AER Core** (sibling to AER Flow, AER CLI, AER Agents) and rewrites Milestones 4–6 to fold in the Observation Tier (stdout/stderr capture) that was agreed upon but never written down, and to reflect that Flow — not a Python adapter — is the next real consumer of this spec.

This revision also splits Exited's cause from its numeric exit code by adding a reason field (§2.3) — code alone could not distinguish a timeout-kill from a cancel-kill from an ordinary crash, all of which previously collapsed into the same -1 sentinel. Adds an abstract, on-demand cancellation capability (§7), required before Flow can support cancelling a running execution rather than only ones that hit their timeout. Renumbers Milestones (now §8) and Behavioral Invariants (now §9) accordingly, and folds cancellation into the M4 scope alongside the Observation Tier and FFI boundary, for the same reason Observation Tier was folded in: the FFI boundary is the contract Flow gets built against, and Flow needs to be able to request cancellation across it from day one.

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

| From    | To        | Trigger                         | Valid   |
| ------- | --------- | ------------------------------- | ------- |
| Created | Running   | OS confirms spawn               | ✓       |
| Running | Exited    | OS confirms process termination | ✓       |
| Any     | Any other | —                               | ✗ error |

---

## 2. Event Model

Events are the observable output of a task execution. The state machine is internal; events are the external contract.

### 2.1 Lifecycle events (always emitted)

| Event     | Trigger                               | Fields                                   | Guaranteed ordering    |
| --------- | ------------------------------------- | ---------------------------------------- | ---------------------- |
| `Started` | Immediately after OS confirms spawn   | `pid: u32`                               | Always before `Exited` |
| `Exited`  | After OS confirms process termination | `code: i32`, `reason: ExitReason` (§2.3) | Always after `Started` |

### 2.2 Observation events (opt-in — see §4)

| Event         | Trigger                                      | Fields                       | Guaranteed ordering                                                                                |
| ------------- | -------------------------------------------- | ---------------------------- | -------------------------------------------------------------------------------------------------- |
| `StdoutChunk` | Bytes available on the process's stdout pipe | `seq: u64`, `bytes: Vec<u8>` | Strictly increasing `seq` within the stdout stream; always after `Started`, always before `Exited` |
| `StderrChunk` | Bytes available on the process's stderr pipe | `seq: u64`, `bytes: Vec<u8>` | Strictly increasing `seq` within the stderr stream; always after `Started`, always before `Exited` |

`StdoutChunk` and `StderrChunk` are independent OS pipes with no cross-stream ordering guarantee. Each stream's `seq` is monotonically increasing *within that stream only*. Callers must not infer that a given `StdoutChunk` happened before or after a given `StderrChunk` based on `seq` values alone — only same-stream ordering is guaranteed.

### 2.3 Exit code and reason

`code` and `reason` are independent fields, both always present on `Exited`. `reason` is the authoritative record of whether Core itself caused termination, and why; `code` remains best-effort OS-level detail and is frequently `-1` whenever `reason` is anything other than `NaturalExit`.

**`code`:**

| Condition                                                      | `code` value                                                         |
| -------------------------------------------------------------- | -------------------------------------------------------------------- |
| Process exited on its own                                      | OS exit code (0–255 on Unix; 0–4294967295 on Windows, stored as i32) |
| Core forcibly terminated the process (timeout or cancellation) | `-1`                                                                 |
| OS provides no exit code                                       | `-1`                                                                 |

**`reason`:**

| `reason` value    | Meaning                                                                                                                                                                                                                                                                                     |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `NaturalExit`     | The process terminated without Core having initiated termination. This covers an ordinary exit, a crash, or termination by anything outside Core's awareness — including the OS, another tool, or a person manually killing the process (e.g. via Task Manager). Core did not request this. |
| `TimedOut`        | Core killed the process because the configured timeout (§5) elapsed.                                                                                                                                                                                                                        |
| `CancelRequested` | Core killed the process because an explicit, on-demand cancellation request (§7) was received.                                                                                                                                                                                              |

A caller that needs to know *why* a process stopped must read `reason`, never infer it from `code` alone — `code` cannot distinguish these cases from each other, and was never able to.

---

## 3. Ordering Invariants

These invariants are enforced by the state machine and validated by integration tests. All are required to hold in every milestone.

1. **Started precedes Exited.** `Started` is always the first event; `Exited` is always the last.
2. **Exactly one Started per run.** A successful `Task::run()` emits `Started` exactly once.
3. **Exactly one Exited per run.** A successful `Task::run()` emits `Exited` exactly once.
4. **No events on spawn failure.** If the OS refuses to spawn the process, neither `Started` nor `Exited` is emitted and `run()` returns an error.
5. **Exited is terminal.** No event is emitted after `Exited`, including `StdoutChunk`/`StderrChunk`.
6. **Exited fires even on forced termination.** If the process is killed because of a timeout or an on-demand cancellation request, `Exited` — with `reason` set to `TimedOut` or `CancelRequested` respectively — is still emitted before `run()` returns its corresponding error.
7. **No descendant process survives `run()` returning.** After `run()` returns (for any reason — natural exit, timeout, or cancellation), all processes that were part of the spawned process tree are dead.
8. **Stream events are bounded by lifecycle events.** Every `StdoutChunk`/`StderrChunk` occurs strictly between `Started` and `Exited`. No stream event is ever emitted before `Started` or after `Exited`.

---

## 4. Execution Semantics

- **Single-shot only.** One `Task::run()` call = one process execution. No reuse.
- **Synchronous.** `run()` blocks until the process exits.
- **No PTY/terminal emulation.**
- **Optional timeout.** Set via `Task::with_timeout(Duration)`. When not set, `run()` blocks indefinitely (M1 behavior) unless cancelled (§7).
- **Output capture is opt-in and does not change execution semantics.** By default (`CaptureOutput: false`), behavior is identical to M1/M2: stdout/stderr are captured internally only to prevent pipe-buffer deadlock and are not surfaced to callers. When `CaptureOutput: true` is set, the same internal capture is additionally surfaced to the caller as `StdoutChunk`/`StderrChunk` events. No other behavior changes between the two modes — `run()`'s blocking semantics, timeout handling, and exit reporting are identical either way.

### 4.1 Environment and Working Directory

Three pre-spawn configuration methods control the child process's environment and starting directory. Like `with_timeout` and `with_capture_output`, these are builder methods on `Task`; once `run()` has been called, none of them may be changed.

- **`with_env(key, value)`** — sets an environment variable for the child. Repeatable; if called more than once with the same key, the last call before `run()` wins (later calls override earlier ones for that key). Variables set this way are always visible to the child, regardless of `with_clear_env`.
- **`with_clear_env(bool)`** — when `true`, the child does not inherit any variable from the parent process's environment; only variables set via `with_env` are present. Default `false` — the child inherits the full parent environment, unchanged from M1–M4 behavior.
- **`with_cwd(path)`** — sets the child's working directory. If the path does not exist or is not a directory, spawning fails the same way any other OS-level spawn rejection does: `run()` returns `Err(AerError::SpawnFailed)` and, per Ordering Invariant 4 (§3), neither `Started` nor `Exited` is emitted.

All three are configuration, not runtime state: they take effect once, at spawn, and have no observable effect once the process is running. Behavior is identical on Windows and Unix — both platforms apply configuration through the same standard-library process-builder primitives (`env` / `env_clear` / `current_dir`), with no OS-specific divergence.

---

## 5. Timeout Semantics (M2)

### Configuration

```rust
let task = Task::new("my-program", vec![])
    .with_timeout(Duration::from_secs(30));
```

`with_timeout` is a builder method. Tasks without it behave identically to M1 unless cancelled (§7).

### Kill sequence

When the timeout elapses and the process has not yet exited:

| Platform | Sequence                           |
| -------- | ---------------------------------- |
| Unix     | SIGTERM → wait 5 seconds → SIGKILL |
| Windows  | `TerminateProcess` immediately     |

The 5-second grace window on Unix gives the process a chance to handle SIGTERM and exit cleanly. SIGKILL is sent unconditionally after the grace period regardless of whether the process responded to SIGTERM. On Windows there is no reliable graceful kill for arbitrary console processes; `TerminateProcess` is used directly. This is the same kill sequence used for on-demand cancellation (§7) — there is only one kill mechanism in Core, triggered by two different causes.

### Return value on timeout

`run()` returns `Err(AerError::TimedOut)` after emitting `Exited { reason: TimedOut, .. }`. The `Started → Exited` invariant is preserved even when the process is killed.

### New error variants (M2)

| Variant                 | Meaning                                                                |
| ----------------------- | ---------------------------------------------------------------------- |
| `TimedOut`              | Process was killed because the timeout elapsed                         |
| `KillFailed(io::Error)` | The kill attempt itself failed (rare; process may have already exited) |

---

## 6. Process Tree Semantics (M3)

After `run()` returns — regardless of whether the process exited naturally, was killed by a timeout, or was cancelled on demand — **no descendant process of the spawned root shall remain alive.**

This guarantee is transparent to callers: no new API is required.

### Platform implementations

| Platform | Mechanism                                                                                                                                                                                                                                                                                         |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Windows  | Job Object created at spawn; `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` flag ensures the entire tree dies when the job handle is closed. On timeout or cancellation, the monitor calls `TerminateJobObject` (kills all processes in the job atomically, closing inherited pipes and unblocking `wait`). |
| Unix     | `setsid()` called in the child before exec, making the child the process group leader (pgid == pid). On kill, `killpg(pgid)` broadcasts the signal to the entire group.                                                                                                                           |

### Guarantee strength differs by platform

The two mechanisms are not equally strong, and the difference is part of the behavioral contract:

- **Windows — hard guarantee.** Job Object membership is kernel-enforced and inherited: a descendant cannot leave the job, and `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` / `TerminateJobObject` reach every member regardless of how it was created.
- **Unix — best-effort.** Process-group membership is voluntary: a descendant that itself calls `setsid()` (or `setpgid()`) leaves the group and is invisible to `killpg`. Narrow PID/pgid-reuse races also exist between exit and signal delivery. In practice the mechanism is reliable for well-behaved children (shells, compilers, CLIs), but a process that deliberately creates its own session escapes it.

If the hard guarantee ever becomes a requirement on Unix, the known upgrade paths are cgroups (Linux) or pidfd-based descendant tracking; neither is scoped into any current milestone. Callers and downstream specs (AER Flow) must not assume more than best-effort cleanup on Unix.

### Why TerminateJobObject is required on Windows (not TerminateProcess)

Grandchildren inherit the root's stdout/stderr pipe handles. `wait_with_output()` waits for EOF on those pipes. If only the root process is killed via `TerminateProcess`, grandchildren keep the pipes open and `wait_with_output()` hangs forever. `TerminateJobObject` kills the entire tree simultaneously, which closes all inherited pipe handles and immediately unblocks the wait.

---

## 7. Cancellation (M4)

A live execution may be stopped on demand, independent of any configured timeout. This is distinct from §5: a timeout is Core acting on its own, on a deadline it was configured with in advance; cancellation is always initiated by an external caller, at an arbitrary moment Core cannot predict.

**Guarantees (behavior, not mechanism):**

- Given a live execution (between `Started` and `Exited`), a caller has a way to request its cancellation. The specific shape of that mechanism — a handle returned at spawn time, an identifier passed to a separate call, or anything else — is intentionally left unspecified at this behavioral level. Only the capability is required.
- On a cancellation request, Core performs the same kill sequence already guaranteed for timeout (§5) and the same process tree cleanup already guaranteed at completion (§6). There is no second kill mechanism — cancellation and timeout converge on identical termination behavior, differing only in what triggered them.
- `Exited` is emitted with `reason: CancelRequested` (§2.3) before `run()` returns its corresponding error, exactly as Ordering Invariant 6 (§3) requires.
- A cancellation request for an execution that has already reached `Exited` is a no-op. It must not be treated as an error condition requiring special handling beyond reporting that the execution had already finished.
- Core does not know, and is never told, *why* a caller wants to cancel. That is entirely a concern of whatever sits above Core (see AER Flow spec, §9). Core's only contract is: "is this execution still running? If so, stop it the same way a timeout would."

This capability is scoped into M4, alongside the Observation Tier (§2.2/§4) and the FFI boundary, because the FFI boundary is the contract Flow is built against — adding cancellation after that boundary is defined would mean revisiting it a second time, the same reasoning that already justified bundling Observation Tier into M4 in the prior revision of this spec.

---

## 8. Milestone Definitions

| Milestone | Adds                                                                                                                                                                                                                            | Status                                                                                                                          |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| M1        | Core scaffold, state machine, STARTED/EXITED events, single-shot execution                                                                                                                                                      | ✓ Complete                                                                                                                      |
| M2        | Configurable timeout, kill escalation (SIGTERM → SIGKILL / TerminateProcess)                                                                                                                                                    | ✓ Complete                                                                                                                      |
| M3        | Process tree cleanup (Job Objects on Windows, setsid on Unix)                                                                                                                                                                   | ✓ Complete                                                                                                                      |
| M4        | Observation Tier (`CaptureOutput`, `StdoutChunk`/`StderrChunk` events), on-demand Cancellation (§7), **and** the FFI boundary (C-compatible ABI) exposing the full event set and the cancellation mechanism across the boundary | ✓ Complete                                                                                                                      |
| M5        | .NET binding (P/Invoke wrapper) over the M4 ABI, including stream event marshalling and a cancellation handle/API. First real consumer: AER Flow.                                                                               | Pending                                                                                                                         |
| M6        | Python binding (ctypes/cffi wrapper)                                                                                                                                                                                            | **Deferred.** No current consumer — AER Flow is C#/.NET. Re-scope only if a concrete Python-side worker or adapter requires it. |

### Rationale for the M4 rescope

Both the Observation Tier and Cancellation are folded into M4 for the same reason: the FFI boundary is the contract Flow gets built against. If the C ABI ships without either and they're added afterward, the ABI, the .NET binding, and any Flow code already written against it all need a second pass. Defining the full surface — lifecycle, observation, and cancellation — once, before M5 starts, avoids that rework.

This does **not** pull in PTY/interactive semantics (Tier 3 — stdin, raw mode, resize). Those remain explicitly out of scope for M4 and have no milestone assigned yet.

---

## 9. Behavioral Invariants (design targets for future milestones)

The following invariants are not yet enforced but the code must be structured to eventually enforce them:

- No event is emitted after the terminal state (already structurally guaranteed by M1 state machine, extended to stream events in §3.8).
- No duplicate terminal events per task (already structurally guaranteed by M1 state machine).
- Stream events, once exposed via FFI (M4), must preserve per-stream ordering across the language boundary — a .NET caller must observe `StdoutChunk` sequence numbers in the same order Core emitted them, with no reordering introduced by the binding layer.
- `reason` must always be one of the three defined values (§2.3); no caller-visible "unknown" or null state is permitted once M4's FFI boundary exposes it.
