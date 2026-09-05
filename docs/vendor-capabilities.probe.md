| | `claude` 2.1.258 (Claude Code) *(carried, not re-probed)* | `codex` codex-cli 0.153.2 *(carried, not re-probed)* | `agy` 1.1.27 |
|---|---|---|---|
| plan usage & reset | **`/usage` — 2% used, with reset times** | **`account/rateLimits/read` — 13% used, resets 2026-09-11T16:37:48.0000000+00:00, 0% used, resets 2026-09-05T01:52:17.0000000+00:00, 0% used, resets 2026-09-11T20:52:17.0000000+00:00** | not found on: --help, subcommand list, in-session slash command |
| per-turn cost | **`total_cost_usd` in every result event** | not found on: structured output stream | not found on: structured output stream, --help |
| structured output | **`--output-format stream-json --verbose`** | **`codex exec --json` JSONL** | **`--output-format stream-json`** |
| --permission-prompt-tool | **`--permission-prompt-tool <mcp-tool>` — consulted for permission decisions** | not found on: exec --help | not found on: --help, stderr, flag acceptance vs. a control flag, structured output stream |
| effort | `--effort` — low, medium, high, xhigh, max *(inspected, not run)* | **model-specific reasoning efforts from `model/list`** | `--effort` — low|medium|high *(inspected, not run)* |
| extra directories | `--add-dir` *(inspected, not run)* | `codex exec --add-dir` *(inspected, not run)* | `--add-dir` *(inspected, not run)* |
| subscription authentication | — | **`codex login status` — ChatGPT subscription** | — |
| resume & per-turn usage | — | **`codex exec resume` — same thread id, usage on both turns** | — |
| models | — | **visible models from `model/list`** | — |

Every cell above is one of three things, and the difference matters: **observed** (a run
demonstrated it), *inspected* (read from help or the binary, never executed), or **not found
on** an explicit list of surfaces. A bare "absent" is not expressible — that is the whole
point, because every wrong row this suite was built after was a negative from one surface.

- **claude · effort** — Read from help: "--effort <level> Effort level for the current session (low, medium, high, xhigh, max)". Help names the accepted values, but naming is not behaviour: 0023 declines to assert a mapping until each value is shown to be accepted AND to behave distinctly.
- **claude · extra directories** — Read from help. On `agy` this is load-bearing rather than optional: `-p` ignores the process working directory entirely (#491), so the room's folder must be bound explicitly.
- **codex · per-turn cost** — No dollar-denominated cost field was found in the `codex exec --json` stream. The completed turn did carry per-turn token usage, so the native evidence is token-denominated; any API-equivalent cost requires a versioned external price table.
- **codex · --permission-prompt-tool** — Codex exposes no `--permission-prompt-tool` equivalent on the inspected surfaces. Its distinct control surface is `codex exec --sandbox` plus approval-policy configuration; that is not represented as an equivalent callback.
- **codex · extra directories** — Read from `codex exec --help`; the adapter must still verify that managed host policy honors the requested writable roots.
- **agy · plan usage & reset** — `--help` carries no usage/quota flag, no such subcommand exists, and `agy -p "/usage"` produced no percentage — the model answered conversationally rather than the CLI reporting. Help mentioned 'usage' 1 time(s), all of them the synopsis line.
- **agy · per-turn cost** — No `total_cost_usd` in a `stream-json` run, and no cost flag in `--help`. The run streamed a `result` event carrying per-turn **token** usage (the `usage` object), but no dollar cost field — token-denominated, not dollars.
- **agy · --permission-prompt-tool** — **Undocumented in `--help`** — which is why help text alone was never enough. Rejected: exit 2, "flags provided but not defined: -permission-prompt-tool". The control flag exits 2, so this CLI does discriminate — the rejection is real.
- **agy · effort** — Read from help: "--effort Reasoning effort for the current CLI session (low|medium|high)". Help names the accepted values, but naming is not behaviour: 0023 declines to assert a mapping until each value is shown to be accepted AND to behave distinctly.
- **agy · extra directories** — Read from help. On `agy` this is load-bearing rather than optional: `-p` ignores the process working directory entirely (#491), so the room's folder must be bound explicitly.
