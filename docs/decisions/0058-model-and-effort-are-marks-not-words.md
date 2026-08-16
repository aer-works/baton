# 0058 — Depth and effort become marks on the workflow-room worker chip

Status: accepted
Date: 2026-07-27

## Context

The corpus carried three answers to the same question. `04-workers-commands-control.md:322`,
`:324` and `:326` disagreed with each other about whether model stays on the chip, moves to the
picker, or joins effort as a third chip axis, and `05-stress-test.md:50` argued from a premise
(effort belongs "beside the model" as text) that `04:324` had already retracted. `04:324`'s
objection was about *width* — painting vendor, model, effort, skills and a permission grant onto
one compact label at once is what made a worker's chip unreadable — while `04:322`, `:326` and
`05-stress-test.md:50` were right that model and effort belong at a glance, not buried a tap away.

An issue comment is not a record, and the corpus does not get to carry three answers to one
question. This record dissolves the conflict instead of picking a side: a mark carries the same
fact as a word, in a fraction of the space.

## Decision

**Depth (model tier) and effort both stay on the workflow-room worker chip, encoded as achromatic
marks rather than words. Vendor stays the chip's word.**

The marks must satisfy four constraints, each pulled from an existing decision this corpus already
carries — this record does not invent new rules, it applies the ones that exist to a case that was
previously unresolved:

1. **Achromatic.** [0006](0006-visual-direction-quiet.md)'s Ink-stance paragraph reserves colour for
   status alone — "the only colour on screen is status," carried into Quiet because it "costs
   nothing and sharpens exactly the information this product exists to convey." Depth and effort
   must read from shape, fill or weight, never colour — a coloured depth meter would compete with
   the one thing colour is reserved for.
2. **Silhouette, not count-of-things.** 0006 rule 2's reasoning — marks must differ in silhouette,
   not merely be distinguishable by colour once you can see it — applies verbatim here. Three depth
   steps and four effort steps must be told apart in greyscale, at chip size.
3. **Tier, never the model string.** [0023](0023-effort-and-models-are-named-by-behaviour.md): the
   mark encodes the tier (deep / balanced / fast) or the effort behaviour (quick / standard /
   careful / exhaustive), never a vendor's model string or flag. The vendor→tier mapping is #498's
   subject and lives in the picker.
4. **Vendor stays a word.** [0017](0017-vendor-model-effort-are-three-choices.md) keeps vendor,
   model and effort as three axes; only two of them become marks. `claude` and `agy` remain the
   chip's primary identity. `VendorIconMap`
   (`src/Aer.Ui/Converters/VendorIconConverters.cs`) already exists — whatever it draws must not
   collide visually with the new marks.

Full purpose-named values (the tier's name, the effort's name) live one tap away in the picker;
the mark on the chip is the at-a-glance signal, not the only place the value is expressed.

### What this deliberately does not decide

Skills are not settled here — deferred to #386, which owns skills and has not been built.
[0033](0033-skills-attach-directly-no-persona.md)'s `vendor · N skills` stands on the chip until
then.

### Scope split from 0054 — this is a new ruling

[0054](0054-participants-turns-and-addressing.md) states its chip rule generally — "chips and
transcript cards show the participant name; model is secondary text" — without drawing a
session-room/workflow-room boundary itself. Its shipped implementation (PRs #1310, #1312) scoped
that rule to single-participant session rooms, where model reads as secondary **text** next to the
participant name because one worker in view carries no multi-axis width pressure.

**This record is what draws the boundary explicitly.** Text-secondary stays the encoding for the
single-participant session-room chip; **marks** are for the multi-worker workflow-room chip, where
several workers are visible at once and width pressure is the actual reason marks exist — the same
pressure `04:324` named. A workflow-room chip carrying vendor, depth, effort, skills and a
permission grant at once cannot afford text for all of it, where a session-room chip with one
worker can. Convergence of the two encodings, if ever wanted, is a future decision — not implied
by either record.

The split is presentation, not noun: per [02-screens.md](../design/02-screens.md) ("adding a
worker never creates a new object; the header chip changes, the room is the same room"), the room
stays one object regardless of participant count. What changes with count is only how the chip
encodes model and effort — text in the single-participant case, marks in the multi-worker case —
never the room's identity.

### What is blocked, and what is not

The marks themselves are composites — a three-step depth meter is filled subpaths beside unfilled
ones in a single mark — and #511 documents that today's mark model (one geometry key plus one
`filled` bool) cannot express that. **Drawing the marks is blocked by #511's model decision.** This
record is not: it resolves the corpus contradiction and states the ruling so the corpus stops
carrying three answers, independent of when the mark geometry ships.

## Consequences

**Easier.** #391 (display) and #498 (choice) unblock together, as the M27 triage already noted they
should — surfacing one of 0017's three axes forces reopening for the others, so shipping the
ruling once avoids sequencing the same conversation three times. The corpus edits this record's
issue also makes (`04:322`/`:324`/`:326` collapsed to one call, `04:140`'s superseded paragraph
folded into `:142`, `05-stress-test.md:50` rewritten) stop citing a retracted premise.

**Harder.** Nothing ships from this record alone — the actual mark geometry waits on #511's
composite-fill decision, and #476 (vendor brand marks) still needs cross-checking so three mark
families on one chip do not collide.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `04-workers-commands-control.md:322/324/326` state three different chip designs | read directly — `:322` says model always on chip, `:324` amends it to picker-only, `:326` puts model and effort both on chip as a third axis | this record has no contradiction to dissolve, and the "resolves three answers" framing is wrong |
| `05-stress-test.md:50` argues from `04:324`'s retracted premise | read directly — it cites "the same reason the model is there," which is `04:322`'s premise, already amended by `:324` | the rewrite in the corpus-edit issue has nothing to fix |
| `VendorIconMap` exists today at `src/Aer.Ui/Converters/VendorIconConverters.cs` | file exists in the repo (#476's subject) | constraint 4's collision warning has no concrete target to check against |
| #511 blocks composite-fill marks, not this record | #511's own body: "decide the model first, because it applies to every future composite" — scoped to mark geometry, not to a corpus ruling | this record would need to wait on #511 too, and should say so |

Related: #641 (this issue), #391 and #498 (unblocked together), #511 (prerequisite for drawing the
marks, not for this ruling), #476 (vendor mark collision check), #386 (skills, deferred).
