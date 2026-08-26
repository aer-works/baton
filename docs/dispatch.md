# `aer dispatch` — the front door for driving a worker

`aer dispatch` runs **one worker role, one-shot**, against a vendor CLI that is already logged in on
this host, and drops the result into a room directory. It is the operator-facing front door to the
same engine `aer run` drives — a role is materialised into a single-step workflow, dispatched, and
its declared outputs are contract-checked exactly as a full run's are.

It is **not** the chat surface. A dispatch turn is non-interactive and runs to completion once; the
interactive session (chat) is a different path with a different prompt and a continuing turn.

```
aer dispatch <role> --spec <file> [--room-dir <dir>] [--adapter <vendor>] [--model <m>] [--effort <e>]
                    [--workspace <dir>] [--workflow-id <label>]
```

## Flags

| Flag | Meaning |
|------|---------|
| `--spec <file>` | The task prompt for the worker — the file whose contents become the spec. |
| `--room-dir <dir>` | Where the run is recorded (created if absent) — this is the room. Optional: omitted, each invocation gets a fresh unique one at `./.aer/dispatch-<role>-<8 hex>`, because a dispatch is one-shot and a stable derived directory would make the second `aer dispatch review` *resume* the first's terminal snapshot instead of running. |
| `--adapter <vendor>` | Run the role on a specific vendor (`claude` / `agy`) instead of its tier's default. The `--adapter` escape hatch; a role never names a vendor itself. |
| `--model <m>` | The model axis, independent of the role ([0017]/[0023]). Omitted keeps the tier's model — except on a vendor swap, where the tier's vendor-specific model is dropped for the new vendor's default (#1082). |
| `--effort <e>` | The effort axis, independent of the role. Omitted keeps the tier's effort; dropped on a vendor swap. |
| `--workspace <dir>` | The directory the worker runs in and may read. Defaults to the current directory. Bound explicitly because `agy -p` ignores the process working directory (#491). |
| `--workflow-id <label>` | A label forwarded to the run; defaults to the materialised template id. |

Vendor, model, and effort are **three independent axes** over a role's instructions ([0017]):
the role carries a default bundle (its tier), and each axis overrides on its own.

Model and effort are validated at the adapter boundary before dispatch (#1090): a dot-delimited claude
id (`claude-opus-4.8`, a typo for `claude-opus-4-8`) is refused with the correction rather than run;
and on agy, where the effort suffix in the model name and `--effort` are one control, a disagreeing
pair is refused up-front naming the real cause instead of failing after the run has started.

## Roles

Each role declares what it must produce; those declarations become the contract the engine enforces,
so a role that writes nothing fails loudly. The roles and their outputs are defined in
`src/Aer.Adapters/WorkerRoles.json` (authoritative); this table is a snapshot, pinned against that
catalog by `WorkerRoleCatalogTests`.

| Role | Tier | Writes | For |
|------|------|--------|-----|
| `advise` | standard | `advice.md` | Weighing an open design question before building — a second opinion. |
| `implement` | standard | `changes.md` | A bounded change whose approach is already decided; exercises the write path. |
| `review` | frontier | `report.md`, `verdict.json` | Adversarial review of a claim; the default for a PR touching `src/` or asserting something in `docs/`. |
| `patch` | frontier | `patch.diff` | Proposing code changes as an applyable diff without mutating the workspace. |
| `fact-check` | minimal | `findings.md` | Confirming an exhaustive, supplied list of facts against the repo — not for noticing what the list omits. |
| `janitor` | cheap | `janitor.md`, `branch.diff` | Running named mechanical checkers to green after an implementer, without changing behaviour. |
| `orchestrate` | orchestrator | `turn-actions.json` | A resident room turn that reads room state and emits turn actions. |

The prompt each worker receives is the spec followed by the role's own output instructions, so the
worker is told to produce exactly what the contract asserts. A dispatched worker is also told its turn
is one-shot (#1095): do the work to completion now and write the outputs before the turn ends — never
schedule background work or wait for a wake-up, because nothing resumes the turn.

## What a dispatch leaves in the room

The room directory accumulates the materialised workflow definition and its worker bindings, the
`flow.jsonl` event ledger (the append-only record of what the engine did), and an `artifacts/` tree
holding each step's declared outputs. The authoritative room layout is `spec/aer-room-spec-v1.0.md`;
`aer status <dir>` — the room directory is positional there, not a flag — reads the ledger and
reports where each step stands.

## The vendor premise

AER spawns the vendor's **own** first-party CLI, which authenticates itself against a **subscription**
— AER never handles a credential, and there are no API keys anywhere in this path (Architecture Rule
4). So a role runs only on a vendor that is already logged in on this host, and *which* vendor that is
is a fact of the host, not something a dispatch can provision. Dispatching a role to a vendor whose
CLI is not authenticated fails at that vendor's own login check, not inside AER.

If the vendor reports quota exhaustion, the engine paces a retry to the reported reset instant
(decision 0026) rather than burning attempts on a doomed retry. A foreground `aer dispatch` surfaces
that park — `Parked on vendor quota — the run resumes automatically at <time>` — and can be stopped
with Ctrl-C, which records a resumable state; re-running resumes it (#1094).
