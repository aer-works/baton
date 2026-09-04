# DeepSWE selected-configuration snapshot — 2026-09-04

This is an immutable input snapshot for routing discussions, not a claim about
subscription entitlement or real wall-clock time. The raw observations live in
[`selected-configurations.csv`](selected-configurations.csv); keep future runs
in a new date directory rather than editing this one.

## Provenance

- Source: DeepSWE configuration selector, copied by the operator on 2026-09-04.
- Benchmark: DeepSWE v1.1 public coding benchmark. It uses a shared
  mini-swe-agent harness, so the results are useful for comparing these selected
  configurations *in that harness*, not for ranking raw models universally.
- Population: 36 selected vendor/model/effort configurations.
- `avg_api_cost_usd` is the source's API-equivalent estimate. Baton invokes
  subscription-authenticated CLIs, so it is deliberately not treated as a
  subscription-capacity meter.
- `output_tokens` and `agent_steps` are workload proxies. Neither records
  elapsed wall-clock time; test duration, tool waits, and provider scheduling
  still need to be captured by Baton.

## What this snapshot says for Baton routing

| Route / setting | Quality | API-cost proxy | Workload signal | Routing reading |
|---|---:|---:|---:|---|
| Gemini 3.8 Flash / high | 74% | $2.36 | 143k output / 166 steps | Best observed score at low proxy cost, but the highest non-Sonnet work intensity. A strong long-running worker, not a free fan-out default. |
| Opus 5 / high | 73% | $6.08 | 64k / 73 | Best Opus operating point here. `xhigh` holds score while adding 28% output and 22% steps; `max` adds one point over high for about 2x cost. |
| Fable 5 / high | 69% | $9.18 | 57k / 59 | Sensible conductor ceiling in this sample. `xhigh` buys one point; `max` buys none while roughly doubling cost and output. |
| Sol / high | 69% | $2.66 | 28k / 37 | The compact high-quality OpenAI route: it matches Fable high's score with about half the steps and output. |
| Terra / max | 70% | $3.96 | 72k / 76 | A steep effort curve. It reaches near-Sol quality, but Sol `xhigh` scores one point higher with 44 steps and 41k output. |
| Luna / max | 67% | $0.61 | 73k / 102 | Exceptional API-cost proxy, but not a speed/free-work winner: it requires 2.8x Sol-high's output and steps to get two points less score. Use it where cheap persistence is worth a long agent trajectory. |
| Gemini 3.7 Flash / medium | 65% | $2.03 | 94k / 117 | The preferred 3.7 setting in this sample: high has equal score with more output, steps, and cost. |
| Sonnet 5 / all observed efforts | 31–54% | $2.19–$26.40 | 36k–214k / 77–268 | The conspicuous caution case. More effort buys score, but with unusually large trajectories. Do not infer a general model verdict; investigate the harness/tool interaction before making this a Baton default. |
| Gemini 3.1 Pro / high | 12% | $2.14 | 28k / 76 | A configuration/harness anomaly for this benchmark, not a suitable default route without a direct Baton probe. |

## Practical default implications

1. Optimize a fleet on **quality, steps, and output together**, then consult
   API-equivalent cost as a secondary comparison. Subscription limits may track
   a different hidden measure.
2. Treat high-step routes as a concurrency risk. In this snapshot, 100 agent
   steps are roughly 2.8 Sol-high trajectories, 1.3 Terra-max trajectories, or
   one Luna-max trajectory; that makes them plausible contributors to the
   observed early weekly-limit exhaustion even when the source's dollar proxy
   is low.
3. A useful initial policy is: Sol high / Opus high for compact quality work;
   Fable high for conductors; Gemini 3.8 Flash high only for tasks that benefit
   from a large agent trajectory; Luna max for deliberately budget-oriented,
   non-urgent work. Validate these through Baton-ledger data before enforcing
   them.
4. The missing metric is `wall_clock_ms` per execution, with tool/runtime wait
   time where available. Issue #1849's append-only ledger should retain it with
   vendor/model/effort, room, work/PR identity, input/output tokens, and steps.

## Future collection contract

For a new DeepSWE selector capture, create
`benchmarks/deepswe/YYYY-MM-DD/selected-configurations.csv` with this same
schema and a sibling README that states source date, benchmark version/harness,
and any changes in the selected configuration set. Do not backfill missing
effort levels with interpolation.
