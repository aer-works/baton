# AER — Claude Code Instructions

AER is a cross-language process supervision engine. The Rust crate `aer-core` is the product; all invariants — deterministic spawn, STARTED/EXITED events, state machine transitions — are enforced here. The .NET binding (`dotnet/`) is a thin P/Invoke wrapper over the C FFI and lives in this repo. Python binding is deferred.

---

## Repo structure

```
aer-core/
├── src/
│   ├── lib.rs        public API surface, AerError definition
│   ├── event.rs      Event enum (Started, Exited, StdoutChunk, StderrChunk)
│   ├── machine.rs    StateMachine (Created → Running → Exited)
│   ├── task.rs       Task::run() / run_with_cancel() — drives the machine and emits events
│   ├── ffi.rs        C-compatible ABI (M4)
│   └── os/           platform abstraction (windows.rs / unix.rs)
├── include/
│   └── aer.h         C header (stable ABI contract)
├── tests/
│   └── integration_test.rs
├── bindings/
│   └── dotnet/       .NET binding — P/Invoke wrapper over core/include/aer.h (M5)
├── spec/             behavioral specs (source of truth, not code)
│   ├── AER Overview.md
│   ├── aer-core-behavioral-spec-v1.1.md   ← current
│   └── aer-core-behavioral-spec-v1.0.md   ← archived, superseded by v1.1
├── .github/workflows/
│   ├── ci.yml             lint + fmt + test on win + linux
│   └── release-please.yml versioning and changelog
└── pixi.toml         task runner and toolchain manager
```

---

## Prerequisites

- **Rust toolchain** — managed by pixi; no separate `rustup` install needed.
- **.NET 10 SDK** — required for `dotnet-*` tasks; install separately (not managed by pixi).
  - Windows: `winget install Microsoft.DotNet.SDK.10`
  - macOS: `brew install dotnet-sdk` or the official installer
  - Linux: follow [Microsoft's install guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux)

## Running tasks

Always use `pixi run <task>`. Never invoke `cargo` or `dotnet` directly in CI.

**Rust**

| Task | Command |
|---|---|
| `build` | `cargo build` |
| `test` | `cargo test` |
| `lint` | `cargo clippy --all-targets -- -D warnings` |
| `fmt` | `cargo fmt --all` (fix) |
| `fmt-check` | `cargo fmt --all -- --check` (CI) |
| `example` | `cargo run --example hello` (M1) |
| `example-timeout` | `cargo run --example timeout` (M2) |
| `example-tree` | `cargo run --example tree` (M3) |
| `example-capture` | `cargo run --example capture` (M4) |
| `example-cancel` | `cargo run --example cancel` (M4) |
| `example-env` | `cargo run --example env` (env/cwd) |

**\.NET binding** (requires .NET 10 SDK on PATH)

| Task | Command |
|---|---|
| `dotnet-build` | `dotnet build` in `bindings/dotnet/` |
| `dotnet-test` | `dotnet test` (also runs `build` first) |
| `dotnet-lint` | `dotnet build -warnaserror` |
| `dotnet-fmt` | `dotnet format` (fix) |
| `dotnet-fmt-check` | `dotnet format --verify-no-changes` (CI) |
| `dotnet-example` | `dotnet run --project Aer.Core.Example` (also runs `build` first) |

---

## Module responsibilities

- **lib.rs** — re-exports public types, defines `AerError`. Nothing else.
- **event.rs** — pure data: the `Event` enum. No logic.
- **machine.rs** — `StateMachine` enforces legal transitions. Not public; callers see only events.
- **task.rs** — `Task::run()` / `run_with_cancel()` drive the full lifecycle: spawn → Started → wait → Exited. Hosts the timeout monitor thread (M2). Clones `KillHandle` for the monitor thread (M3). `CancelHandle` (M4c) wires an external caller to the live kill handle.
- **ffi.rs** — C-compatible ABI (M4). `AerTask`, `AerCancelHandle`, `AerEvent`, `AerErrorCode`. All exported symbols are `#[no_mangle] pub unsafe extern "C"`. Panic safety via `catch_unwind` at every boundary.
- **os/mod.rs** — `OsProcess` trait + `OsHandle` + `KillHandle`. `cfg` gates select the platform impl.
- **os/windows.rs / unix.rs** — OS-specific spawn, wait, and kill escalation. Windows: Job Objects for process tree containment (M3). Unix: setsid + killpg for process group management (M3). Must not leak platform behavior into callers.

---

## Milestone constraints

Do not add any of these until the milestone that introduces them:

| Feature | Milestone |
|---|---|
| FFI boundary | M4 ✓ |
| .NET binding (`dotnet/`) | M5 |
| Async execution | post-M5 |
| Python binding | deferred |

---

## Error handling rules

- No `unwrap()` or `expect()` in library code (`src/`). Tests may use them.
- No swallowed errors — every `Result` must be propagated or explicitly mapped to `AerError`.
- No `Box<dyn Error>` — all errors are typed `AerError` variants.

---

## Testing conventions

- All tests live in `tests/integration_test.rs` (integration) or inline in `src/machine.rs` (state machine unit tests only).
- Integration tests must be platform-agnostic: use the `#[cfg(target_os = "windows")]` helper functions in the test file to select commands, never hardcode shell paths.
- Exit codes in tests: use 0–127 only (cross-platform safe range).

---

## Git conventions

- Conventional commits: `<type>(<scope>): Capitalized description`
- Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `ci`, `test`, `chore`
- No direct commits to `main`. All changes via PR.
- Close issues in the PR body (`Closes #n`), not in commit messages.
- Each issue is scoped to ship as a standalone PR (one-to-one). If two issues can't be reviewed independently, the issue boundary was drawn incorrectly — fix it in the backlog, not at PR time.
