# 0028 — Visual rank is a decision: no permissive control is ever the default

Status: accepted
Date: 2026-07-25
Amends: [0006](0006-visual-direction-quiet.md)

## Context

[0022](0022-permission-ladder-and-denial-is-an-answer.md) exists because of one sentence in the design
corpus:

> The design problem is not showing the prompt — it is **stopping the prompt from becoming a reflex**.

The artifact that argues this **draws the opposite.** From
`docs/design/mockups/04-workers-commands-control.html`:

```html
<span class="btn pri">Allow once</span>   <span class="btn dgr">Deny</span>
```

```css
.pri{background:var(--accent);color:var(--on-accent);border-color:var(--accent)}
.dgr{border-color:var(--fail);color:var(--fail)}
```

`Allow` is the accent-filled solid. `Deny` is an outline. On a permission prompt, the affirmative
control is the loudest thing on screen — which is precisely the layout that trains the reflex the prose
forbids, one element away from the safety rule the whole record is built on.

It is not isolated. The same inversion runs through the corpus wherever a primary class was applied by
habit:

| Drawn as primary | Drawn as secondary | Why it is backwards |
|---|---|---|
| `Allow once` | `Deny` | trains click-through on the one control that must not be reflexive |
| `Apply` | `Ask someone…` | the product's own designated centrepiece ([0019](0019-consulting-is-not-deciding.md)) drawn third |
| `Try again` | `Ask claude to fix it` | the corpus itself calls the second *"the interesting affordance… the most common next action"* |
| `Summarise now` | `Leave it` | [0027](0027-context-is-per-worker.md) makes this a choice; a default answers it for you |
| `Add to room memory` | `Edit… / No` | [0016](0016-memory-is-room-owned.md) requires additions be *accepted, never inferred* |

**Why it survived every review.** `docs/design/coverage-audit.md` deliberately dropped *"the
pixel-level styling of the mockups"* while claiming to keep them *"for layout and state"*. Button rank
**is** layout and state — but it lives in a CSS class, not in prose, so a review that reads the words
and a coverage check that greps the text both pass it clean. Nothing in the repo said which control
should be loudest, so nothing was contradicted.

That is the general shape worth recording: **a design decision expressed only in pixels is invisible to
every control this project has.**

## Decision

**Visual rank is a decision that must be stated, and no permissive control is ever the visual
default.**

**1. On any control that grants, applies, overwrites or dismisses a safeguard, the permissive option is
never the primary.** Concretely: not accent-filled, not the sole solid among outlines, not focused on
open, not activated by `Enter` or `Space`
([0022](0022-permission-ladder-and-denial-is-an-answer.md) §4 already forbids the keyboard half; this
is the same rule for the eye).

**2. Neither is the destructive option.** This is not "make Deny loud." A prompt where refusing is the
shouting one trains a different reflex and makes the safety surface feel punitive, which is how people
turn it off. **On a genuine either/or, the two options carry equal weight** and the person chooses.

**3. Where one option genuinely deserves prominence, it is the one that keeps options open.** *Ask
someone* over *Apply*; *ask the worker that failed* over *retry blindly*; *leave it* over *summarise
now*. This follows from [0019](0019-consulting-is-not-deciding.md): if consulting is a first-class move,
it cannot be drawn as the afterthought.

**4. A mockup is not exempt.** Where a drawn artifact and a record disagree about rank, the record
wins — the same precedence `docs/design/README.md` already sets for words. Rank is now something a
record can be about.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The mockup draws `Allow once` with the primary-accent class while `Deny` carries the danger class | **measured** — `docs/design/mockups/04-workers-commands-control.html`, read directly | the contradiction this record was written to correct is not present, and it is fixing nothing |
| Visual rank is expressible in the token system rather than needing a per-control override | **measured** — `design/tokens.json` is the canonical set (#489), and `DesignTokenDriftTests` fails when generated tokens drift from it | the rule is stated but unenforceable in the shipped UI, and needs a mechanism rather than a record |

## Consequences

**Easier.** The Quiet direction gets its missing half. [0006](0006-visual-direction-quiet.md) settled
that *colour* carries status and not decoration; this settles that *emphasis* carries consequence and
not convention. Together they answer "how loud should this be?" without re-litigating per screen — the
same thing the ten states do for state.

**Harder.** It runs against a real convention. Every UI toolkit ships a primary-button style, every
designer reaches for it, and a dialog with two equal-weight buttons looks unfinished until you know
why. Expect this rule to be re-broken by habit rather than by argument, which is why it is written down
rather than assumed. It also has no automated gate — token drift is checkable, emphasis is not — so
this is honestly a review rule, and [`docs/plan.md`](../plan.md) is right that *a recorded lesson is not
a control.* The nearest thing to teeth is that a permission surface's rank is now a named thing a
reviewer can point at.

**Corrected 2026-08-12 (#1124).** "No automated gate" is no longer true. §2's most mechanical
failure shape — a permissive control carrying the primary visual marker while a sibling deny/cancel
goes bare — is now checked by `pixi run audit-permissionrank`
(`tools/audit-completeness/permissionrank.py`), a gate member since the same PR. It is a heuristic
with its scope disclosed in its own docstring: it sees file-local markup pairings (`Classes="accent"`
/ `Classes.accent=` in AXAML, `FilledButton`/`ElevatedButton` in Dart) and cannot see cross-file
pairings, code-behind styling, or wrapped components. So emphasis-as-judgment stays a review rule;
emphasis-as-markup is now a control.

**Obliges us to** state the intended rank when specifying any surface that grants or destroys; keep the
permissive option out of the primary slot, off the focus ring, and off `Enter`; give genuine either/or
choices equal weight; and treat a mockup's emphasis as a claim to be checked against the records rather
than as neutral presentation.

**Relates to** [0006](0006-visual-direction-quiet.md), which this amends —
0006 governs colour and status legibility, this governs emphasis.
[0022](0022-permission-ladder-and-denial-is-an-answer.md) is the surface it most protects, and
[0019](0019-consulting-is-not-deciding.md) is the affordance it most often rescues from third place.

Related: `#481` (`y`/`n` never on `Enter` — the keyboard half of the same rule), `#497` (the ladder),
`#482` (a failure offers the fix).
