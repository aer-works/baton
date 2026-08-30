# 0025 — A step's instruction is its body, and "ask me first" is a toggle on the step

Status: accepted
Date: 2026-07-24
Amends: [0014](0014-shapes-are-a-list-not-a-canvas.md)

## Context

[0014](0014-shapes-are-a-list-not-a-canvas.md) settled the **shape** of a shape: an ordered list that
renders as a graph, not a freeform canvas. What it does not say is what a *step* contains — and the
corpus found that the design had drawn the editor without the single most important field on it.
From [`05-stress-test.md`](../design/05-stress-test.md), under *"things I put on a screen without
thinking them through"*:

> The shape editor has name, worker, and "ask me first". **It has no prompt field.** A step called
> "review" must tell its worker what reviewing means, and I omitted the single most important thing
> about a step.

[`06-answers.md`](../design/06-answers.md) closes it and does not soften the assessment — *"this was
the worst omission in the design."*

The second half is a call that looks small and is load-bearing. Every workflow tool with human
oversight models it as a **node type** — an approval node, a gate node, a manual task — which means
oversight is a thing you add to a graph rather than a property of the work. The corpus rejects that
in [`02-screens.md`](../design/02-screens.md): *"A gate is a property of a step, not a node you add.
'Ask me first' is the entire mental model for human oversight — one toggle, in the place you are
already looking."*

## Decision

**A step is a name, a worker, an instruction, and one toggle.**

**1. The instruction is the step's body — not a field behind a disclosure.** The row shows the name,
who runs it, and whether it gates; the instruction sits underneath as the content. The corpus's own
example:

> **draft** · claude · opus · careful
> *Write a plan for the change described in the room. Be specific about files and order of work.*
>
> **review** · antigravity · gemini 3 pro · ask me first
> *Critique the plan above. Name anything that will not work, and say why. Do not rewrite it.*

**2. A step's blocker's output flows in implicitly. There is no template language.** By default that
blocker is the step above ([0014](0014-shapes-are-a-list-not-a-canvas.md)); when a step names a
different blocker, *that* step's output is what flows in instead — the rule is unchanged, only which
step counts as "previous" moves. **No variables, no interpolation, no expression syntax** — the corpus
is explicit that this is *"the complexity that makes workflow tools miserable, and the whole reason
this is a list rather than a canvas."* The same argument that killed the canvas kills the template
language: both move attention off the actual decision and onto the machinery. **Two or more blockers
have no expression for composing their outputs** — a step with several blockers receives no defined
combination of their results, which is the real limit 0014 names, not a missing gesture.

**3. A step with no instruction is invalid, and says so at edit time** — not at run time. An
unauthored step that only fails when it runs is a trap, and the editor already knows.

**4. "Ask me first" is a property of a step, not a node type.** One toggle is the entire mental model
for human oversight. Turning it on puts a gate *before* that step. This is also what makes a shape
readable at a glance: the gates are the highlighted rows, so *"where do I get a say"* is answered by
scanning rather than by tracing edges.

**5. It survives on a phone**, which is the payoff 0014 predicted and this record confirms: drag to
reorder, tap to edit, one step per screen, and *"Ask me first"* is a switch on that screen. The
corpus notes a phone is genuinely **better** than a mouse at reordering a list, so the primary
structural gesture is the one the device is best at.

## Consequences

**Easier.** Authoring a shape becomes writing three sentences and flipping a switch. There is exactly
one concept to teach for human oversight, and it lives where you are already looking. The whole
model diffs cleanly in version control — an instruction is prose in a list, so a template's history
reads like a document's rather than like a serialised graph. And a template becomes genuinely
portable across vendors, since the instruction carries the intent and nothing carries a vendor's
prompt syntax.

**Harder.** Refusing a template language means the cases it would have served need real answers:
composing two outputs together, or writing an instruction that reads from more than one prior step,
has **no expression** in this model — naming a blocker (0014) says a step waits on another step, not
how to combine what several of them produced. That is a deliberate limitation, worth paying, and it
will be argued again the first time someone wants it. Edit-time validation is also a new obligation on
the editor rather than the engine, and the two must agree about what "valid" means or a template can
pass one and fail the other.

**Obliges us to** make the instruction the step's visible body on both surfaces; pass previous output
implicitly with no syntax to learn; reject an instruction-less step at edit time with a message that
says what is missing; keep "ask me first" a boolean property of a step rather than a node type or a
separate object; render gated steps as visually distinct rows; and resist adding variables — if a case
genuinely needs them, that is a new decision record superseding this one, not a quiet feature.

**Relates to** [0014](0014-shapes-are-a-list-not-a-canvas.md), which this amends — 0014 owns the
list-not-a-canvas shape, this owns what a row of it contains.
[0015](0015-three-kinds-of-needs-you.md) classifies the pause "ask me first" produces (an *approval*,
`ReadyForReview`). [0017](0017-vendor-model-effort-are-three-choices.md) and
[0023](0023-effort-and-models-are-named-by-behaviour.md) supply the other half of a step's *who* — a
step names vendor, model and effort, in AER's own vocabulary.

Related: `#339` (collapse the five templates to three shapes with presets), `#327` (Author's preview
graph), `#320` (approval-gate defaults).
