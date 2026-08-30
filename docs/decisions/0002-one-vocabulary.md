# 0002 — One vocabulary, no translation map

Status: accepted
Date: 2026-07-22

## Context

The project maintained a ~20-pair map translating engine terms into user-facing ones. It required
permanent human discipline on every string, and it decayed exactly as that predicts. Shipping today
as primary user-facing text:

- `Terminal` as a task's status on the fleet list and in the mobile app
- `turn-anchor` as a step name on the Home decision card
- `turn.marker` in *"Waiting for your review — turn.marker ready"*
- `prompt.txt` as the artefact named on a human decision card
- `[pause -> draft]` on a DAG node
- *"Nothing is waiting on you — terminal."* as the completion message

Home does translate `Terminal` to **"Finished"** — correctly — while the fleet list, the mobile app
and the completion message do not. So the map is not merely decaying; the same value renders
differently on three surfaces of one product.

## Decision

**Retire the translation map. Code and UI use the same words.**

Rename the code to the plain word wherever a good one exists, and update the spec alongside. Keep a
spec term only where no plain equivalent exists, and then use that term in the UI too rather than
translating it.

Enforce mechanically rather than by discipline: a CI lint over user-facing string literals in
`**/*.axaml` and Dart sources, failing on engine vocabulary outside an allowlist (#315).

## Consequences

**Easier.** A string is correct or the build fails, on the commit that introduces it. No one has to
remember a table.

**Harder.** Renaming normative terms in `spec/aer-flow-behavioral-spec-v1.0.md` touches the document
the project calls its source of truth, and some engine terms have no good plain equivalent — those
need a deliberate choice rather than a lookup.

**Obliges us to** pick the words once, in one place, and treat that as a decision rather than a
per-screen judgement. The map failed for the same reason the spec went stale: both relied on someone
remembering.

Related: [0001](0001-two-nouns-workflow-and-session.md), #315 (the lint), #314 (spec claims checked
in CI).
