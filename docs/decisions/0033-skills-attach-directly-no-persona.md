# 0033 — Skills attach directly to a worker; there is no Persona object

Status: accepted
Date: 2026-07-26

## Context

The M27 design pass (`docs/design/02-screens.md`) introduced **Persona**: a named, saved preset
binding one Skill plus vendor/model/effort plus a permission grant plus a "voice" string onto a
worker's chip — a distinct object a person creates, saves to a library, picks from a picker, and
can diverge from ("modified" state, reset-to-preset, save-as-new).

A design-readiness audit of the full M27 corpus, followed up in conversation, surfaced that most of
Persona's cost came from being a *second object* layered on top of Skill, not from anything the
underlying capability needed:

- **Shipping eight predefined Personas** (Scout, Courier, Scribe, Artisan, Debugger, Auditor,
  Advisor, Architect) imposed an identity taxonomy before a single skill had been validated against
  real use — the exact thing Claude Code's own zero-predefined-subagent baseline avoids.
- **The persona picker's shipped-eight-presets list is itself misleading** the moment none of them
  actually ship — a picker listing selectable named things claims they're real, the same species of
  problem as a chip showing a preset's name instead of the worker's identity
  ([[feedback-persona-is-a-qualifier]] from this corpus's own earlier correction).
- **The "modified" chip state, reset-to-preset, and save-as-new machinery** existed only to track a
  worker's relationship to a *saved preset* — `02-screens.md` itself marked the exact behavior
  "your call, not yet decided."
- **A Persona bound exactly one Skill.** There was never a stated reason a worker couldn't simply
  carry more than one skill at once — Persona's single-skill shape was an artifact of being a named,
  singular preset, not a real constraint on what a worker can do.

## Decision

**A worker attaches Skills directly — zero, one, or several at once. There is no Persona object.**

- **Attaching a skill is not naming or saving anything.** A worker's current skill set *is* its
  current configuration, not an instance of some external preset. There is nothing to diverge from,
  so there is no modified state, no reset-to-preset, and no save-as-new — that entire mechanism
  doesn't need cutting, because it never needs to exist.
- **The chip shows a count, not a name.** A bare worker chip (`claude`) stays exactly as drawn
  today. A worker with skills attached reads `claude · 2 skills`; the attached skills are visible on
  hover or in the chip popover, by name. There is no compressed single-name identity to invent,
  because there is no longer a single named thing to compress.
- **Vendor, model, and effort remain three separate axes**
  ([0017](0017-vendor-model-effort-are-three-choices.md)), completely unaffected by this record.
  Attaching a skill does not touch them.
- **The permission grant stays a separate axis, never silently widened.** A skill may declare tool
  requirements ([0010](0010-skills-and-advisor.md): "instructions + tool requirements + bundled
  assets"); attaching it checks those requirements against the worker's actual grant
  ([0004](0004-permission-scopes.md)'s `project ∩ session ∩ step, always narrowing, never
  widening`). A skill that wants `Bash` attached to a read-only room's worker fails to attach with a
  clear reason — it does not expand what the room already allows.
- **The room orchestrator's authority is an ordinary attached Skill**, auto-bound to whoever holds
  the role and auto-detached on reassignment ([0032](0032-room-orchestrator-is-mandatory.md)) — the
  same mechanism as any other skill, not a special case.
- **A starter library of skills is a defensible thing to ship, unlike predefined Personas.**
  Attaching a skill doesn't claim a worker's identity ("this worker now *is* an Artisan") — it says
  the worker currently has a set of instructions loaded, which can be added, removed, or combined
  freely. A small library of real, useful skills (a thorough-review checklist, a commit-message
  style, a reconnaissance pass) is closer to shipping example prompts than shipping an identity
  taxonomy, and does not conflict with the zero-predefined bar this pass is judged against. What
  goes in that starter library, if anything, is not decided here — it depends on the canonical skill
  schema, which is not yet defined (see *Obliges us to*, below).
- **The persona-creation drawer's model-purpose × effort grid is retired.** It existed to organize a
  library of named presets for browsing; a flat, searchable list of skills replaces it, since a
  skill is vendor/model/effort-agnostic content, not a point on that grid.
- **The "voice" field is not carried forward as a separate concept.** It doesn't need its own
  mechanism ([#577](https://github.com/aer-works/aer-flow/issues/577), closed as moot by this
  record) — tone and personality are just instructions, and a skill already *is* instructions
  ([0010](0010-skills-and-advisor.md)). A skill author who wants a particular voice writes it into
  the skill's own instruction text, which already has a defined path to every vendor (native
  `SKILL.md` for `claude`, the plugin/agent equivalent for `agy`, prompt-injection as the graceful
  floor). Nothing new to build.

**The word "Persona" does not appear anywhere in `docs/design/` after this pass.** It was a real
idea that was tried, found to cost more than it bought, and replaced — not a synonym still worth
using informally.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Claude Code ships zero predefined subagents | **assumed** — observed in the vendor corpus; no check probes it | the "imposes an identity taxonomy before validation" argument loses its baseline, though the second-object cost argument stands without it |
| A worker's vendor identity is renderable alongside a skill count, without a preset name replacing it | **measured** — this record's own chip rule, against `design/tokens.json`'s status roles | the chip has to carry a name after all, which is precisely the failure this record exists to prevent |

## Consequences

**Easier.** One object (Skill) instead of two (Skill + Persona). No picker that implies shipped
identities. No modified-state tracking, reset button, or save-as-new dialog to build or test. No
separate voice-field mechanism to define. Orchestrator authority uses the same mechanism as every
other capability instead of a bespoke flag.

**Harder.** The chip's compact-identity value that a named Persona gave ("this worker is doing
Artisan-style work, at a glance") is gone; `claude · 2 skills` is accurate but less evocative than
`claude · Artisan` was. Judged an acceptable trade for removing an entire object layer and its
failure modes.

**Obliges us to.** Remove every occurrence of "Persona" from `docs/design/*.md` and
`docs/design/mockups/*.html` and replace the picker, chip-popover, and creation-drawer content with
skill-attachment equivalents (tracked as the corpus sweep this record accompanies). Close
[#577](https://github.com/aer-works/aer-flow/issues/577) as moot. The canonical skill schema —
[0010](0010-skills-and-advisor.md)'s other still-open item — is now the single most load-bearing
undecided thing in this design, since a starter library, the picker, and every worker capability
all route through it; it should be scoped before M27 build work starts on this area.

Relates: [0010](0010-skills-and-advisor.md) (the skill model this record builds directly on),
[0031](0031-skills-are-account-wide.md) (where the library lives),
[0032](0032-room-orchestrator-is-mandatory.md) (the orchestrator-skill relationship),
[0004](0004-permission-scopes.md) (the grant a skill's tool requirements are checked against),
[0017](0017-vendor-model-effort-are-three-choices.md) (the axes this record does not touch),
[#577](https://github.com/aer-works/aer-flow/issues/577) (voice field, closed as moot by this
record), [#578](https://github.com/aer-works/aer-flow/issues/578) (working-status verbs, unaffected
by this record — still open).
