# Invoking Baton against another repo

For the **cold invoking agent**: you have been told to run a Baton lane over some repository, you
have no prior session context, and your job is to get one worker to produce one file. This page is
the working invocation and the edges around it, as they actually are today.

It is **not** for developing Baton — that is [`CLAUDE.md`](../../CLAUDE.md) — and it is not the
reference for `aer dispatch`, which is [`docs/dispatch.md`](../dispatch.md). Where those own a fact,
this links rather than restates.

Everything below is the state of the tree on the day it was written. Dispatch ergonomics
([#1354](https://github.com/aer-works/baton/issues/1354)), the machine completion contract
([#1356](https://github.com/aer-works/baton/issues/1356)), and validation errors carrying a
corrected-invocation `Try:` line ([#1357](https://github.com/aer-works/baton/issues/1357)) have all
landed — §3, §5, and §6 below describe what they actually do rather than what they were tracked to
add.

---

## 1. The one invocation that works today

```
aer run <workflow-file> --bindings <bindings-file> --room-dir <fresh-dir> --echo-worker
```

Two files you author, one directory you name. `--echo-worker` streams the worker's stdout so you can
see it is alive; drop it and you see nothing until the run settles.

`aer dispatch` is the intended front door and needs no JSON from you. Read §6 before choosing it —
an audited role/adapter pair now auto-provisions its own worktree rather than refusing, which is a
real consequence (uncommitted changes become invisible to the worker), not a formality. `aer run`
remains the path that works uniformly, including for a composed template's audited phase, which §6
still refuses at bind time.

**The first argument is a file path.** `aer templates` lists template *ids*, and `aer run` does not
resolve them — it opens the argument as a file and fails with `Template file '<name>' does not
exist.` That the two are different namespaces now shows up in the error itself, as a `Try:` line:
`'aer run' takes a workflow FILE; built-in templates are used via 'aer dispatch <role>'`
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

That prose is for a person watching. For a machine caller (#1356), the same information is
available two other ways, and both give you the same set of paths without parsing a sentence:

- **`aer status <room-dir> --json`** — one JSON object to stdout, nothing else:
  <!-- record-once-ok: #1359 src/Aer.Cli/WorkflowStatusView.cs -->
  `{state, steps:[{id, state, execution, linkedFrom}], outputs:[...], error, try}`. `outputs` is the
  flat list of absolute paths every succeeded step's declared outputs resolved to — the same paths
  the human line above prints, derived from the same read. Works on a running room too
  (`state: "Running"`), not only a settled one. `try` (#1357) is the same corrected-invocation text a
  validation refusal's `Try:` stderr line carries, kept as its own field rather than folded into
  `error` — `null` when the refusal had none. Only ever populated on a pre-ledger `Failed` room (§5's
  exit-code-2 case); a settled or running room's ledger projection has no exception to carry one.
  `linkedFrom` (#1359) names the predecessor execution when the step's current one was started by
  `aer resume`; anything that was dispatched or retried normally shows `null` there.
- **`<room-dir>/terminal.json`** — written once, the moment the workflow FIRST reaches a terminal
  state, in the identical shape `status --json` prints. Written *last*, after every output it could
  reference already exists on disk, specifically so you can watch this one file with a file monitor
  instead of polling `aer status` or babysitting the `aer run` process — the async
  task-notification parity the issue asked for. Its absence means "not terminal yet", not "never
  started"; see §5 for the one case where it is the *only* record a room has. **`aer resume` rewrites
  it** on that step's own settle, but does NOT invalidate it the moment the resume starts — a watcher
  polling this file sees the FIRST run's terminal state for the resume's whole duration and cannot
  tell the room is busy again from this file alone; check `aer status <room-dir>` (no `--json`) for
  that, or the exit code of the `aer resume` process itself.

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

**Exit codes are a contract, and a dead room from provisioning is no longer indistinguishable from a
slow one (#1356, #1374).** `aer run`/`aer dispatch`/`aer resume` (#1359) return one of six codes.
**`aer resume` continues ONE worker** — it hands an already-dispatched step's vendor session your
follow-up message, reusing the workspace and grant that step already had, and the ledger gains a
fresh execution pointing back at its predecessor (`aer resume <room-dir> --worker <role>
--message <text> --bindings <file>`; see `aer resume --help`). Its exit code is still the WHOLE
ROOM's outcome, same table, not "did the resumed step itself succeed" — if some other step had
already Failed, even a perfectly good resume exits 1; read the resumed step's own status via
`aer status --json`'s `steps[].state`/`linkedFrom` for that:

| Code | Meaning |
|---|---|
| 0 | `Succeeded` — every step Succeeded |
| 1 | `Failed` — a step ran and failed for an ordinary reason (also the bucket a still-Running or still-Paused process falls into if it returns short of Terminal, e.g. no `--wait`) |
| 2 | `ValidationRefused` — bindings/workflow validation, or an unresolvable worker binding (bad adapter name, an incoherent grant, an unprovisioned worktree an `AuditedNotEnforced` grant needed), was refused **before anything was dispatched, against a room with no ledger yet** |
| 3 | `Timeout` — the step(s) that failed did so because a dispatch hit its binding's `Timeout`, not because the worker ran and failed on its own |
| 4 | `Cancelled` — the workflow settled via cancellation, not failure |
| 5 | `RoomHeld` — another Flow instance already holds this room (a live pump, or a background component's brief lock). Not a terminal outcome and not written to `terminal.json`: the room may be perfectly healthy, so nothing here overwrites its real state. Retry later, or check `aer status`/the sentinel for what the room actually is |

A room whose provisioning fails before `flow.jsonl` ever exists — the GrantAuditMode case above is
one way to reach this, a malformed bindings/workflow file is another — no longer sits at "Running /
no ledger yet" forever: it is left in a queryable `Failed` state (`aer status`, or the
`terminal.json` sentinel §3 describes, which such a room gets even though it has no ledger at all)
that names why, and the process that hit it exits 2. **That queryable-`Failed` treatment is reserved
for a genuinely pre-ledger room** (#1374): a later invocation that fails against a room whose
`flow.jsonl` already exists — a re-run with a typo'd `--bindings` against an already-completed room,
say — still exits 2 for that invocation, but leaves the room's own ledger/sentinel untouched rather
than overwriting a real terminal record with a fabricated one.

**`--wait` on `aer run`** only matters at a pause point — its full contract is
[`RunOptions.Wait`](../../src/Aer.Cli/RunOptions.cs)'s own doc comment; in short, omitting it hands
control back to you the moment a workflow pauses (as today, leaving `aer decide` to carry it
forward later), while passing it keeps that same invocation attached, watching the room until the
pause is resolved from elsewhere and the workflow settles, or you interrupt it. One thing it does
not cover: an `aer run` that already crashed in an earlier invocation is not something a later
`--wait` call reattaches to — **that gap (crash-orphaning) is still open.** For a room you did not
start yourself, the only completion signals stay the process's own exit or the `terminal.json`
sentinel §3 describes. **Do not background an `aer run` and poll `aer status` for a state word —
wait on the process, or watch `terminal.json`.**

**Budget the wall clock in minutes, not seconds.** A repo-scale agy review ran roughly 3–5 minutes in
the 2026-08-26 session that prompted [#1358](https://github.com/aer-works/baton/issues/1358) — one
observation, an order of magnitude rather than a measurement. What is exact is the ceiling: the
binding's `Timeout` field, which the example above sets to 25 minutes to match what the `review` role
declares in [`src/Aer.Adapters/WorkerRoles.json`](../../src/Aer.Adapters/WorkerRoles.json). A timeout
shorter than the work kills a run you have already paid for.

**Most validation/refusal errors now carry a `Try:` line naming a corrected invocation**, printed
directly under the error and echoed on the pre-ledger `terminal.json`/`status --json` sentinel's
`try` field (§3) — [#1357](https://github.com/aer-works/baton/issues/1357). Two you are most likely
to meet are the template-file error in §1 and the worktree error in §6. Not every refusal gets one:
an unknown option or an extra positional argument has no way to infer what you meant, so those are
left without a suggestion rather than a guessed one.

---

## 6. Per-role dispatch, and which roles it completes for today

[`docs/dispatch.md`](../dispatch.md) is the reference for `aer dispatch` — its flags, the seven roles,
what each writes, and the three independent vendor/model/effort axes. Read it there. What follows is
only the part a cold agent needs before choosing that path: it does not complete for every
role/adapter pair.

```
aer dispatch <role> --spec <spec-file> --room-dir <fresh-dir> [--adapter agy|claude] [--workspace <dir>]
                    [--output <path>]
```

A role whose grant withholds writes, dispatched to an adapter whose withheld writes do **not** reach
the outbox, is bound as `AuditedNotEnforced` — which needs a provisioned git worktree, or
`WorkerBindingResolver` refuses it at bind time. `dispatch` now provisions that worktree itself,
automatically, for every such role/adapter pair (#1354/#1380) — no flag needed, and none exists to
suppress it. Today that is exactly the write-withholding roles on `agy`:

| Role | Writes | `--adapter claude` | `--adapter agy` |
|---|---|---|---|
| `advise` | `advice.md` | works | works |
| `implement` | `changes.md` | works | works |
| `janitor` | `janitor.md`, `branch.diff` | works | works |
| `review` | `report.md`, `verdict.json` | works | works — auto-provisioned worktree |
| `patch` | `patch.diff` | works | works — auto-provisioned worktree |
| `fact-check` | `findings.md` | works | works — auto-provisioned worktree |
| `orchestrate` | `turn-actions.json` | works | works — auto-provisioned worktree |

**`advise`'s "works" on `agy` is not the same shape as `review`'s.** Unlike the other read-shaped
roles, `advise` keeps an explicit `write_files: true` grant (pinned reason in `WorkerRoles.json`'s
`advise` entry), so it never enters the `AuditedNotEnforced`/auto-provisioned-worktree path this
table otherwise describes: on `agy` its write stays `Enforced` against your live `--workspace`
directory, not a disposable worktree. See `docs/dispatch.md`'s printed-grant-line section for what a
dispatch actually discloses before it runs.

**"Auto-provisioned worktree" is a real consequence, not a formality.** The worker is handed a fresh
worktree of `--workspace` (or the cwd) at `HEAD` — never the caller's own directory, whether or not
that directory already happens to be a worktree itself, because the post-run audit's whole premise is
that the tree started clean *because this run made it*. Concretely: **uncommitted and staged changes
in the workspace are invisible to the worker.** Dispatch discloses this before the run starts —

```
Workspace: worktree of <repo> at HEAD (<short-sha>) — uncommitted changes are not visible to the worker
```

— and the tree's eventual teardown follows the same kept-vs-removed rule as any other provisioned
worktree. See `docs/dispatch.md`'s `--workspace` row and its "auto-provisioned worktree" section for
what that rule is and where the disclosure comes from.

This still only reaches the composed **role** dispatch above — a template phase's audited grant (`aer
dispatch <template>`) is unchanged and refuses at bind time exactly as it always has;
`WorkflowTemplateComposer` deliberately does not auto-provision (see its own `autoProvisionWorktree:
false` call site for why). **Workaround for a template phase: use `aer run` with a hand-authored
pair**, the same way §2's example runs `review` on `agy` directly — it clears the refusal because it
asks for the write in the first place instead of having one flipped on for it, so `GrantAuditMode`
stays at its `Enforced` default. Hand-editing generated bindings to claim `IsWorktree: true` is still
not a workaround for that case: that field is a stamp the provisioner leaves, and a hand-authored
`true` claims an isolation that does not exist.

Cells marked *works* still require that vendor's CLI to be logged in on this host, and `--adapter`
without `--model`/`--effort` drops the role tier's vendor-specific model.

A role's declared outputs — the `Outputs`/`ProducedOutputs` pair you need for the `aer run` shape — are
in `WorkerRoles.json`, and the table above lists them. `--output <path>` copies the primary one out to
`<path>` once the room reaches Terminal; see `docs/dispatch.md` for its validation rules.
