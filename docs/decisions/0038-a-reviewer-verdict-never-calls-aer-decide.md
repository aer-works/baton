# 0038 — A reviewer's verdict is evidence for a human decision, never the decision itself

Status: accepted
Date: 2026-07-26

## Context

`docs/plan.md` has carried this as an open question since M25: *"whether a delegated
implementer/reviewer loop can run without a human calling `aer decide`. `DecisionType`
(`Resume`/`Reject`/`RetryWithRevision`/`Supersede`) already exists and is exactly the primitive such
a loop needs, resolved via `aer decide` at a step's declared `PausePoint` — but `PausePoint`'s own
doc comment records every pause as awaiting *human* review/approval. Nothing in the code enforces
that; nothing has tried the alternative either."*

This session ran exactly this loop by hand for `#579`: a Gemini implementer, then an independent
Gemini reviewer, then a human-directed decision — verify the reviewer's findings independently, fix
what it flagged, and only then merge. A prior session's own memory concluded the natural next step was
automating the last part too: *"a small, cheap, structured-output-only decision-maker reading the
reviewer's typed verdict and calling `aer decide` — not a new engine feature, just a non-human caller
of an existing one."*

**That conclusion is wrong, and [0019](0019-consulting-is-not-deciding.md) already says so.** 0019
names *"approve this, choose that"* as exactly the gates it governs — `PausePointKind.ReadyForReview`
is the first one, by definition (*"the step ran to a terminal outcome and its result awaits human
review/approval before the DAG proceeds"*). And 0019 forecloses the specific substitution a
decision-maker calling `aer decide` off a reviewer's verdict would be: *"Nothing but your answer
closes the gate — not a consulted worker agreeing, not all of them agreeing."* Its own Consequences
section names the exact failure mode this would produce: *"the risk is that consulting feels so
cheap it becomes procrastination... the gate must therefore stay visibly pending, and the consulted
answers must not accumulate into something that looks like a resolution."* An auto-resolving reviewer
verdict is that failure mode taken to its limit — the gate does not merely risk looking resolved, it
*is* resolved, by something other than the operator.

**The Architecture Rule 1 argument in the prior memory is a category error, not a reason.** Rule 1
permits routing on an explicit tool return — which *step* runs next. It says nothing about who holds
*decision authority* over a gate. A reviewer's structured verdict being a legitimate thing to route on
does not make it a legitimate thing to decide with; 0019 governs the second question and 0019 already
answered it.

## Decision

**This was never an open question. `docs/plan.md` mischaracterized something 0019 already settled.**

- **`aer decide` is called by a human, always, for every gate kind — including `ReadyForReview`.**
  No structured verdict from any worker, however well-scoped or however independently produced,
  substitutes for that call.
  > **Amended 2026-08-03 by [0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md),
  > term 5:** "a human, always" was over-scoped — 0049 owns the corrected wording (a delegate under
  > an explicit recorded grant may also close a gate; whoever produced the evidence never can). The
  > bullet's remaining content stands.
- **The delegated implementer/reviewer loop is real, valuable, and needs zero new primitives** —
  exactly the shape this session ran by hand: one workflow (implement → review, review declaring
  `PausePoint(ReadyForReview)`), the reviewer's typed verdict presented as the evidence a human weighs
  (the same *"summary + verbatim + queryable"* disclosure 0019 already requires for any consulted
  opinion), and a human's own `aer decide` call to resolve it. **This session's own #579 loop *is* the
  shape** — the human-verification step was load-bearing, not overhead to automate away later.
- **What M27 actually builds here** is making that loop *cheap to start* (an authored Pipeline
  template, per 0003 — "implement → review" is already exactly `05-stress-test.md`'s own worked
  example), not removing the human from its terminal gate.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `DecisionType` carries `Resume`/`Reject`/`RetryWithRevision`/`Supersede`, and `PausePoint` documents every pause as awaiting human review | **measured** — `src/Aer.Flow/Domain/DecisionType.cs`, with the ordinals pinned by `FlowEventLogJsonTests` (#604) | the primitive is not what this record says it is, and the "already exists" half of the argument fails |
| Nothing in code enforces the human half of that documentation | **measured** — read directly; no caller-side check exists | the constraint is already structural, and this record restates an invariant rather than deciding one |
| A delegated implementer→reviewer loop is runnable with a human at the decision point | **measured** — run by hand for #579, and again across #563/#588/#598/#604 | the loop's feasibility is theoretical, and the record decides against an option nobody has shown works |

## Consequences

**Easier.** Nothing to build to close this — the primitive (`DecisionType`/`PausePoint`) already
supports the valuable half of this pattern (fast, cheap, parallel implement+review), and the
constraint on the other half was already decided elsewhere. This record exists to stop the question
being re-opened, not to open new work.

**Harder.** Nothing new — this forecloses a shortcut, not a capability. A future session tempted to
build "auto-merge on a clean review" (exactly what a `RetryWithRevision`-consuming autonomous
decision-maker would become) needs to read this record and stop.

**Obliges us to.** Correct the prior session's memory
(`project-aer-cross-vendor-delegation-path.md`), which currently reads as an endorsement of the
auto-deciding shape this record forecloses.

Relates: [0019](0019-consulting-is-not-deciding.md) (the controlling rule, cited directly rather than
re-derived), [0015](0015-three-kinds-of-needs-you.md) (the three pause kinds, of which
`ReadyForReview` is one), [0003](0003-templates-collapse-to-three-shapes.md) (the Pipeline shape this
loop is authored as).
