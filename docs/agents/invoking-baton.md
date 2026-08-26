# Invoking Baton against another repo

For the **cold invoking agent**: you have been told to run a Baton lane over some repository, you
have no prior session context, and your job is to get one worker to produce one file. This page is
the working invocation and the edges around it, as they actually are today.

It is **not** for developing Baton — that is [`CLAUDE.md`](../../CLAUDE.md) — and it is not the
reference for `aer dispatch`, which is [`docs/dispatch.md`](../dispatch.md). Where those own a fact,
this links rather than restates.

Everything below is the state of the tree on the day it was written. Three known-sharp edges are
tracked as open issues and are called out where you will hit them: dispatch ergonomics
([#1354](https://github.com/aer-works/baton/issues/1354)), the machine completion contract
([#1356](https://github.com/aer-works/baton/issues/1356)), and errors that diagnose without
prescribing ([#1357](https://github.com/aer-works/baton/issues/1357)). Nothing here describes
behaviour those issues would add.

---

## 1. The one invocation that works today

```
aer run <workflow-file> --bindings <bindings-file> --room-dir <fresh-dir> --echo-worker
```

Two files you author, one directory you name. `--echo-worker` streams the worker's stdout so you can
see it is alive; drop it and you see nothing until the run settles.

`aer dispatch` is the intended front door and needs no JSON from you — but it does not complete for
every role/adapter pair yet. Read §6 before choosing it; `aer run` is the path that works for all of
them.

**The first argument is a file path.** `aer templates` lists template *ids*, and `aer run` does not
resolve them — it opens the argument as a file and fails with `Template file '<name>' does not
exist.` That the two are different namespaces is not said anywhere the error can reach you
([#1357](https://github.com/aer-works/baton/issues/1357)).

---

## 2. A complete minimal pair — one review step, agy, no network, explicit output path

Both files below are derived from this repo's own live-vendor smoke fixtures, which are the only
worker-binding JSON in the tree that a real vendor run has ever accepted:

- shape of the single-step workflow, and of a `PermissionGrant` entry —
  [`tests/Aer.Cli.SmokeTests/Fixtures/readonly-reviewer-workflow.json`](../../tests/Aer.Cli.SmokeTests/Fixtures/readonly-reviewer-workflow.json)
  and [`readonly-reviewer-bindings.json`](../../tests/Aer.Cli.SmokeTests/Fixtures/readonly-reviewer-bindings.json)
- an `agy` binding entry — [`draft-review-paused-bindings.json`](../../tests/Aer.Cli.SmokeTests/Fixtures/draft-review-paused-bindings.json)
- the authoritative field list, defaults, and what each field means —
  [`src/Aer.Adapters/WorkerBindingConfigEntry.cs`](../../src/Aer.Adapters/WorkerBindingConfigEntry.cs)

### `review-workflow.json`

```json
{
  "WorkflowTemplateId": "repo-review",
  "WorkflowTemplateVersion": 1,
  "Steps": [
    {
      "StepId": "review",
      "Worker": "reviewer",
      "Inputs": [],
      "Outputs": ["report.md"],
      "DependsOn": [],
      "RetryPolicy": { "MaxAttempts": 1 }
    }
  ]
}
```

### `review-bindings.json`

```json
{
  "reviewer": {
    "Adapter": "agy",
    "Model": "gemini-3.6-flash-low",
    "Timeout": "00:25:00",
    "WorkingDirectory": "C:\\absolute\\path\\to\\the\\repo\\under\\review",
    "Contract": {
      "WorkerName": "reviewer",
      "RequiredInputs": [],
      "ProducedOutputs": [{ "Name": "report.md" }],
      "OptionalMetadata": []
    },
    "PromptTemplate": "Review the repository you have been given for <the specific claim>. Cite file:line evidence for every finding.",
    "PermissionGrant": {
      "ReadFiles": true,
      "WriteFiles": true,
      "RunShellCommands": false,
      "ShellCommandPatterns": [],
      "NetworkAccess": false
    }
  }
}
```

Then:

```
aer run review-workflow.json --bindings review-bindings.json --room-dir /tmp/review-001 --echo-worker
```

Four couplings hold this together, and getting any of them wrong is a run you pay for and throw away:

| This | must equal | that |
|---|---|---|
| the top-level key in bindings (`"reviewer"`) | | the step's `Worker` |
| `Contract.WorkerName` | | the same worker name |
| every `Contract.ProducedOutputs[].Name` | | the step's `Outputs` |
| every `Contract.RequiredInputs` entry | | an upstream step's declared output |

`WorkingDirectory` must be an **absolute** path (or a bare name registered in this machine's profile
mapping — see the field's own docs in `WorkerBindingConfigEntry.cs`). On `agy` this is what the
worker can actually see: `agy -p` ignores the process working directory, so the adapter passes the
directory explicitly, and a wrong value produces a confident review of the wrong tree rather than an
error. On Windows, double every backslash — it is a JSON string, and a lone `C:\Users\…` is rejected
as an invalid escape before anything else is checked.

### Why this is the read-lane profile

`NetworkAccess: false` is not a hardening choice you could relax — on `agy` it is the only
grant shape that resolves. That vendor's only auto-approve flag is all-or-nothing
(`--dangerously-skip-permissions`), so the adapter refuses a grant asking for network *or* shell
without the other rather than silently over-granting the one you did not request
(`AgyWorkerAdapter.TryTranslatePermissionGrant`). A reviewer does not need the network: its
deliverable is a file at a path AER hands it, not something it fetches.

`WriteFiles: true` in a *read* lane looks wrong and is not. On `agy`, a withheld write does not
reach the outbox — the worker simply cannot produce its report, and you get a paid-for run that
fails the contract. The grant above resolves to `--mode accept-edits`, and the adapter then seeds a
least-privilege `write_file($AER_OUTPUT_DIR/report.md)` allow rule — one per declared output — into
the AER-owned home it runs that worker under. Writes are still bounded by AER's own `PreToolUse`
hook; the grant is not the boundary.

---

## 3. Where the output lands, and how you find it

At settle, `aer run` prints one line per produced output of each succeeded step:

```
Workflow status: Terminal
  review: Succeeded
  report.md -> <room-dir>\artifacts\execution_<id>\report.md
```

**Read that line rather than reconstructing the path.** The `execution_<id>` segment is allocated per
execution, so a retry writes to a different directory and the previous one is still on disk.

The room directory also holds `snapshot.json` (the workflow this room is bound to), `flow.jsonl` (the
append-only event ledger), and `flow.lock`. The authoritative room layout is
[`spec/aer-room-spec-v1.0.md`](../../spec/aer-room-spec-v1.0.md).

---

## 4. Adapter notes

### agy

| Grant | Resolves to |
|---|---|
| read + write, no shell, no network | `--mode accept-edits` |
| read only | `--mode plan` |
| neither | `--mode default` |
| shell **and** network together | `--dangerously-skip-permissions` |
| shell without network, or network without shell | **refused before dispatch**, with the reason |

Model and effort are separate fields (`Model`, `Effort`) and are separate axes from the adapter.
Two agy-specific traps:

- On agy, effort is also encoded in the model name's suffix. `Model: "gemini-3.6-flash-low"` plus
  `Effort: "high"` is refused up front — pass one, or make them agree.
- `Effort` accepts either a raw vendor value (`low`/`medium`/`high`) or a canonical effort word
  (`quick`/`standard`/`careful`/`exhaustive`); anything else is refused before the run starts rather
  than forwarded blind.

Model names are pinned per tier in
[`src/Aer.Adapters/WorkerTiers.json`](../../src/Aer.Adapters/WorkerTiers.json), and `agy models` is
what the repo's own audit checks those pins against. `gemini-3.6-flash-low` above is the value
`draft-review-paused-bindings.json` uses; take a current one from those two sources rather than from
this sentence.

### claude

`claude` is the other registered adapter and takes the same binding shape (see
`readonly-reviewer-bindings.json`, which is a claude entry). One difference matters when choosing:
on claude a **withheld** write still reaches the outbox, so `WriteFiles: false` there is a genuine
read-only lane that still produces its report. That asymmetry is why §6's table splits by adapter.

Both adapters spawn the vendor's own already-authenticated CLI. Baton never handles a credential, so
a lane only runs on a vendor that is already logged in on this host — see the README's *Vendor
authentication* section.

---

## 5. Sharp edges

**A room directory is bound to one workflow, and re-running resumes it.** `aer run` against a
`--room-dir` that already holds a snapshot runs *that* workflow rather than the file you named, and
refuses outright if the two are different templates. Against an already-terminal room it reports the
prior run's status, writes nothing, and exits non-zero — which looks exactly like a fresh failure
except for the `Resumed the snapshot already bound in this room directory` line above it. **Use a
fresh directory for every new piece of work.** Omit `--room-dir` entirely and you get one derived
from the workflow file's name, in `./.aer/` — stable, therefore resuming, which is usually not what
an orchestrator wants.

**Never pass a relative `--room-dir`.** It is resolved to absolute at the CLI boundary now, but the
failure it caused is worth knowing: the worker is a different process with a different working
directory, so it resolved the relative output path against its own cwd and wrote the report where AER
never looked — reported as `Contract not satisfied`, after the run was paid for in full.

**`aer status` takes the room directory positionally**: `aer status <room-dir> [--follow]`. There is
no `--room-dir` flag on it, and passing one is an `Unknown option` error.

**You cannot yet tell a dead room from a slow one, and exit codes are not a contract.** `aer run`
returns 0 only when the workflow is Terminal with every step Succeeded, 1 otherwise — but a room
whose provisioning failed before a ledger existed sits at "Running / no ledger yet" forever, and at
least one validation-failure path has been observed exiting 0. There is no `--wait`, no
`status --json`, and no completion sentinel to watch. All of that is
[#1356](https://github.com/aer-works/baton/issues/1356); until it lands, the only completion signal
is the `aer run` process itself exiting, and the only progress signal is prose from
`aer status --follow` or the `--echo-worker` stream. **Do not background an `aer run` and poll
`aer status` for a state word — wait on the process.**

**Budget the wall clock in minutes, not seconds.** A repo-scale agy review ran roughly 3–5 minutes in
the 2026-08-26 session that prompted [#1358](https://github.com/aer-works/baton/issues/1358) — one
observation, an order of magnitude rather than a measurement. What is exact is the ceiling: the
binding's `Timeout` field, which the example above sets to 25 minutes to match what the `review` role
declares in [`src/Aer.Adapters/WorkerRoles.json`](../../src/Aer.Adapters/WorkerRoles.json). A timeout
shorter than the work kills a run you have already paid for.

**Validation errors name the invariant, not the fix.** They are precise about what is wrong and say
nothing about which invocation would be right, so a cold agent learns by rejection —
[#1357](https://github.com/aer-works/baton/issues/1357). Two you are most likely to meet are the
template-file error in §1 and the worktree error in §6.

---

## 6. Per-role dispatch, and which roles it completes for today

[`docs/dispatch.md`](../dispatch.md) is the reference for `aer dispatch` — its flags, the seven roles,
what each writes, and the three independent vendor/model/effort axes. Read it there. What follows is
only the part a cold agent needs before choosing that path: it does not complete for every
role/adapter pair.

```
aer dispatch <role> --spec <spec-file> --room-dir <fresh-dir> [--adapter agy|claude] [--workspace <dir>]
```

A role whose grant withholds writes, dispatched to an adapter whose withheld writes do **not** reach
the outbox, is bound as `AuditedNotEnforced` — which requires a provisioned git worktree that
`dispatch` does not provision and no flag can express. Today that is exactly the write-withholding
roles on `agy`:

| Role | Writes | `--adapter claude` | `--adapter agy` |
|---|---|---|---|
| `advise` | `advice.md` | works | works |
| `implement` | `changes.md` | works | works |
| `janitor` | `janitor.md`, `branch.diff` | works | works |
| `review` | `report.md`, `verdict.json` | works | blocked — [#1354](https://github.com/aer-works/baton/issues/1354) |
| `patch` | `patch.diff` | works | blocked — [#1354](https://github.com/aer-works/baton/issues/1354) |
| `fact-check` | `findings.md` | works | blocked — [#1354](https://github.com/aer-works/baton/issues/1354) |
| `orchestrate` | `turn-actions.json` | works | blocked — [#1354](https://github.com/aer-works/baton/issues/1354) |

The blocked cells fail with:

> Worker-binding config entry for '<role>' specifies GrantAuditMode.AuditedNotEnforced without a
> provisioned worktree.

**Workaround: use `aer run` with a hand-authored pair.** §2's example is that same `review` lane on
`agy`. It clears the blocker because it asks for the write in the first place instead of having one
flipped on for it, so `GrantAuditMode` stays at its `Enforced` default — and `Enforced` runs no
post-run worktree audit, which is what the isolation requirement exists for. Hand-editing generated
bindings to claim `IsWorktree: true` is not a second workaround: that field is a stamp the
provisioner leaves, and a hand-authored `true` claims an isolation that does not exist.

Cells marked *works* still require that vendor's CLI to be logged in on this host, and `--adapter`
without `--model`/`--effort` drops the role tier's vendor-specific model.

**Per-role copy-paste examples land here as [#1354](https://github.com/aer-works/baton/issues/1354)
closes those cells.** Until then, a role's declared outputs — the `Outputs`/`ProducedOutputs` pair you
need for the `aer run` shape — are in `WorkerRoles.json`, and the table above lists them.
