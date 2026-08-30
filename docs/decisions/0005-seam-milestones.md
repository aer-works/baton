# 0005 — Capability milestones alternate with seam milestones

Status: accepted
Date: 2026-07-22

## Context

Twenty-four milestones produced a sound engine and an unusable product. The engine was verified
working during the M25 evaluation: multi-vendor orchestration, the event log, per-task concurrency
and pause semantics all behave correctly — Claude drafted, Gemini reviewed, the run paused at its
gate, and the approval landed from a phone.

Every defect found was in a **seam between specified components**, not in a component:

| Defect | Seam |
|---|---|
| Phone never sees desktop-started work (#330) | in-process run path <-> WebSocket broadcast |
| Decision inbox scoped to one task (#319) | mobile inbox <-> daemon's open-task singleton |
| Approve without seeing the output (#320) | engine PausePoint <-> UI presentation |
| Quick-start binds no directory (#321) | mobile start call <-> materialiser defaults |
| Permissions unenforced (#331) | grant translation <-> what the CLI flag actually does |
| No timestamps in the list (#322) | list UI <-> API contract |

Milestones were capability-shaped — M21 = mobile, M24 = sessions. Each was independently verifiable
and each genuinely passed. No milestone was ever *"a person starts work on their desktop and
approves it from their phone"*, so that path was nobody's deliverable, and it is exactly the path
that broke.

M25 was originally scoped **"Final Polish."** That single mis-scope is how an integration-shaped
problem shipped as done.

## Decision

After every N capability milestones, one **seam milestone** whose only deliverable is integration —
no new capability.

A seam milestone's completion gate is a **journey** passing end to end across surfaces (#312), not a
component test. Journeys are what a seam milestone exists to protect.

## Consequences

**Easier.** The integration work has a home and an owner instead of being everyone's implicit
background responsibility, which in practice meant nobody's.

**Harder.** It costs a milestone of visible feature progress on a cadence, which will feel expensive
precisely when the product seems to be working — which is exactly when the seams are rotting
unobserved.

**Obliges us to** define journeys before the first seam milestone can be scoped (#312), and to build
journey tests that drive the real UI (#313), since every existing completion gate (`smoke-claude`,
`smoke-mixed-vendor`, `smoke-dialogue`, `smoke-session`) drives the engine or the daemon's HTTP
surface and **none touches a UI**.

**Note the recurrence, and how fast it was.** Manual testing on 2026-07-21 found chat, commands,
mode and remote broken behind an all-green PR. The lesson was recorded as *"API-level tests don't
prove UI works"* and nothing structural changed. It recurred at larger scale **the following day**.

The one-day gap is the reason this record exists. A lesson that decays over a quarter can be blamed
on memory; one that fails within twenty-four hours was never a control in the first place — writing
it down *was* the entire response, and writing things down does not fail builds. That is the
distinction this decision is meant to enforce: every item it obliges us to (#312, #313) is a
required artifact or a gate, not a note.
