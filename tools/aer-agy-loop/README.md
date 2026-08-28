# `aer-agy-loop` — dispatch one AER workflow step, read back its output

```
pixi run aer-dispatch -- --list-templates

pixi run aer-dispatch -- \
    [--template <name from --list-templates>] \
    --prompt-file <path> \
    --output-name <name> \
    --working-directory <absolute path> \
    [--adapter agy] [--model <name>] [--effort <level>] \
    [--read-files|--no-read-files] [--write-files|--no-write-files] \
    [--run-shell-commands|--no-run-shell-commands] [--network-access|--no-network-access] \
    [--timeout-minutes 20] [--dry-run]
```

Prints the produced output file's content to stdout. On failure, prints `aer run`'s own output plus
the raw `flow.jsonl` event log to stderr (the CLI's terminal summary alone — `Workflow status:
Terminal / worker: Failed` — carries no diagnostic detail; the log usually does) and exits non-zero.

## Why this exists

Every one of these was hand-rolled with an ad-hoc Node one-liner during the #513 cross-vendor
orchestration trial, and got a different bug each time:

- `WorkflowTemplateVersion` is an `int`, not a semver string.
- `Steps[].Inputs` and `Contract.OptionalMetadata` are JSON arrays, not objects.
- `--room-dir` must be absolute. A relative one resolves against the CLI's own cwd, but `agy` runs
  with cwd set to `WorkingDirectory` (`AgyWorkerAdapter.cs`: `agy -p` ignores the process working
  directory entirely). A relative room-dir plus an explicit `WorkingDirectory` silently produces an
  `AER_OUTPUT_DIR` the dispatched process resolves against the wrong root — the run exits 0, the
  step is reported `Failed`, and nothing says why.

Exactly the failure mode `tools/vendor-verify/README.md` already names: established once, in a
scratch directory, thrown away with the session. This exists so the next dispatch doesn't re-pay
for the same three bugs.

## What this is not

A single-step dispatch primitive, not a loop orchestrator. Whether a reviewer's verdict means "loop
back to the implementer with these findings" is a decision this script does not make — that stays
with whoever is orchestrating the exchange. Automating that decision into glue code would be the
same shape of mistake Architecture Rule 1 already forbids inside the engine itself (Flow must never
parse conversation content to make routing decisions) — this just names that the same discipline
applies one layer up, in tooling that could otherwise grow into a shadow engine.

## Templates — pick the role, not the settings

`--template <name>` pins vendor, model, effort, permission grant and
timeout as a set. Run `pixi run aer-dispatch -- --list-templates` for what each one is
for and what it resolves to; the definitions and the reasoning behind each setting live next to the
`TEMPLATES` dict in `dispatch.py`, and are deliberately not restated here.

Two things worth knowing before reaching for one:

- **Precedence is explicit flag > template > built-in default**, so `--template review --model haiku`
  does what it says. The templates are a starting point you can override, not a lock — every
  permission has a `--no-` arm. (Under `--lane` the per-dispatch overrides are refused instead —
  see the lane section below for why.)
- **A read-only dispatch is refused before it can spend**, because a worker satisfies its
  `ProducedOutputs` contract only by writing into `AER_OUTPUT_DIR`
  ([#629](https://github.com/aer-works/baton/issues/629)). Granting the shell instead is not an
  escape: a granted shell reaches reads, writes and the network whatever the other flags say, so AER
  refuses that combination at bind time and `dispatch.py` refuses it here first
  ([#529](https://github.com/aer-works/baton/issues/529)). "Read-only reviewer" is therefore not
  expressible — both routes to it are closed. The guard in `dispatch.py` carries which arm is
  measured on which vendor; that scope is not repeated here.

A pinned `agy` model name is checked against `agy models` by STEP 9 of
`pixi run audit-completeness`.

## `--lane` — implement, janitor, review as one three-step run

`--lane --prompt-file <implement-brief> --working-directory <repo>` builds ONE workflow whose three
steps are the shape of every shipping lane — `implement` (your brief) → `janitor` (the canonical
`janitor-prompt.md`, verbatim) → `review` (a generated adversarial brief over `git diff main...HEAD`,
producing the schema-checked `verdict.json` the review template requires) — chained with `DependsOn`,
each step carrying exactly its template's settings, and the engine's own scheduling replacing the
three hand-orchestrated dispatches this loop used to run ([#741](https://github.com/aer-works/baton/issues/741)).

Because each step resolves from its own template, the single-dispatch knobs (`--template`,
`--worker-name`, `--output-name`, and every model/effort/grant/timeout override) are **refused, not
ignored** — a flag that looks accepted and does nothing would be the worse behavior. `--worktree`
composes with it as usual. Outputs land per step: `implement-report.md`, `janitor.md` (the filename
the canonical brief itself instructs), `report.md` + `verdict.json`.

## `--dry-run` — resolve, guard, generate, stop

Applies flag precedence, runs every guard, writes `workflow.json`/`bindings.json`, prints the
resolved grant, and exits **without calling `aer run`**. Spends nothing, and does not need a built
`Aer.Cli.exe` — it reports its absence instead of failing, which is what lets
`pixi run audit-selfcheck` dry-run every template in CI.

It stops *after* the JSON is generated, not before, because all three bugs in "Why this exists" live
in that build. A dry run that skipped it would validate only the half that was never the problem.

Grants the guards **refuse** still exit 2 under `--dry-run` — it reports what a real run would do,
and a real run would be refused. That is the property that makes it a test surface: exit 0 means
dispatchable, exit 2 means refused, and both are now free to ask.
[#639](https://github.com/aer-works/baton/issues/639) is why it exists: before it, only the
refused combinations could be checked without paying, so verifying that an *allowed* grant is
allowed cost a live run — and the one time that was checked, it did.

## Using this for an advisor consult

The `advise` template is this, pinned. `--model gemini-3.1-pro-high` (agy's Pro tier, high reasoning
effort — see `agy models` for the full catalogue) is a reasonable default when dispatching a consult
rather than an implementation or review task.

**Ground it — don't ask it cold.** A bare knowledge question about a fast-moving CLI is a
training-data-staleness risk, not just a style preference: asked "what CLI flag does agy use to
auto-approve every tool permission request?" with nothing to read, `gemini-3.1-pro-high` answered
`--yolo` — confidently, and wrong. The real flag is `--dangerously-skip-permissions`. That wasn't
pure fabrication: `-y`/`--yolo` was a real flag on Google's Gemini CLI, the line `agy` (Antigravity
CLI) evolved from, and the model correctly recalled a real fact about a related tool without
noticing the two have since diverged — nothing in a bare question prompts that check. The fix isn't
"trust it less," it's "give it something to read": paste the actual current `agy --help` output (or
point it at `docs/vendor-doc-audit.md`, which records measured — not vendor-claimed — behavior) into
the prompt rather than asking from its own memory. This generalizes past this one flag: any fact
about this project's own fast-moving tooling should be grounded the same way.

**Higher effort does not fix this.** Re-asked the identical bare question at `--effort high`
(`gemini-3.1-pro-high --effort high`), the model gave the exact same wrong answer (`--yolo`) it gave
without an explicit effort setting. This isn't surprising in hindsight: effort controls how much a
model deliberates on *working through* a problem, not whether it *checks* a fact it already believes
it knows. A pure-recall question has nothing to deliberate on — more effort just delivers the same
wrong memory more confidently, it doesn't verify it. Grounding (see above) is the only thing that
actually helps; bumping effort is not a substitute for it.

## The shell-commands guard

`--run-shell-commands` without `--network-access` is refused client-side, before ever calling `aer
run`, and so is `--run-shell-commands` with reads or writes withheld. Both are the same rule: **a
granted shell reaches reads, writes and the network whatever the other flags say**, so withholding
one of them does not withhold it ([#529](https://github.com/aer-works/baton/issues/529)). AER
refuses these at bind time; `dispatch.py` refuses them here so the flags fail before you commit to
them rather than after.

The network arm carries a second, vendor-specific reason on gemini: `GeminiWorkerAdapter` has no way
to unlock shell commands without also unlocking network access (`--dangerously-skip-permissions` is
the only confirmed non-interactive bypass agy exposes, and it grants everything together — see the
adapter's own `TryTranslatePermissionGrant`). That reason applies to one vendor; the #529 one applies
to both.

## Where the worker runs build+test

A worker that compiles and tests its own changes runs that `build+test` several processes deep in the
dispatch tree, inside the Job Object aer-core creates for the worker subtree. That job is created with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and **no** memory or process cap
(`external/aer-core/src/os/windows.rs`). Two consequences follow, and both point the same way:

- The job cannot bound a runaway worker — a heavy in-tree `pixi run gates` (two MSBuild+Roslyn passes
  plus test hosts) has nothing capping what it allocates.
- `KILL_ON_JOB_CLOSE` means the instant the job holder — the .NET engine that P/Invokes aer-core —
  goes down, the whole worker subtree dies at once: **no terminal event**, and nothing in the killed
  subtree gets the chance to log its own exit. Anything that takes the holder down presents as the
  worker vanishing silently.

Measured twice ([#917](https://github.com/aer-works/baton/issues/917)): on a memory-starved box, both
dispatches whose worker actually ran `pixi run gates` in-tree died mid-flight — `flow.jsonl` stopped
at `executionStarted` and the output artifact was left a stub — while every write-only dispatch that
session survived. The initiating trigger stayed unproven, but the shape is clear and the job offers no
cap to soften it.

**So: workers write files; the orchestrator runs the one authoritative `pixi run gates`** in its own
shallow tree, outside aer-core's job, and gates the worker output there. That is also the cleaner
split — one authoritative gate run, not one per worker.
