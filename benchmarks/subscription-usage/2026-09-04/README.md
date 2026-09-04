# Claude subscription-usage comparison — 2026-09-04 snapshot

## Question

Why did the Claude Max 20x weekly allowance run out about three days early even though Fable's
separate weekly allowance was only about 65% used? In particular: did Baton simply accomplish much
more work, or did its orchestration shape create avoidable token consumption compared with native
Claude Code subagents?

This is a preserved local-analysis snapshot, not a claim about Anthropic's private subscription
meter. The token fields below are the CLI's raw comparative evidence. Anthropic does not document a
formula mapping them to Max-plan allowance consumption.

## Scope and method

- Window: 2026-08-31 through 2026-09-04.
- Source: direct parsing of Claude session JSONL. The installed extension's `usage.db` had stopped
  ingesting new sessions after 2026-09-01, so it was not used as the population source.
- Populations: Baton-launched Claude SDK sessions versus native Claude Code subagent sessions.
- Models retained separately: Sonnet and Opus. Fable was the conductor population and is not folded
  into the worker comparison below.
- Measures: assistant responses, output tokens, and cache-read tokens. These describe different
  costs: response count approximates model/tool loops; output approximates generated work; cache read
  approximates repeatedly re-sent context.
- Outcome evidence: Baton's settled implement-room records, including success/failure/cancellation.

The original exploratory tables were produced interactively and were not saved. The aggregate
results and decision-relevant subsets were preserved in Baton issues #1848 and #1802 and are recorded
here. A future rerun should emit machine-readable per-session and per-day CSV alongside this file;
this snapshot does not invent missing daily rows from aggregates.

## Baton versus native Claude Code

| Model | Launch path | Sessions | Median assistant responses | Median output tokens | Median cache-read tokens |
|---|---|---:|---:|---:|---:|
| Sonnet | Baton | 464 | 103 | 63.2k | 9.6M |
| Sonnet | Native subagent | 32 | 52 | 15.0k | 3.4M |
| Opus | Baton | 101 | 88 | 111.2k | 10.2M |
| Opus | Native subagent | 51 | 66 | 22.9k | 5.6M |
| **All** | **Baton** | **565** | — | — | — |
| **All** | **Native subagent** | **83** | — | — | — |

Median Baton/native ratios:

| Model | Responses | Output tokens | Cache-read tokens |
|---|---:|---:|---:|
| Sonnet | 1.98x | 4.21x | 2.82x |
| Opus | 1.33x | 4.86x | 1.82x |

The response and output increases are consistent with Baton workers doing larger, longer autonomous
jobs than native subagents. They are not, by themselves, proof of waste. The much larger cache-read
population means each additional loop also reintroduced substantial context, however, so inefficient
looping becomes expensive quickly.

## Implement-room outcome distribution

Among 140 metered Sonnet `implement` rooms configured with a 1.2M billed-token / 610-step ceiling:

- 109 stayed below 250k billed tokens.
- 31 exceeded 250k.
- 6 exceeded 600k.
- None reached the 1.2M ceiling.
- 107 succeeded.
- Failed or cancelled rooms accounted for about 5.05M of 26.02M billed tokens (about 19.4%).

This argues against interpreting the event as one or two workers merely hitting an excessively high
per-run ceiling. The weekly exhaustion was a fleet-volume problem: many individually valid sessions,
some unusually long, accumulated concurrently. A blanket 250k cap would also be too crude because it
would stop 31 long rooms without asking whether they were converging or producing accepted work.

## Strongest identified waste mechanisms

### Duplicate in-lane reviewers

Before role-level subagent withholding, 8 of 24 implement rooms launched an `Agent` as a second
reader even though the Baton conductor already scheduled a separate review lane. Those eight rooms
accounted for about 1.24B of the night's 1.56B cache-read tokens—roughly 80% of the total.

After `implement` and `review` mechanically withheld Claude's `Agent`/`Task` tools, a later set of 25
rooms used about 0.59B cache-read tokens, recorded as roughly 2.6x less per room. That before/after
change is the clearest causal evidence of avoidable spend in the dataset.

Source for both figures: the conductor's read of the rooms' own `quota-ledger.jsonl` entries under
`~/.baton/rooms`, taken 2026-09-04 — the 24-room "before" set ran 2026-09-03 evening, the 25-room
"after" set 02:00–05:15 ET on 2026-09-04, immediately after #1811 landed the withholding. The room
ledgers are the durable record; no separate CSV was cut for this pair. A future rerun should emit one.

### Model-turn polling

One roughly ten-line change reached about 2.36M tokens because an in-lane background command was
polled repeatedly with `manage_task status`; each status check re-entered the model as another full
turn. Baton briefs should require foreground builds/tests and prevent model-mediated polling loops.

### Failed/cancelled work

The 5.05M billed tokens attached to failed/cancelled implement rooms are not automatically waste—some
may have produced useful diagnosis or partial work—but they are the first outcome class to inspect.
They should be broken down into quota walls, verification failures, duplicate attempts, cancellation,
and genuinely reusable artifacts rather than treated as one bucket.

## Interpretation

The evidence supports both explanations:

1. **Baton did more per session.** Median output was about 4.2–4.9x native subagent output, and median
   response counts were about 1.3–2.0x. A productive 1.2M run can be better value than five short runs.
2. **Baton also had material avoidable amplification.** Duplicate subagent reviews, repeated
   full-context polling turns, and failed/cancelled attempts consumed a large measurable share.

Therefore the right control is not simply a lower per-execution cap. Baton needs fleet-level runway,
per-attempt durable accounting, and continuation decisions tied to observable progress: committed
workspace delta, required artifact creation, targeted verification, or an explicit operator choice.

## Missing evidence for the next rerun

To separate "five times the work" from "five times the churn" more confidently, regenerate a
session-level dataset containing:

- day and start/end time;
- launch path, room, issue/PR, role, adapter, and exact model;
- assistant responses/tool steps and wall time;
- input, output, cache-read, cache-creation, and thinking tokens without zero-filling absent fields;
- retries/resumes and native child sessions linked to their parent;
- diff size, commits, required artifacts, gate result, PR opened/merged, and final outcome;
- accepted-work measures such as merged lines/tests/issue closure, reported separately from raw size.

The primary comparisons should then be tokens per accepted outcome, cache-read per useful model turn,
and failed/cancelled share over time—not API-equivalent dollars alone.

## Durable follow-ups

- #1802: withhold duplicate in-lane subagents (closed; before/after evidence above).
- #1848: fleet spend guard and progress-gated continuation.
- #1849: append-only room/fleet token ledger with versioned API-equivalent estimates.
- #1391: vendor-reported quota/runway projection, advisory in the first cut.
