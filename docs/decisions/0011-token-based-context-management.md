# 0011 — Context management is token-based, per worker, not turn-based

Status: accepted
Date: 2026-07-23

## Context

A long conversation must eventually compact — summarise its history and continue in a fresh
native vendor session — or it overruns the model's context window. AER already does this:
`ExecuteSessionTurnAsync` computes `isCeilingReached = metadata.TurnCount >= metadata.SafetyCeiling`
and, when the ceiling is crossed, forces a handoff (`SynthesizeContextSummary` + a fresh native
session, resetting the turn count). A manual `POST /api/sessions/{id}/compact` does the same on
demand. So the *mechanism* — auto-compact via handoff, plus manual compact — exists and works.

It is counted in the wrong unit. A **turn ceiling is a crude proxy for context pressure**: ten
one-line turns and ten 50k-token turns are treated identically, so the ceiling fires too early on a
light conversation (throwing away usable context and paying for an unnecessary summary) and too late
on a heavy one (risking an overrun the compaction was supposed to prevent). The thing that actually
determines when to compact is *tokens consumed*, and the vendors report it — `claude
--output-format stream-json` emits a usage figure on its `result` line. The same usage stream is
what a cross-vendor cost view (J9, [0008](0008-runtime-streaming-over-append-log.md)) has to capture
anyway, so accounting for it pays twice.

The considered alternative was the cheap one: leave the trigger turn-based and merely expose
`SafetyCeiling` as a user setting. Rejected — it makes the wrong proxy configurable instead of
replacing it.

## Decision

**The context-management trigger is token-based, and the unit is the worker, not the room or
session.** Each participant tracks its own running token usage against its own model's window, from
the vendor's reported usage, and a threshold **announces a choice** — summarise now, start a fresh
room carrying the conclusion, or leave it — rather than acting silently. Automatic compaction
survives only as a disclosed backstop, for when the choice goes unanswered and the window is
genuinely about to overflow; it is never the default trigger. The turn count remains the fail-safe
fallback when a vendor reports no usage at all.

This re-bases `SafetyCeiling`'s trigger role onto tokens; it does not invent a new mechanism (the
handoff and `/compact` endpoint stay as they are). See [0027](0027-context-is-per-worker.md) for the
full mechanics — the `SessionMetadata` object-model gap this requires, the per-worker headroom
example, and why this is a different limit than [0026](0026-running-out-of-plan-is-a-state-not-a-failure.md)'s
running-out-of-plan.

## Consequences

**Easier.** Compaction fires when it should, not on an unrelated turn count, and not on a room-wide
sum that would compact one worker's window because another one filled up. The token stream is shared
with J9's usage/cost view — one capture, two consumers. The threshold becomes a setting that means
something a user can reason about ("compact near 70% of the window"), model-aware because the window
differs by model.

**Harder.** Per-vendor usage accounting must live in the adapter (Adapter Isolation, CLAUDE.md rule
2): `claude`'s stream-json usage is available, and `agy`'s **is now verified too** (#1088) — its
`--output-format stream-json` `result` event carries a `usage` object (input/output/thinking/
cache_read/total tokens); it is token counts, not a dollar figure. (A prior version of this note said
agy's was unverified; that traced to a probe-grammar bug, not agy — strip `CLAUDE_CODE_*` when probing,
see the vendor-CLI probe runbook.) The threshold has to be model-aware, which couples it to knowing the
active model ([0010](0010-skills-and-advisor.md)'s participant-as-binding, surfaced by #391). Tracking
per worker rather than per session requires the participant dimension `SessionMetadata` currently
lacks — the same object-model work `#493` scopes.

**Obliges us to.** Capture usage into a per-worker accounting structure. Keep the turn-count backstop
as the fail-safe, so a session against a usage-less vendor still compacts. Announce at a threshold
rather than acting silently, and disclose what an unanswered, automatic compaction dropped. Verify
`agy`'s usage reporting before enabling token-based compaction for Gemini.

Relates: [0027](0027-context-is-per-worker.md) (the mechanics and code-level detail behind the
per-worker unit and the announced-choice trigger), [0008](0008-runtime-streaming-over-append-log.md)
(runtime, worker lifetime, the usage stream), [0010](0010-skills-and-advisor.md) (model as part of a
participant binding), #395 (implementation), #338 (the Settings surface the threshold lives in), #391
(model visibility — the window is per-model), J9 (usage/cost view).
