# AER Core

[![CI](https://github.com/aer-works/aer-core/actions/workflows/ci.yml/badge.svg)](https://github.com/aer-works/aer-core/actions/workflows/ci.yml)

Cross-language process execution and lifecycle supervision runtime with deterministic cleanup semantics.

---

## The problem

Standard process APIs (`subprocess`, `.NET Process`, `std::process`) cannot reliably manage process lifecycles under failure conditions. When a spawned process creates children of its own, those children frequently outlive the parent after cancellation or timeout — leaving orphan processes that hold ports, lock files, and burn CPU with no owner.

## What AER does

AER is a Rust core library that guarantees consistent process lifecycle behavior across platforms:

- **Deterministic events** — every execution emits `Started` then `Exited`, in that order, always
- **No silent failures** — spawn errors are typed and explicit; no swallowed results
- **Platform-agnostic contract** — Windows and Linux behave identically from the caller's perspective
- **No orphans** — process tree cleanup ensures nothing survives `run()` returning: kernel-enforced on Windows (Job Objects), best-effort on Unix (setsid/killpg — a descendant that starts its own session can escape; see spec §6)

---

## Quickstart

Requires [pixi](https://pixi.sh). The Rust toolchain is managed automatically — nothing else to
install for the Rust examples and tests below.

```sh
# M1 — basic lifecycle: spawn a process and observe events
pixi run example

# M2 — timeout: see a slow process get killed, then a fast process complete normally
pixi run example-timeout

# M3 — process tree: a process forks a background child; AER cleans up the whole tree
pixi run example-tree

# M4 — IO capture and explicit cancellation examples
pixi run example-capture
pixi run example-cancel

# environment variables and working directory
pixi run example-env

# Run all tests
pixi run test

# Lint and format check
pixi run lint
pixi run fmt-check
```

The `dotnet-*` tasks (see [Available tasks](#available-tasks)) additionally require a system
.NET 10 SDK — pixi does not manage it. See [Prerequisites](CLAUDE.md#prerequisites) for install
commands.

### Example output

```
Spawning task...

  → Started  (pid 12345)
  → Exited   (code 0)

Done.
```

---

## .NET usage

The `Aer.Core` package (`bindings/dotnet/Aer.Core`) wraps the C FFI in an idiomatic managed API:
fluent `With*` configuration, `Run`/`RunAsync`, and an `EventRaised` event instead of raw callbacks.

```csharp
using Aer.Core;

using AerTask task = new AerTask("ping", "-n", "4", "127.0.0.1")
    .WithTimeout(TimeSpan.FromSeconds(10))
    .WithCaptureOutput();

task.EventRaised += (_, e) =>
{
    if (e.Kind is AerTaskEventKind.StdoutChunk or AerTaskEventKind.StderrChunk)
    {
        Console.Write(Encoding.UTF8.GetString(e.Data!));
    }
};

task.Run(); // blocks until the process exits
```

`RunAsync` runs the (inherently blocking) native call on a thread-pool thread and wires a
`CancellationToken` to the native cancel handle:

```csharp
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
using AerTask task = new AerTask("sh", "-c", "sleep 30");

await task.RunAsync(cts.Token); // throws AerCancelException once the token fires
```

Failures are typed, not swallowed: `AerTimeoutException` and `AerCancelException` (both
`AerException` subtypes carrying an `AerErrorCode`) signal timeout and cancellation respectively;
every other native failure surfaces as a plain `AerException`.

For a full runnable walkthrough (live output capture, `RunAsync` cancellation, `WithEnv`/`WithCwd`),
see `bindings/dotnet/Aer.Core.Example` — run it with `pixi run dotnet-example`.

---

## Architecture

```
┌─────────────────────────────────┐
│           aer-core              │  ← this repo, Milestones 1–5
│                                 │
│  Task::run()                    │
│    │                            │
│    ├── StateMachine             │  Created → Running → Exited
│    ├── Event emission           │  Started { pid }, Exited { code }
│    └── OS process layer         │  windows.rs / unix.rs
└─────────────────────────────────┘
         ↑ FFI boundary (M4)
┌────────┴────────┐  ┌────────────┐
│bindings/dotnet/ │  │ aer-python │  ← thin translation layers, M5/M6
│   (P/Invoke)    │  │ (ctypes)   │
└─────────────────┘  └────────────┘
```

Dependencies flow inward only. No process logic lives in the bindings.

---

## Roadmap

| Milestone | Status | Adds |
|---|---|---|
| **M1: Core Scaffold** | ✅ Complete | State machine, STARTED/EXITED events, single-shot execution |
| **M2: Timeout & Kill** | ✅ Complete | Configurable timeout, graceful termination, kill escalation |
| **M3: Process Tree** | ✅ Complete | Job Objects (Windows), setsid (Unix) — no orphans (hard guarantee on Windows, best-effort on Unix; spec §6) |
| **M4: FFI Boundary** | ✅ Complete | Stable C-compatible ABI for language bindings |
| **M5: .NET Binding** | ✅ Complete | P/Invoke wrapper, managed `AerTask` (fluent config, `Run`/`RunAsync`, `EventRaised`) |
| **M6: Python Binding** | Deferred | ctypes/cffi wrapper, asyncio context manager |

Full behavioral specification: [`spec/aer-core-behavioral-spec-v1.1.md`](spec/aer-core-behavioral-spec-v1.1.md)

Project board: [AER Roadmap](https://github.com/orgs/aer-works/projects/1)

---

## Available tasks

| Command | Description |
|---|---|
| `pixi run build` | Compile the workspace |
| `pixi run test` | Run all tests |
| `pixi run lint` | Clippy with `-D warnings` |
| `pixi run fmt` | Auto-fix formatting |
| `pixi run fmt-check` | Check formatting (used in CI) |
| `pixi run example` | Run the M1 hello example |
| `pixi run example-timeout` | Run the M2 timeout example |
| `pixi run example-tree` | Run the M3 process tree example |
| `pixi run example-capture` | Run the M4 stdout/stderr capture example |
| `pixi run example-cancel` | Run the M4 manual cancellation example |
| `pixi run example-env` | Run the environment/working-directory example |
| `pixi run dotnet-build` | Build the .NET binding and its test/example projects |
| `pixi run dotnet-test` | Run the .NET binding's test suite (builds the Rust core first) |
| `pixi run dotnet-lint` | `dotnet build -warnaserror` for the .NET solution |
| `pixi run dotnet-fmt` | Auto-fix .NET formatting |
| `pixi run dotnet-fmt-check` | Check .NET formatting (used in CI) |
| `pixi run dotnet-example` | Run the .NET usage tutorial (builds the Rust core first) |

---

## License

[Unlicense](LICENSE) — public domain.
