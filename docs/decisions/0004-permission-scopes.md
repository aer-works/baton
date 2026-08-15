# 0004 — Permissions scope by project, room and step

Status: accepted
Date: 2026-07-22

## Context

> *Vocabulary note (2026-08-15): every "grant" in this record is the **permission** object.
> [0055](0055-an-authority-grant-is-not-a-standing-permission.md) later reserves bare "grant" for the
> authority grant of [0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md). This
> record predates that distinction and is deliberately not rewritten — 0055 §3a holds the reasoning
> and the list of places that cite this record's formulations verbatim.*

Permissions today are a single flat `PermissionGrant` per worker binding, and they are **not
enforced**.

Verified against the live daemon and the real `claude` CLI (#331): a room materialised with
`RunShellCommands: false` was asked to run `hostname` and returned `Compy-2`, this machine's actual
hostname — a value it could only obtain by executing the command.

The translation is not the bug. `ClaudeWorkerAdapter` correctly consults the grant and correctly
omits `Bash`, producing `--allowedTools Read,Edit,Write`. The bug is what that flag means:
`--allowedTools` **pre-approves** tools so they do not prompt; it is not a deny-list. Nothing in
`src/` passes `--disallowedTools` or `--permission-mode`. AER treats an auto-approval list as a
sandbox boundary.

Consequently every permission surface in the product is advisory:

- Author's `Run shell commands` checkbox, left unchecked
- `codebase-session`'s description — *"conservative file/command permissions"*
- **plan mode**, whose whole promise is `WriteFiles: false`

Author compounds it by offering one grant for the whole workflow, captioned *"Leave unchecked to use
each runner's own default permissions"* — so an unchecked box means *inherit*, not *deny*, and there
is no way to express *deny* at all. Meanwhile the engine already supports a grant **per worker
binding** and nothing exposes it.

## Decision

**Three scopes, composing by intersection. Effective grant = project ∩ room ∩ step. Always
narrowing, never widening.**

**1. Project / directory — the ceiling.** Where trust actually lives, and what a human reasons
about: *this scratch folder, anything goes; this client's monorepo, no network and no shell, ever.*
Stable, outlives any room. Completely missing today; this is the new construct. Where the ceiling is
stored and first presented is resolved by
[0034](0034-project-permission-ceiling-lives-in-aers-own-config.md): AER's own app-level config, keyed
by project path, never a file inside the project's own directory.

**2. Room — the working grant.** Plan / default / auto, moment to moment. May only narrow the
project ceiling, never exceed it. Mostly exists already via `/api/sessions/{id}/mode` (the endpoint's
own naming predates the room/session split — see [0013](0013-room-is-the-user-facing-noun.md)).

**3. Step — override inside an authored pipeline.** A review step stays read-only even in a
permissive project. **The engine supports this on `claude`**; Author collapses it to one global
control. Exposing it is a UI change there, not an engine one.

*Scope, and it has moved twice ([#664](https://github.com/aer-works/aer-flow/issues/664)).* A review
step is read-only **and** produces a report, so this clause is only true if a withheld write still
reaches `AER_OUTPUT_DIR`. It did not: [#629](https://github.com/aer-works/aer-flow/issues/629) refused
that binding outright, which made the sentence false on every vendor.
[#649](https://github.com/aer-works/aer-flow/issues/649) made it true on `claude` — the write tools
stay pre-approved and AER's `PreToolUse` hook confines them to the outbox — and left it false on
`agy` for the reason recorded in [#670](https://github.com/aer-works/aer-flow/issues/670).
`IWorkerAdapter.WithheldWritesReachTheOutbox` is where that answer lives, and the bind-time refusal
still fires for any adapter answering no.

**App/global stays tiny** — one or two hard floors at most. Anything larger becomes a second
configuration system nobody reads.

**Vendor is not a scope.** It is a *capability* question — what an adapter can enforce — not a
*policy* question. Conflating them is how `GeminiWorkerAdapter` refusing a grant leaks into
user-facing permission UI. The adapter reports what it can enforce; AER refuses to run when a
required denial is unenforceable.

**Grants fail closed.** If a denial cannot be enforced for the chosen vendor, the run does not start.
Today it starts and silently permits.

**Inheritance must be visible.** "Unchecked = inherit" is coherent only if the control shows the
**effective** value it is inheriting. Author's checkboxes mean inherit and display nothing, which is
why they read as *off* and are not.

## Consequences

**Easier.** Trust is expressed once per project instead of re-decided per room. A read-only
review step becomes expressible. "Plan mode" becomes true rather than aspirational.

**Harder.** Intersection semantics must be legible — a user who checks a box and sees no change
because the project ceiling forbids it will conclude the app is broken unless the UI explains which
scope won.

**Obliges us to.** Fix enforcement **first** (#331). Scopes layered over an unenforced grant produce
a more elaborate lie, not more safety. Ordering is: enforce and fail closed → add the project ceiling
→ expose per-step in Author. The scope **ladder** is offered at the moment of asking, not only in
settings, and a denial is a real answer the worker is told about and continues from
([0022](0022-permission-ladder-and-denial-is-an-answer.md)).

Related: #331 (enforcement), #321 (quick-start binds no directory at all — worst case is unenforced
permissions rooted where nobody chose), #327 (Author's inherit-vs-off checkboxes),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (the asking-moment ladder),
[0034](0034-project-permission-ceiling-lives-in-aers-own-config.md) (where the ceiling lives).
