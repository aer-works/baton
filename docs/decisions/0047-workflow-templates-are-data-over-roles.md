# 0047 — Workflow templates are data composed over the role catalog

Status: accepted
Date: 2026-08-01

Builds on [0046](0046-a-room-is-a-container.md) (a workflow is work run under a room),
[0003](0003-templates-collapse-to-three-shapes.md) (Pipeline is the DAG-driven shape),
[0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md) (a step is prose + a gate, no template
language), and the shipped rungs: the worker-role catalog (#888/#897) and `aer dispatch <role>` over the
`RoleDispatch` primitive (#900).

## Context

`aer dispatch <role>` runs a **one-step** workflow. Rung 3 is the multi-step case: a **Pipeline** —
role-phases in order, where a review reads the diff of the work before it. Two things are in the way.
The only multi-step composition today, `BuiltInWorkflowTemplates` (M22), is **hardcoded C#** and **not
role-based** — the drift #901 tracks. And the diff a review needs is produced today by making the
*janitor* run `git` (#789's residual "did the cheap model run the command right" risk).

## Decision

**A workflow template is data — a named, ordered list of role-phases plus declared inputs — resolved the
same way the role catalog is, and composed into a DAG by the engine.**

1. **Templates are data, not code.** A shipped default set lives next to the engine; a user directory
   under `AER_HOME` overrides/extends it — the resolution `WorkerRoleCatalog` already uses. A built-in
   template and one **authoring** later saves are the *same format in the same place*, which is
   [0046](0046-a-room-is-a-container.md)/[0003](0003-templates-collapse-to-three-shapes.md)'s "saved
   shapes worth reusing, plus whatever a user authors" made structural.

2. **A template phase names a role**, dispatched via `RoleDispatch` (#900) — so a template composes the
   catalog's atoms and never re-declares a role's outputs/grant/timeout. This **folds #901 in**: the
   built-in set is re-expressed as shipped template data over roles, and the hardcoded parallel catalog
   retires.

3. **The composer turns phases + declared inputs into a DAG.** Phases lay out in order with `DependsOn`
   edges ([0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md)'s implicit previous-output flow
   unchanged). A phase may additionally declare a symbolic **input** — e.g. `diff-of-work-so-far` — from
   a **closed, engine-defined set**; the composer inserts the matching capture step before it and wires
   it in. This is *not* [0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md)'s forbidden template
   language: the author still writes only name + worker + instruction + gate; the symbolic input is an
   engine capability keyed by a fixed name, not a user expression composing outputs. (Called an **input**,
   not a "need", to stay clear of [0015](0015-three-kinds-of-needs-you.md)'s "needs-you" —
   [0002](0002-one-vocabulary.md).)

4. **The capture step is a closed, fixed, engine-defined operation — never a template-supplied command.**
   `diff-of-work-so-far` maps to the engine's own `git diff <base>` — the base ref (the workspace HEAD at
   workflow start, injected by the run entrypoint) against the **working tree**, not `base..HEAD`, so
   committed *and* uncommitted work land in the diff and no upstream worker is forced to commit. It is
   invoked with engine-controlled arguments (directly, not by interpolating into a shell string). It is deterministic engine machinery,
   so it carries no vendor grant — but it is safe **only because it is not arbitrary.** The engine runs
   any `CoreDispatchTarget` it is handed with **no permission check of its own**; enforcement lives
   entirely in the vendor adapters' `Resolve` (the `PreToolUse` hook), and the 0034 project ceiling is
   unbuilt. So an *arbitrary* engine-run command would bypass the grant translation, the hook, and the
   ceiling in full — exactly what `DialogueWorkerAdapter` already refuses ("*a declared vendor that could
   still run an arbitrary command would … report an enforcement it does not have, which is worse than a
   known gap*"). The closed set is that refusal applied here: the capture step is expressly **not** a
   generalization of `NoOpWorkerAdapter` into "run any declared command" — that path inherits
   `NoOpWorkerAdapter`'s ungated shell-string shape and voids its triviality justification.

5. **`aer dispatch <name>` widens to run either.** `<name>` resolves to a role (→ the one-step workflow
   #900 builds) or a workflow template (→ the composed Pipeline). **One namespace, kept unique** —
   authoring refuses a template named after a role. The result in both cases is a **workflow run** under a
   room ([0046](0046-a-room-is-a-container.md)), not a new room.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The engine applies no permission check; it spawns whatever `CoreDispatchTarget` it is handed | **measured** — `CoreDispatcher.DispatchAsync` does variable expansion + the ARG_MAX guard + capture, no grant/hook logic; every `new CoreDispatchTarget` in `src/` is inside `Aer.Adapters` (research 2026-08-01) | an engine-run command might inherit enforcement, and the closed-set constraint is less critical |
| Grant enforcement lives in the vendor adapter's `Resolve` (the `PreToolUse` hook), opt-in via `IPermissionGrantTranslator` | **measured** — `WorkerBindingResolver.cs:117-125`; `ClaudeWorkerAdapter` appends `--settings`→`hook-check`; only claude/gemini implement the translator | an engine step could be gated the same way a worker is |
| AER already refuses running an arbitrary command under a gate it cannot enforce | **measured** — `DialogueWorkerAdapter.cs:154-171` throws when a participant's command diverges from the vendor preset | the closed-operation design lacks precedent and is merely one option |
| The 0034 project permission ceiling is accepted but unimplemented | **measured** — grep of `src/` finds only doc-comment references; `ClaudeWorkerAdapter.cs:537` defers whole-surface fail-closed to it | a ceiling might catch an errant engine command, softening the closed-set requirement |
| `NoOpWorkerAdapter` runs a fixed `echo` with no gate, justified only by triviality | **measured** — `NoOpWorkerAdapter.cs:20-29` interpolates the output name into a `cmd`/`sh` string; class doc scopes "no vendor CLI" to that triviality | generalizing it to run a declared command would be a safe, small change rather than a hazard |

## Consequences

**Easier.** One template format and resolution path serves built-ins, user-authored templates, and the
CLI; the orchestrator (#778) and a person pick from the same set. The review-reads-the-diff contract
becomes deterministic and engine-enforced rather than resting on a janitor's shell command, retiring
#789's residual risk.

**Harder.** The capture step is real (if small) engine surface with a hard boundary: a **closed,
enumerable set** of engine operations, each invoked without shell-string interpolation, with a test that
a template cannot introduce a new command. The capture also diffs against a base ref in a workspace —
honest only once the engine *provisions* it (rung 4 / #669); until then it runs against the assumed
workspace as `dispatch.py` does today, stated as interim.

**Obliges us to** re-express the built-in templates as data over roles (closing #901 as part of this),
keep the capture operation set closed and shell-injection-free, amend #887's rung bullets to the
template-data model, keep `dispatch.py`'s own convergence (#898) separate, and have M29 authoring write
this same format to this same location.

**Relates to** [0046](0046-a-room-is-a-container.md), [0003](0003-templates-collapse-to-three-shapes.md),
[0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md), [0004](0004-permission-scopes.md)/[0034](0034-project-permission-ceiling-lives-in-aers-own-config.md)
(the ceiling this must not bypass), and [0015](0015-three-kinds-of-needs-you.md) (the "needs" it avoids
colliding with).
