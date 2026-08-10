# 0052 — The ladder ships without the cross-room rung; its home is project scope, not account scope

Status: accepted
Date: 2026-08-09
Amends: [0022](0022-permission-ladder-and-denial-is-an-answer.md)

## Context

[0022](0022-permission-ladder-and-denial-is-an-answer.md)'s decision table lists five rungs, in order:
just-once, *this command in this room*, *any command in this room*, **this command in any room**, and
the standing refusal. Building the runtime gate (M-Phase-6, #390) makes four of them concrete and
enforceable now — persisted on the room's chat-worker `PermissionGrant` in the room's own
`bindings.json`, enforced next turn (claude via `--allowedTools`/`--disallowedTools` patterns, agy via
its `PreToolUse` hook). The fourth rung — **any this command in *any* room** — is different in kind: it
is the only rung whose grant is not room-scoped, and building it requires a decision the corpus does not
make, namely *where a cross-room standing grant is stored*.

A design-readiness pass over the permission corpus (0004, 0016, 0022, 0031, 0034, `06-answers.md`,
`spec/journeys.md`) found that this storage is genuinely unspecified, and that shipping a four-rung
ladder while 0022 stands unamended would be a silent amendment to an accepted decision — the drift the
`record-once` gate exists to stop.

## Decision

**The ladder ships with its four room-scoped rungs only. The cross-room rung is not offered yet, and
when it returns it is a *project*-scoped grant, not an account-wide one.**

Reasoning, in order of weight:

1. **There is no scope to hang it on.** 0004's model is project ∩ room ∩ step, always narrowing. There
   is no cross-room scope in it, and 0034's project store is a *subtractive* ceiling — a maximum — not an
   *additive* standing grant that pre-approves a command.

2. **Account-wide is the wrong home.** The only account-wide precedent is skills
   ([0031](0031-skills-are-account-wide.md)), and it is weaker than its shape suggests: a skill is a
   capability the person *chooses to attach per worker* — attachment is the scoping act — whereas a
   standing grant applies with no per-room act at all. A per-command "allow everywhere" list is a
   *second* account-wide configuration system, which [0004](0004-permission-scopes.md) warns against by
   name ("app/global stays tiny — one or two hard floors at most, or it becomes a second configuration
   system nobody reads"). It is also the rung most exposed to 0022's own anti-reflex thesis: it converts
   one click under prompt fatigue into standing *account-wide* policy — the corpus's "allow everything
   forever is not a permission system," one notch down.

3. **Project scope is the right home, and it is cheaper.** What a person who wants "any rm in any room"
   almost always means is *"I trust rm in this project."* 0004 already has a project scope, and 0034
   already keys AER-side per-project state by normalized absolute path. A project-scoped standing grant
   therefore needs no new account store, creates no second configuration system (it rides the store 0034
   already obliges building), and — because project keying is by path — does not even wait on the open
   room-folder question below.

4. **It rests on a question the design leaves open.** `06-answers.md` records *"does a room live in one
   folder forever?"* as explicitly the owner's open call, and the meaning of "any room" shifts with the
   answer. (Project keying by path sidesteps this, which is a further argument for landing it there.)

5. **The honesty clause requires it.** 0022 §"what the vendors can actually enforce" (lines 94–96): where
   AER cannot enforce — or here, cannot even *define the storage of* — a rung, it says so at the moment
   of granting rather than implying a guarantee it cannot keep. A rung with no defined home is exactly
   that, and not offering it is the honest form.

The **standing refusal** rung ("always deny this command") is unaffected and *does* ship: it is
room-scoped like the allow rungs, persisted as `DeniedShellCommandPatterns` and enforced deny-beats-allow
on both vendors. Only the cross-room *allow* rung is held.

## Rests on

| fact | how we know | if false |
|---|---|---|
| 0004's scopes are project ∩ room ∩ step, with no cross-room scope | **the record** — [0004](0004-permission-scopes.md) §Decision | a cross-room scope already exists to hang the rung on, and this hold is unnecessary |
| 0034's project store is a subtractive ceiling, not an additive grant | **the record** — [0034](0034-project-permission-ceiling-lives-in-aers-own-config.md) | the project store could hold the grant as-is today and the "needs a store" premise weakens |
| 0034 keys AER-side per-project state by normalized absolute path | **the record** — 0034 §"Obliges us to" | project scope would itself depend on the room-folder question, and argument 3's "does not wait" clause fails |
| "does a room live in one folder forever?" is open | **the record** — `docs/design/06-answers.md` §"What is still genuinely open" | "any room" has a settled meaning and the rung could be scoped precisely now |
| The four room-scoped rungs are enforceable now on both vendors | **measured + built** — `RuntimePermissionGrantAmenderTests`, `AgyHookCheckCommandTests`, `docs/vendor-capabilities.md` | the ladder ships fewer than four rungs and this record overstates what is delivered |

## Consequences

**Easier.** The ladder that ships is entirely enforceable and entirely room-scoped — one storage model
(`bindings.json`), one lifecycle (the grant dies with its room, [0009](0009-session-lifecycle-and-retention.md)),
no account-wide configuration surface to design or explain.

**Harder.** A person who genuinely wants a command trusted across every room in a project cannot express
that yet; they re-grant per room until the project-scoped store exists. That is the cost, and it is
smaller than the cost of a second account-wide config nobody reads.

**Obliges us to** offer only the four room-scoped rungs at the moment of asking; state plainly (per the
honesty clause) that "any room" is not among them; and, when the project-scoped standing-grant store is
built on 0034's per-project keying, return the rung there — as a project grant, never an account one.

Related: [0004](0004-permission-scopes.md) (the scope model with no cross-room scope),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (the ladder this amends),
[0031](0031-skills-are-account-wide.md) (the account-wide precedent this rung deliberately does *not*
follow), [0034](0034-project-permission-ceiling-lives-in-aers-own-config.md) (the per-project store its
future home rides).
