# 0049 — The wake loop is in-contract, and the orchestrator is a resident presence that decides

Status: accepted
Date: 2026-08-03

Amends [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) (narrows its "a human, always") and
revises the room spec §8 orchestrator entry's recorded posture. Extends the #778 register: the
2026-07-30 residency shape and the 2026-08-01 projection/origination contract both stand; this
record settles what sat above them. Owner decisions from the 2026-08-03 conversation, recorded in
their words.

## Context

The engine spec's §20 fenced one capability off by name: any future "watch and react automatically"
capability would be a *"different system built on top […], not a revision to this contract"* (§20's
own words, elided). The room spec re-affirmed that fence by reference — while `RoomWakeBridge`
(`src/Aer.Daemon/RoomWakeBridge.cs`) ships it: a daemon-hosted loop that watches `room.jsonl`,
derives the room's wake set, and escalates memory proposals on its own tick. The #704 audit
(2026-08-01, on the issue) named this the one real unrecorded decision left in the spec work.

Separately, the room spec §8 recorded (from 2026-07-30) an M26 floor of *"a resident orchestrator
[holding] lanes of work with **every** decision escalated to a person."* The owner overrode that as
a posture on 2026-08-03; and the register already carried the tension unreconciled — 0038 (07-26)
says *"`aer decide` is called by a human, always, for every gate kind"*, while #778's body (07-30,
operator-settled) says the enforceable line *"deliberately does NOT forbid an orchestrator (a
different agent) deciding within its grant."*

The owner's model, verbatim (2026-08-03): *"they should be exactly like me interacting with you,
through Claude code mobile app, right this instant. except that instead … I would open up a room in
Baton instead of a session in Claude code … then we would talk through everything that we're talking
through right now, including having you make decisions and do things and kick off workflows … The
only difference would be it is inside of a room in baton instead of inside of a session in Claude
code."* And on the floor posture: *"we shouldn't just have every decision go to the human … the room
orchestrator is necessary and it is necessary for the room orchestrator to make decisions."* And the
container model it lives in (2026-08-01): *"the room is the outer container, and it runs a workflow,
which doesn't change its name when it runs."*

## Decision

**1. The wake loop is in-contract at the room level, and the engine fence stays intact.** The
daemon-resident wake loop *is* the "different system built on top" §20 predicted — §20's exclusion
remains true of the engine, which still advances only inside mutation-interface calls. The loop is a
client of the engine: derived state (the wake set) is recomputed fresh, never persisted, never
authority; every autonomous mutation it makes is a journaled event through the mutation interface,
under the room's guard, with structural attribution. The 2026-08-01 projection/origination line
(recorded on #778) governs it: projection is in-contract plumbing with no grant; origination
requires an explicit, scoped, recorded grant.

**2. Every room has an orchestrator; authority is the dial, not existence.** Per the owner's
2026-08-01 statement, a room *"automatically comes with a room orchestrator … defaulted to something
that maybe the user can set, or you can pick when you start the room."* There is no orchestrator-less
room; "chat with the room" always has an answerer, and chat is not a separate thing — the
orchestrator is the room's conversational front door (the 2026-07-30 residency shape already made it
a chat worker; this confirms the convergence). The "escalate everything" posture survives only as a
build-sequencing milestone on the way to parity, never as the product default. Whether the
orchestrator is *awake* is presence, not existence: a headless CLI run with no daemon renders
"configured, not resident" rather than pretending otherwise.

**3. The authority model is Claude-Code-session parity, enforced by AER, vendor-agnostically.**
The UX is the permission model a Claude Code user already knows — a default mode in which the
orchestrator acts, per-room allow rules, and the three kinds of "needs you" (permission / decision /
action) as the escalation surface. Parity includes the ask-when-it-matters behavior: ambiguous
intent, irreversible actions, large spend still reach the human. The **enforcement** is AER's own,
at the daemon's tool boundary — the orchestrator's verbs (dispatch, decide, status, artifacts) are
AER-exposed tools and AER checks the grant before executing, identically whichever vendor is
speaking. Vendor-native permission machinery is per-vendor defense-in-depth, never the contract —
the uniform-enforcement *mechanisms* differ measurably (`--permission-prompt-tool` honoured on
claude, rejected by agy; MCP wiring is per-invocation flags on claude, a machine-global config file
on agy), and AER's own agy hook today inspects tool names only — an AER implementation gap, not a
vendor limit: agy's hook payload carries full tool arguments, a framing #659 itself already
corrected once.

**4. What the loop and the orchestrator owe** (the contract terms, plain):

- **Signed actions** — every autonomous mutation carries decider identity and the grant it acted
  under; "who decided this and were they allowed to" is answerable from the ledger alone. **Owed,
  not yet held at HEAD**: `HeldWorkDispatched` carries a decider identity but no grant field, and
  the sweep passes a hardcoded constant identity — rung 5 builds the rest (see Rests on).
- **The leash** — per-room bounds on concurrent dispatches and wake rate. Wake delivery to the
  orchestrator is event-driven and debounced, never per-poll-tick; an idle orchestrator at zero
  authority spends nothing (wakes route to humans exactly as today).
- **Nobody grades their own homework** — the worker whose execution produced the evidence under
  decision can never be that decision's decider, enforced as an identity comparison. Unchanged from
  #778's recorded line.
- **Escalation is a first-class outcome** — at the edge of its grant the orchestrator hands the
  pause up rather than failing or guessing.

**5. Amendment to 0038 and 0019.** Their core insight stands and is the third term above: the
evidence producer's verdict never closes its own gate, and a *consulted* opinion never accumulates
into a resolution — consulting is still not deciding. What both over-scoped is the same phrase:
0038's "a human, always" and 0019's "only the operator's answer closes a gate" (0038 derived its
rule from 0019's, so correcting one without the other would leave the contradiction one document
deep — each now carries a dated note): the corrected rule is that a gate is closed by **a human, or by a delegate acting within an
explicit, scoped, recorded, revocable grant — and never by the evidence producer**. A grant is not
consultation; it is the human's answer given in advance and scoped, on the model of the operator's
own standing mandate to the assistant (named escalation triggers, revocable). #778's 2026-07-30
correction already recorded this; 0038's and 0019's texts are hereby reconciled to it.

**6. Vendor readiness is a prerequisite surface.** Everything above runs on live subscriptions:
connected / authenticated / has-quota is knowable per vendor, shown to the user in the room, and an
orchestrator that cannot act says why ("dormant: no vendor has quota until 18:00") rather than
sitting silent. #594 (quota classification build, decision 0026's `ExhaustedUntil`) moves onto the
critical path; #802 defines behavior when quota blocks a step.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Resumable sessions exist on both vendors (`claude --resume`/`--continue`; `agy --continue`/`--conversation <id>`, resume key handed at gate time) | **measured** — `docs/vendor-capabilities.md` | the resident-orchestrator session design becomes vendor-specific, breaking term 3's parity |
| MCP tools work on both vendors, including a blocking tool holding a turn open | **measured** — `docs/vendor-capabilities.md` ("A blocking MCP tool holds a turn open — on both vendors") | the AER-owned tool boundary cannot be the uniform enforcement point |
| The uniform-enforcement mechanisms are asymmetric: `--permission-prompt-tool` honoured on claude, rejected by agy; agy's MCP config is a machine-global file, not per-invocation flags | **measured** — `docs/vendor-capabilities.md` | vendor-native enforcement could carry the contract and term 3's split would be unnecessary caution |
| The tool-name-only scoping limit on agy is AER's own hook (`AgyHookCheckCommand` does not inspect arguments); agy's hook payload carries full tool arguments | **recorded** — #659's own corrected framing (the "vendor can't" version shipped once and was corrected there) | agy defense-in-depth is capped by the vendor after all, and #659's hook upgrade buys less than planned |
| Term 4's signed-actions claim is an obligation, not a description of HEAD: `HeldWorkDispatched` records decider identity but no grant field, and the sweep's identity is `MemoryProposalEscalation.DefaultDeciderIdentity`, a constant | **measured** — `RoomMutationInterface.cs` (event shape), `MemoryProposalEscalation.cs` (constant), read 2026-08-03 | the ledger already answers "were they allowed to" and rung 5's signed-actions build is already done |
| `RoomWakeBridge`'s autonomous mutation goes through the mutation interface under the room's guard, with structural attribution | **measured** — `RoomWakeBridge.cs` §#878 doc comment and `MemoryProposalEscalation`, read 2026-08-03 | term 1's "the loop is a client of the engine" claim needs code changes, not just recording |
| The wake set is derived fresh each tick and never persisted | **measured** — `RoomWakeBridgeState` doc comment (identical set on restart) | the determinism story needs more than "derived state is never authority" |
| An always-present orchestrator at zero authority costs nothing while idle | **design intent, unmeasured** — depends on wake delivery never spending a model turn outside the grant (term 4's leash) | "always present" carries a standing cost and the existence-vs-authority dial needs a cost story before shipping |

## Consequences

**Easier.** #704 closes honestly: the last unrecorded overtaking is recorded, and the spec can
describe `RoomWakeBridge` as what it is. #778's rung 5 builds on a recorded contract instead of a
fence it would have to break. The grant-vocabulary question dissolves into permission-model parity —
no new authority language for users to learn, which is the product bar (would a Claude Code user
find this as clear?) met by construction.

**Harder.** The leash, the readiness surface, and event-driven wake delivery are now owed builds,
not options: #594 and #802 join the critical path, #552's resource bound generalizes to the
orchestrator, and the daemon's tool boundary must do its own room-scoping on agy (its MCP config is
a machine-global file, so per-invocation tool scoping is unavailable there — enforcement by session
identity instead).

**Obliges us to** add the wake bridge to room spec §5 and revise §8's orchestrator entry (this PR);
point the engine spec's §20/§21 at the answer (this PR); design wake→orchestrator delivery
event-driven and debounced before rung 5 ships; and build the vendor-readiness surface before any
grant beyond the floor is honest to offer.

**Does not change** the engine spec §§3–18, Architecture Rule 1 (the orchestrator's judgment lives
in a worker; routing stays structured), the 2026-08-01 projection/origination line, the 2026-07-30
occupant-portability constraint (role stays room-owned and occupant-independent), or 0019's
anti-consultation content — a consulted opinion still never closes a gate.

**Relates to** [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) (amended),
[0019](0019-consulting-is-not-deciding.md) (amended in the same narrowing; its anti-consultation
core reaffirmed),
[0046](0046-a-room-is-a-container.md) (the container this presence lives in),
[0026](0026-running-out-of-plan-is-a-state-not-a-failure.md) (term 6's build),
[0012](0012-what-aer-flow-is.md) (the room worldview), and the #778 register (extended).
