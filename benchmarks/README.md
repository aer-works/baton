# Benchmarks

Dated, immutable snapshots that inform routing decisions. Each lives in its own `YYYY-MM-DD`
directory with a README stating source, harness, and scope; a new capture gets a new directory,
never an edit to an old one.

| Snapshot | What it holds | Feeds |
|---|---|---|
| [`deepswe/2026-09-04`](deepswe/2026-09-04/README.md) | 36 vendor/model/effort configurations from the DeepSWE v1.1 selector: pass@1, API-cost proxy, output tokens, agent steps. | Tier pins (#1861, #1863) |
| [`subscription-usage/2026-09-04`](subscription-usage/2026-09-04/README.md) | Baton-launched versus native Claude Code sessions, 2026-08-31 to 09-04: responses, output, cache-read, implement-room outcomes. | #1848, #1849, #1391 |

## Derived scores

[`deepswe/derive_scores.py`](deepswe/derive_scores.py) writes `derived-scores.csv` beside a date
directory's raw file, leaving the raw capture untouched. It adds two plain ratios
(`quality_per_100_steps`, `quality_per_usd`, kept because people ask for them and labelled as ratios: on
their own they rank a bad cheap answer above a good dearer one), two Pareto flags on quality versus
steps (`on_frontier` across every vendor, `on_vendor_frontier` within one, the comparison that matters
when subscriptions do not trade against each other), and one composite, `utility_lambda_<L>` =
quality minus L times steps. L is the coefficient to argue about: it is a script argument, it is
written into the column header, and `--sweep` prints the top rows under several values so the argument
can be had with the table in front of you. `--check` fails if the committed derived file is stale.

At the default L of 0.10 (one quality point forfeited per ten agent steps) the 2026-09-04 order is Sol
max, Sol xhigh, Opus high, Sol high, Opus max, Opus medium. Raise L to 0.20 and Opus medium passes
Opus high; at 0.40 nothing over 60 steps survives the top five. The two Gemini 3.8 Flash rows lead on
raw quality and fall to tenth and eleventh once steps count at all.

## Reading rules the snapshots share

- Results are specific to their harness. They compare configurations inside that harness; they do
  not rank models universally.
- The API-cost proxy is not the subscription meter. Baton drives subscription-authenticated CLIs and
  nobody outside the vendor knows the meter's weighting.
- Steps, not output, are what drained the weekly allowance: every step re-reads the context. Compare
  routes on quality, steps, and output together, then look at cost.

## The cast

Opinion, not measurement. The operator's pronouns and one-line characters for talking about the
vendors and models in conversation. Everything in `spec/`, issues, PR bodies, and the vendor register
stays "it"; a name is not evidence of anything, least of all a pronoun.

| Who | Pronouns | Character |
|---|---|---|
| Claude (vendor) | they | A family, not a person. |
| Antigravity | it | Refuses effort flags it dislikes, ignores your working directory, walls you mid-sentence. No one home, and it wants you to know. |
| Codex | he | Shows up when you are out of quota, fixes two of your tickets, leaves a nine-page program document. Unsolicited but competent. |
| Fable | she | Conductor. Reads the room; still holds the grudge about the three lanes lost at 09:25. |
| Opus | he | Fifty-two steps, gets it right, does not hurry. The expensive dinner guest who is worth it. |
| Sonnet | they | Two hundred and sixty-eight steps to a 54. Working very hard, unclear on what. |
| Haiku | it | Low effort by design. Confirms the list it was handed and goes back to sleep. Respect. |
| Sol | she | Compact, 37 steps, no wasted motion. The one you would want on review if she were in the building. |
| Terra | he | Steep effort curve. Fine at max, sulks below it. |
| Luna | they | Cheap and persistent, 102 steps to almost get there. The night shift. |
| Gemini 3.8 Flash | she | New in town: 74% and 166 steps. Talented, exhausting. Auditioning. |
| Gemini 3.1 Pro | he | Twelve percent. We do not talk about him. |
