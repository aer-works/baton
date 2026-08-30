# 0022 — The permission ladder is offered at the moment of asking, and a denial is a real answer

Status: accepted; **cross-room rung held by [0052](0052-the-ladder-ships-without-the-cross-room-rung.md)**
Date: 2026-07-24
Amends: [0004](0004-permission-scopes.md)

> **The ladder ships without the "any this command in *any* room" rung — see
> [0052](0052-the-ladder-ships-without-the-cross-room-rung.md).** The room-scoped rungs in the table
> below stand, and are what the runtime gate builds and enforces. Only the cross-room rung is held: the
> corpus defines no store for a cross-room grant, and 0052 records that its home is *project* scope, not
> account scope — to be reopened when that store is built.

## Context

[0004](0004-permission-scopes.md) settled the **scopes** — a permission is bounded by project ∩
session ∩ step, failing closed. That is the model, and it stands. What it does not say is anything
about the *moment a permission is asked*, and the corpus's position is that this moment is where the
entire design succeeds or fails.

The problem is not showing a prompt. From
[`04-workers-commands-control.md`](../design/04-workers-commands-control.md):

> The design problem is not showing the prompt — it is **stopping the prompt from becoming a
> reflex**.

And the failure mode is stated precisely: *"'Allow once' for everything trains people to click
through; 'allow everything forever' is not a permission system."* An unscoped prompt is worse than no
prompt, because it manufactures the appearance of a safety control while training the exact habit
that defeats it. The corpus's stress test puts a number on the pressure: *"Fifty prompts a day trains
the click-through reflex the design exists to prevent."*

Two things follow that 0004 does not carry, and both were absent from the repo.

The second one is separable and, if anything, more load-bearing. 0004 fails closed — correct — but
says nothing about what a *refused* worker experiences. The corpus does:

> A denial is a real answer, not a cancel. The worker is told it was refused and carries on with that
> knowledge — it does not silently retry, and it does not die. **That is the difference between a
> permission system and an obstacle course.**

`docs/vendor-capabilities.md` shows this is implementable rather than aspirational: `claude` returns
the whole denied call as structured `permission_denials` with the tool name, id and input, replayable
once a human answers; `agy` names the missing permission and the rule that would grant it. Both
vendors hand back enough to tell a worker *what* was refused and *why*.

## Decision

**The scope ladder is offered where the question is asked, and a denial is an answer the worker
continues from.**

**1. The ladder is visible at the moment of asking.** From the corpus's permission screen, the rungs
in order:

| Rung | Meaning |
|---|---|
| Just this once | the default, and the narrowest |
| Any *this command* in this room | the command family, bounded by room |
| Any command in this room | the room ceiling |
| Any *this command* in any room | the command family, unbounded by room |
| Never / always deny *this command* | the standing refusal |

These are 0004's scopes surfaced as choices, not a new model. The rule is that they are **visible at
the point of asking and not buried in settings** — the corpus: *"the middle rungs are what make it
survivable, and they must be visible at the moment of asking."* The stress test adds the repeat case:
<!-- record-once-ok: #1242 docs/design/05-stress-test.md -->
*"if a person is answering the same permission twice, the second time should offer the standing
permission first."*

**2. Standing permissions are visible and revocable, listed per room**, because *"a permission you
granted three weeks ago and cannot find is indistinguishable from no permission system at all."*

**3. Denial is an answer.** The worker is **told it was refused** and continues with that knowledge.
It does not silently retry and it does not die. The room records the denial as a turn — the corpus
draws it as *"you · denied — Denied `rm -rf build/`. claude was told and is continuing."* Denial is
therefore a **first-class outcome of a turn**, not an error path and not a cancellation.

**4. Keyboard-first, and never on a reflex key.** `y` and `n` answer a focused permission, and
**neither is ever bound to `Enter`**. The corpus's keyboard map states the general rule: *"a
destructive action never sits on a key you might hit by reflex. Enter sends; it never approves, never
denies, never deletes."* A gate clearable by muscle memory is decorative — the same finding as #331,
arriving at the interface instead of the adapter.

**5. A pending permission dies with its turn.** It cannot outlive the turn that raised it: stopping
the worker removes it from the room, the "needs you" list, and the phone **at the same moment**,
because those are three views of one object ([0020](0020-one-state-machine.md)). The transcript
records that a permission was pending when the turn was stopped, so the history explains itself.
Notifications already delivered are withdrawn where the platform allows, and opening a stale one
lands on the room saying the request no longer exists rather than on a dead prompt.

### What the vendors can actually enforce, stated honestly

The ladder is a *product* model. `#472` measured what sits under it, and the two vendors differ in a
way that must not be hidden:

- **`agy` matches command rules literally, against the whole command line** — not by prefix, not by
  regex. A family-shaped rung ("any `rm` in this room") is **not expressible** as an `agy` allow-rule;
  the enforceable instruments there are `--sandbox` plus targeted `unsandboxed(…)` escapes, or the MCP
  consultation path.
- **`agy --sandbox` genuinely enforces**; `claude`'s project ceiling is advisory.

So a rung's *promise* must match its enforcement on the chosen vendor. Where AER cannot enforce a
ladder rung, it says so at the moment of granting rather than implying a guarantee it cannot keep —
which is `docs/vendor-capabilities.md`'s standing instruction for 0004 and applies with more force
here, because this record puts the rungs in front of a person as choices.

## Consequences

**Easier.** The safety surface stops being a tax. A person answering fifty prompts a day answers most
of them once, with a scope, and the ladder is the reason the count falls instead of the attention
paid to each one. Denial-as-an-answer makes refusing genuinely usable — you can say no without
killing the work, which is the only way "no" gets used at all.

**Harder.** A denied turn now has to *continue*, which means the adapter must feed the refusal back
into the worker's context in that vendor's own shape and the transcript must record it as an outcome
rather than an error. Standing permissions become persistent state that has to be listed, attributed
to a room, and revoked — and on `agy` every standing permission is an edit to a **global** settings file with no
project-local override, so "this room only" is a fiction AER maintains above a vendor that cannot
express it. That gap is real and has to be visible rather than papered over.

**Obliges us to** show the ladder at the point of asking and never only in settings; offer the
standing permission first on a repeat; record a denial as a turn the worker continues from; keep
`y`/`n` off `Enter`; kill a pending permission with its turn on every surface at once; list standing
permissions per room with revocation; and state plainly where a rung is advisory rather than enforced on the
chosen vendor.

**Relates to** [0004](0004-permission-scopes.md), which this amends — 0004 owns the scope model, this
owns the moment. [0015](0015-three-kinds-of-needs-you.md) classifies the pause; this governs the
permission kind specifically. [0019](0019-consulting-is-not-deciding.md) applies while a permission is
open: you may ask anyone about it, and consulting never answers it.
[0020](0020-one-state-machine.md) is what makes "dies everywhere at once" expressible.

Related: `#445` (the mechanism a permission is raised through), `#331` (permissions were advisory —
the defect this exists to make impossible), `#481` (`y`/`n` never bound to `Enter`), `#462` (a queued
message must not send into a blocked worker), `#472` (what each vendor can enforce).
