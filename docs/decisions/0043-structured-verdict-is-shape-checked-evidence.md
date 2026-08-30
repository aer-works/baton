# 0043 — A review verdict is a schema'd contract output, shape-checked by the engine and never read by it

Status: accepted
Date: 2026-07-30

## Context

Every review lane this project has run returned its verdict as free prose, and the orchestrating
session regex-grepped severity words out of markdown to decide what to act on — measured across six
consecutive review lanes on 2026-07-29/30 (the register is field note 4 on
[#665](https://github.com/aer-works/aer-flow/issues/665); the promotion is
[#732](https://github.com/aer-works/aer-flow/issues/732)). Decision 0038 already fixed the
boundary — a verdict is evidence for a decision, never the decision itself — but unstructured
evidence cannot be routed, stored, or shown without a person re-reading the whole artifact.

The engine already validated output *content* in one narrow way: `OutputCondition` (spec §4.1),
"exists, parses as JSON, pointer equals scalar."

## Decision

**A `ProducedOutput` may declare an `OutputSchema` from a closed set; the engine validates parse at
outcome classification exactly like a condition; validation is parse-only — the engine never reads
the parsed content.** The mechanics are recorded once in spec §4.2; the field-level shape is the
typed record `Aer.Flow.Domain.ReviewVerdict` itself.

Rejected shapes, and why:

- **Keep verdicts as prose; let tools parse.** The measured status quo. Parsing prose with regex
  worked until the first janitor pass rewrote a status sentence to satisfy a checker — prose is
  editable in ways that change meaning silently; a schema violation is loud at the source.
- **Validate with `OutputCondition` alone.** The condition language is pointer-equals-scalar by
  §4.1's own "deliberately tiny" rule; it can require `/status == "done"` but cannot say "findings
  is an array of {severity, claim, status}". Extending the condition DSL to express that is the
  exact de-facto-DSL growth §4.1's exclusions forbid.
- **A generic JSON-Schema file per output.** Maximally flexible, but the engine then executes
  worker-authored validation programs — schema-of-the-week drift, a new dependency, and the
  closed-set guarantee (the engine knows every shape it enforces, and each shape has one typed
  reader) is gone. A closed enum grows by one deliberate PR per shape, which is a feature.
- **Engine routes on severity (auto-fail a step whose verdict has a High finding).** The tempting
  half-step, and the one 0038 exists to forbid: severity is the reviewer's judgment, judged in turn
  by a person. The moment the scheduler reads it, workers write for the scheduler — Goodhart in one
  move — and "Flow carries discipline, Workers carry intelligence" (Architecture Rule 1) is dead.
  A workflow that wants a machine gate declares an `OutputCondition` on a scalar the worker
  computes (§10.1's pattern); the structured findings stay for the human.

## Rests on

| fact | how we know | if false |
|---|---|---|
| Review verdicts were parsed as prose by regex in practice | **measured** — six review lanes' orchestration on 2026-07-29/30, register field note 4 on [#665](https://github.com/aer-works/aer-flow/issues/665) | there is no consumer pain and this is speculative structure |
| Prose claims are silently rewritable by downstream automation | **measured** — a janitor worker rewrote a status sentence to green a checker (field note 12 on [#665](https://github.com/aer-works/aer-flow/issues/665), hardened in #764) | the prose-verdict status quo was durable and the loudness argument weakens |
| STJ binds an absent required constructor parameter to null instead of throwing | **measured** — `ReviewVerdictSchemaTests` pins the missing-`findings` document to a refusal with the shape-floor message | `TryParse`'s hand-written null checks are dead code and the parser can be simplified |
| A schema'd output failing parse classifies as `ExecutionFailed` on clean exit 0 | **measured** — `ContractValidatorTests`' schema arms, red-proven against the pre-change validator | the contract check is decorative and a malformed verdict reaches its consumers |

## Consequences

**Easier.** The dispatch loop and every future surface read one typed document instead of grepping
markdown; a review step that produced garbage fails at the step, with the parser's sentence in the
failure reason, instead of downstream; the UI half (the decision surface rendering findings) builds
on a type, not a convention.

**Harder.** Review workers must now write valid JSON or fail — a worker that writes brilliant prose
and broken JSON is a failed execution on a clean exit, which is new strictness at the exact place
the old looseness was the defect. The enum is engine-versioned: a new shape is a PR, not a config
entry.

**Obliges us to.** Keep validation parse-only — any future contributor threading a
`ReviewFindingSeverity` into readiness, retry, or routing is crossing 0038's line, and the spec
§4.2 paragraph exists to be cited at them. Grow `OutputSchema` only by deliberate addition with a
typed reader per member.

Relates: spec §4.2 (mechanics, recorded once),
[0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) (the boundary this applies), spec §4.1
(the condition language this deliberately does not extend),
[#732](https://github.com/aer-works/aer-flow/issues/732) (scope; the UI half is filed separately).
