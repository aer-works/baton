# 0026 — Running out of plan is a state with a reset time, not a generic failure

Status: accepted
Date: 2026-07-25

## Context

This product runs on **subscriptions you already pay for** ([0012](0012-what-aer-flow-is.md)). That is
the commitment the whole architecture is built around — adapters that own no key-handling code,
shelling out to whatever CLI is authenticated on the host. It has a consequence nobody wrote down:

**the dominant real-world failure for a subscription user is running out of plan.**

Not a crashed worker, not a bad tool call — hitting a weekly cap and being locked out for hours. The
behavioural spec saw this and said so, in `spec/aer-flow-behavioral-spec-v1.0.md` §21:

> **Quota/rate-limit exhaustion is inexpressible in the failure model.** … `FailureClassification`
> says only `Permanent | Retryable`; `RetryPolicy` counts attempts and would **burn every one against
> a still-exhausted quota**; and no-daemon plus no-wall-clock determinism means **nothing wakes up
> when the window resets**. … **this must be decided before the classification types freeze (M7 Phase
> 1).**

M7 Phase 1 closed. **`#18`, the issue tracking it, was closed 2026-07-10 with no comment and no
record.** `src/Aer.Flow/Domain/FailureClassification.cs` reads `{ Retryable, Permanent }` today: the
types froze on the interim behaviour, which the spec described as a stopgap.

Then the ground-up design pass ran for a day and produced seven artifacts, nineteen records and
eighteen journeys — and quota appears in none of them **as a failure**. It appears twice as a *cost
affordance*: [0019](0019-consulting-is-not-deciding.md) notes a consulted worker *"costs real quota"*,
[0024](0024-commands-are-namespaced.md) notes `/ask-all` *"multiplies quota"*. Both are about
spending. Neither is about hitting zero.

Worse, the one place a limit *is* handled points the wrong way.
[0018](0018-attention-is-the-primary-signal.md) sorts a **rate-limited** vendor into band 4 — the
muted band, *"states you are not asked to act on."* That is right for a background room and wrong for
the worker you are watching, and it is the same inversion 0018 caught and corrected for
host-unreachable without applying it here.

**The failure this produces:** turn 60 of a long room, the plan hits its weekly cap, and the room goes
quiet in the calmest band the product has. `/ask-all` — one keystroke, one turn per worker — is the
fastest way to get there, and it is the flagship gesture.

## Decision

**Running out of plan is a first-class outcome with a reset time, distinct from both retryable and
permanent failure.**

**1. The engine gains a third classification.** `FailureClassification` becomes
`{ Retryable, Permanent, ExhaustedUntil }`, carrying the reset instant the vendor reported — or an
explicit *unknown*, which is a different and honest thing from a guess. `RetryPolicy` must not spend
attempts against an exhausted quota; an `ExhaustedUntil` outcome consumes no retry budget, because
retrying is not what is wrong.

**1a. `claude` gives the detection signal a name: `errorCode: "credits_required"`.** A dispatched
turn that fails on subscription quota reports this typed error code, distinct from an ordinary
`is_error` failure — that is what `ClaudeWorkerAdapter` matches to route into `ExhaustedUntil` rather
than `Retryable`, not string-matching the model's own account of what went wrong. `agy` has no
documented equivalent; on that vendor the classification still applies, but the trigger has to come
from a different, currently unmeasured signal (or fall back to *unknown* on every occurrence).

The spec offered two candidate resolutions — a third classification value, or treating quota as a
pause. **We take the classification**, because a pause implies someone can answer it and nobody can:
the only thing that resolves this is time passing or a different subscription. Modelling it as a pause
would put an unanswerable item in the "needs you" list, which is exactly the credibility problem
[0015](0015-three-kinds-of-needs-you.md) and 0018 exist to protect against.

**2. Determinism is preserved by consulting the clock only where it already is.** §13's no-wall-clock
rule holds because the reset instant is read **inside a mutation call and frozen into the event**,
the same shape §21 identified as compatible. Replay reads the recorded instant; it never re-reads a
clock.

**3. It is per vendor, and the room says which.** A room where one vendor is exhausted and another is
not is **not** blocked — it is a room with fewer available workers, and the interface must say that
rather than reading as broken. This falls out of [0017](0017-vendor-model-effort-are-three-choices.md):
vendor is a separate axis, so exhaustion is a property of the vendor, not of the room.

**4. It is loud where you are looking and quiet where you are not.** This amends 0018's band
assignment: a vendor exhausted in **the room you have open, on the worker you just addressed**, is an
attention state — you asked for something and it will not happen. The same exhaustion in a background
room is band 4, correctly. The discriminator is *did the operator just try to use it*, not *what kind
of state is it*.

**5. Nothing wakes up, and the product says so rather than pretending.** The engine has no scheduler
and this record does not add one. When the window resets, work resumes because a person comes back and
resumes it — so the room must show **when** that is (*"resumes after 14:00"*), not spin. A reset time
the vendor did not give is displayed as unknown; a fabricated estimate is worse than an honest gap,
for the same reason [0023](0023-effort-and-models-are-named-by-behaviour.md) refuses to invent an
effort mapping.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Plan/quota exhaustion is distinguishable at the CLI boundary from an ordinary worker failure | **assumed** — no `vendor-verify` check probes an exhausted plan, and exhausting one to measure it spends a real plan window | the state cannot be entered reliably, and exhaustion degrades back to the `Retryable` misclassification this record exists to replace |
| A reset time is recoverable from what the vendor prints when it refuses | **measured for `agy`, 2026-08-12 (#1128)** — a real cap hit during a dispatched run printed "Resets in 1h39m10s" in the stdout result envelope, and the adapter parses it; still assumed for `claude`'s refusal message specifically (its `/usage` reset instants are measured, its cap-hit refusal text is not) | the state exists but carries no reset time, so "nothing wakes up when the window resets" stays true and only the manual path remains |
| `FailureClassification` is `{ Retryable, Permanent }` and nothing else | **measured** — `src/Aer.Flow/Domain/FailureClassification.cs`, with the ordinals pinned by `FlowEventLogJsonTests` (#604) | the premise that the types froze on the spec's stopgap is wrong, and this record's framing needs re-deriving even if its conclusion survives |

## Consequences

**Easier.** The most common real failure stops being indistinguishable from a crash. *"I'm out of
Claude until Thursday"* is a sentence the product can say, and the operator can act on it — switch
vendors mid-room ([0017](0017-vendor-model-effort-are-three-choices.md) makes that expressible), or
come back. It also gives `#479`'s spend view a purpose beyond curiosity: the number is worth showing
because there is a cliff at the end of it.

**Harder.** This is a change to `Aer.Flow`'s domain types, which the rebuild otherwise leaves alone —
so it is the one engine change M26–M30 must make, and it widens a vocabulary that projections, the
wire protocol and both surfaces all read. Doing it late is worse: every consumer of
`FailureClassification` written against two values has to be revisited.

**Corrected 2026-07-26 (#503, item 8).** This section originally called what each vendor reports about
remaining quota and reset time *unprobed*. It has since been measured, asymmetrically:
`claude -p "/usage"` (and `/cost`) reports percent consumed for session and week, **real reset
instants**, a per-model breakdown, and request counts, headlessly — the corpus's mockup number,
*"72% of this week's limit"*, is the shape of a number the CLI already returns, not a placeholder
(`docs/vendor-capabilities.md`). `agy` reports **none of it**: no built-in usage command, nothing in
`--log-file`, nothing in its conversation metadata. So the degrade-to-unknown path this record
requires is not a hedge against an unmeasured gap — it is `agy`'s permanent, measured state, and
`claude`'s reset instant should be treated as reliably available rather than merely hoped for.

**Corrected 2026-08-12 (#1128).** The paragraph above conflates two channels, and only one of them
is empty. What `agy` still reports **none of** is *proactive* usage — no usage command, nothing in
`--log-file` or conversation metadata, so there is no `agy` equivalent of *"72% of this week's
limit"*. But its **refusal message** carries a reset time after all: a real cap hit during a
dispatched run printed *"Individual quota reached. … Resets in 1h39m10s."* in the stdout result
envelope, which `AgyWorkerAdapter` now parses into an `ExhaustedUntil` instant. So degrade-to-unknown
is `agy`'s state for *anticipating* the cliff, not for landing on it — at the moment of refusal, a
reset instant is recoverable on both vendors.

**Amended 2026-08-13 (#1184).** Attended execution (an interactive chat turn) splits from unattended execution (a workflow step) using §4's discriminator — *did the operator just try to use it*. An interactive turn's first `ExhaustedUntil` outcome settles immediately so the operator receives prompt feedback and can re-send after reset; it does not schedule a paced retry obligation or park into a multi-hour wait. Unattended workflow steps keep the paced park unchanged.

**Obliges us to** never spend retry attempts against an exhausted quota; record the reset instant at
mutation time and never re-read a clock on replay; keep exhaustion per vendor rather than per room;
treat it as an attention state only where the operator just tried to use it — and, per the 2026-08-13
amendment above, pace the wait only where nobody is; and never fabricate a reset time — required now
for `agy` specifically, not as a generic caution.

**Relates to** [0018](0018-attention-is-the-primary-signal.md), whose band-4 assignment for
rate-limited this amends. [0008](0008-runtime-streaming-over-append-log.md) — per-turn cost is
intrinsic, and this is what that cost eventually runs into.
[0011](0011-token-based-context-management.md) is the *other* limit a room hits; they are different
and must not be conflated: context filling up is per worker and recoverable by compaction, plan
exhaustion is per vendor and recoverable only by time.

Related: `#18` (closed without a decision — this is that decision), `#479` (spend against subscription
limits), `#472` (the probe that did not cover usage), and the behavioural spec's §21.
