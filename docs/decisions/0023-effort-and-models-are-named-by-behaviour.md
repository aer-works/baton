# 0023 — Effort is named by behaviour and models are offered by purpose, never by a vendor's own string

Status: accepted
Date: 2026-07-24

## Context

[0017](0017-vendor-model-effort-are-three-choices.md) states the rule: vendor, model and effort are
three separate choices, effort named by behaviour and model by purpose, never a vendor's own flag
value. This record is the full reasoning and the empirical evidence behind that naming rule.

The design corpus states it plainly, in [`05-stress-test.md`](../design/05-stress-test.md):

> **Named by behaviour, not by mechanism.** Quick / standard / careful / exhaustive, **never a token
> budget or a vendor's flag name.** Vendors express this completely differently and rename it often;
> the person's question is always "how hard should it think about this."

and repeats it as a settled call in
[`04-workers-commands-control.md`](../design/04-workers-commands-control.md), alongside the same rule
for models: *"Purpose, not identifiers — models are offered as deep / balanced / fast. **Nobody
should need to know this month's model string.**"*

**This rests on a repo invariant, not on taste.** CLAUDE.md Architecture Rule 2 (Adapter Isolation)
requires vendor-specific quirks to be isolated inside `Aer.Adapters`, with the core layer
understanding only a single unified vocabulary. A chip rendering `xhigh` is exactly that quirk
reaching the UI: the surface would have to know which vendor is selected in order to know what the
word means.

The empirical picture, from `docs/vendor-capabilities.md` (`#472`):

| | `claude` 2.1.219 | `agy` 1.1.6 |
|---|---|---|
| Effort | `--effort low\|medium\|high\|xhigh\|max` | `--effort low\|medium\|high` |

Five levels against three. This is the strongest available argument *for* 0017's position — the
scales genuinely differ — and it is also why a fabricated-universal-scale objection does not carry:
whatever AER shows, it is already showing something that does not correspond 1:1 to both vendors. The
choice is between exposing two incompatible vendor vocabularies to the person, or owning one.

## Decision

**Effort and model are presented in AER's own vocabulary, in terms of behaviour and purpose. The
adapter maps that vocabulary onto whatever the vendor's CLI wants this month.**

**Effort is named by behaviour:** **quick · standard · careful · exhaustive.** Never a token budget,
never a vendor flag value, never a number.

**Models are offered by purpose:** **deep · balanced · fast.** The model's own name may be shown
alongside — the corpus's own picker draws *"Opus 4.8 · deep work"*, *"Haiku 4.5 · fast"* — but the
purpose is what the choice is *made on*, and nobody is ever required to know the identifier to pick
sensibly.

Three constraints on how this is implemented:

**1. The mapping lives in the adapter**, per Architecture Rule 2. `Aer.Flow`, `Aer.Ui` and
`Aer.Mobile` know only the canonical words. Adding a vendor, or a vendor renaming its flag, is an
adapter change and nothing else — which is the concrete payoff, given the corpus's observation that
vendors *"rename it often."*

**2. Where a vendor cannot express a level distinctly, say so rather than fake it.** A canonical
scale over unequal vendor scales means some levels may collapse on some vendors. A collapse that is
disclosed is honest; a collapse that is silent means two visibly different choices produce identical
runs, which is worse than either naming scheme. Disclosure belongs at the point of choosing, in the
same spirit as [0022](0022-permission-ladder-and-denial-is-an-answer.md)'s rule about advisory rungs.

**3. The mapping itself is measured and shipped.** `#472` observed the flag and its accepted value
list; `#572`/`#573` went further and confirmed each value is accepted in a real run with
distinguishable behaviour, and shipped the canonical mapping with `vendor-verify` sentinels guarding
it (`docs/vendor-capabilities.md`). `#498` is the remaining UI/adapter work that consumes it — not a
reopening of the mapping question.

**4. Effort × model is not a grid, and the available set is enumerated per model, never assumed.**
*Amended 2026-07-28 (`#510`).* The empirical table above compares the two `--effort` flags as though
each vendor had one effort control. **`agy` has two**, and the combinations its CLI accepts have
**holes** — `docs/vendor-capabilities.md` § "`agy models`" enumerates both, and is the canonical
record. A surface presenting effort × model as a matrix would offer combinations the vendor rejects.

This does not reopen the decision; naming by behaviour is what makes it survivable. It sharpens what
the adapter owes: the canonical word maps to *whatever that model actually accepts*, so **a level
unavailable on the chosen model is a collapse, and constraint 2 already governs it** — disclose it at
the point of choosing rather than silently substituting a neighbour. Enumerate from the vendor
(`agy models` is machine-readable; `claude` has no equivalent subcommand, so the two sets come from
different surfaces and both need re-establishing after a vendor self-update).

`docs/vendor-capabilities.md` § "`agy models`" is the canonical record of what is measured here and
what is still open — including which of the two controls wins when both are given, which is not yet
known. Nothing in AER's vocabulary depends on that answer; a *surface offering both controls* would,
which is why the question is tracked rather than closed.

**Also corrected:** `agy` serves Anthropic and OpenAI models too, so "the Gemini worker" is the wrong
mental model for it, and any UI copy saying so is wrong.

## Consequences

**Easier.** The person asks one question — *how hard should this think?* — and gets one answer, in the
same words, whichever vendor is on the chip. Two workers from different vendors become comparable at a
glance, which is the whole point of putting them in one room. A template can name *"draft on a deep
model at careful, review on a fast one at quick"* and remain valid across a vendor's renames.

**Harder.** AER now owns a vocabulary and must defend it. Four effort levels and three model purposes
have to be defensible for every vendor added later, and the first vendor with a genuinely
two-level dial will make the mapping lossy in a visible way. There is a real loss of fidelity for the
expert who *does* know that `xhigh` differs from `max` and wants precisely one of them — the corpus's
position is that this person is rare and the confused newcomer is not, and this record takes that
trade knowingly. A per-worker escape hatch exposing the raw value is a possible later concession, but
it is **not** decided here, and it must not be the default surface.

**Obliges us to** keep vendor effort/model strings out of `Aer.Flow`, `Aer.Ui` and `Aer.Mobile`
entirely; map in the adapter; probe each vendor's effort values for real acceptance and distinct
behaviour before fixing the mapping; disclose a collapse where two canonical levels resolve to one
vendor level; and show a model's purpose as the basis of choice, with its identifier available but
never required.

**Relates to** [0017](0017-vendor-model-effort-are-three-choices.md), whose three-axis model this
gives its naming reasoning to. [0012](0012-what-aer-flow-is.md) is what it serves — multi-model must
not become a tax on the simple case, and a person forced to learn two vendors' effort vocabularies is
paying that tax before they have chosen anything.

Related: `#472`/`#572`/`#573` (the capability probe and the shipped mapping), `#391` (show which model
each agent is running), `#479` (spend against subscription limits — the other number a choice is made
on), `#498` (the remaining UI/adapter work consuming the shipped mapping).
