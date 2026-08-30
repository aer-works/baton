# 0029 — The gate is three mechanisms with three populations, not one (amends 0015)

Status: accepted
Date: 2026-07-25

[0015](0015-three-kinds-of-needs-you.md)'s three *kinds* of pause — permission, decision, approval —
are unchanged and were not in question. What this record replaces is its **mechanism** guidance:
*"prefer `--permission-prompt-tool` on `claude`, and keep the elected-tool path for `agy`."*

That sentence crowns one mechanism. The verification pass behind #527 measured that no single
mechanism covers the gate, and that the three available ones protect **different populations of
tools**. A design that names only one ships with a hole in whichever population that one does not
cover — and the hole is invisible, because a gate that is configured, running, and never consulted
looks exactly like a gate that works.

## Context

Four measurements force this, all re-runnable via `pixi run vendor-verify`:

**1. Tool restriction is not a capability boundary.** `--allowedTools` *pre-approves*; it does not
restrict the toolset (`gate.allowedtools-is-preapproval-not-ceiling`, live defect
[#529](https://github.com/aer-works/aer-flow/issues/529)). A model denied `Write` reaches for `Bash`
and writes the file. So **an MCP gate bounds nothing the model can reach another way**: a
purpose-built `aer_approve_deploy` tool cannot be faked, but "do not write `prod.yaml`" can — with
a shell redirect. Gating an MCP tool and gating a *capability* are different acts.

**2. A hook's `ask` survives `auto` mode, and the MCP callback does not.** 0015 already records that
`--permission-mode auto` silently disables `--permission-prompt-tool` — zero `tools/call`, no error,
no warning. It then concluded that if the operator's own settings enable `auto`, AER must treat its
permission surface as *absent*. That conclusion is now too pessimistic: a `PreToolUse` hook
returning `permissionDecision: "ask"` forces a prompt even in `auto`
(`gate.hook-ask-in-auto`), and a hook exiting 2 blocks a tool even against an explicit allow rule
(`gate.hook-exit-2-beats-allow`). **The hook is the recovery path the record said did not exist.**

**3. Elicitation is uncircumventable on both vendors.** `elicitation/create` is in the MCP
specification — unlike `_meta["anthropic/requiresUserInteraction"]`, which is a vendor extension
absent from the protocol. Measured across every permission mode on `claude`
(`allowedTools`, `bypassPermissions`, `--dangerously-skip-permissions`) and on `agy`
(`--dangerously-skip-permissions`, `accept-edits`): the gated tool body never ran
(`gate.elicitation-capability`, `agy.elicitation-capability`).

Portability here is **measured, not inferred.** The neighbouring mechanism falsifies the inference:
`force_ask` survives `--dangerously-skip-permissions` on `claude` and collapses on `agy`
(`agy.force-ask-defeated-by-skip`). Vendors disagreeing about what a bypass flag bypasses is this
audit's norm.

**4. But elicitation is a refusal, not a channel to a person.** Every arm answered `cancel` —
headless there is no human, and the client says no on their behalf. This is the single most
mis-readable finding in the audit, so it is stated flatly: **elicitation headless is a fail-closed
deny.** It cannot hold a worker while somebody decides.

## Decision

**The gate is three mechanisms. Each is named by the population it covers, and none substitutes for
another.**

| mechanism | covers | property | fails when |
|---|---|---|---|
| **`PreToolUse` hook** | **vendor tools** — `Bash`, `Write`, `Edit`, everything the model reaches without MCP | the only enforcement point over the toolset a worker actually has; `ask` survives `auto`, exit-2 beats an allow rule | **silently, and in two ways**: not loaded (see the discovery constraint below), *or* loaded but its command cannot execute — the tool then runs and the CLI reports nothing (#530) |
| **Blocking `tools/call`** | **AER's own MCP tools** | the durable wait: AER declines to respond until its UI returns a human answer. The only mechanism that *holds* rather than refuses | reaped mid-wait without a `timeout` floor or progress notifications |
| **`elicitation` (+ `requiresUserInteraction` on claude)** | **AER's own MCP tools** | uncircumventable refusal — no permission mode on either vendor approves it | always, headless: it denies rather than asks |

**Today the durable gate is the blocking `tools/call`, and only that.** Form-mode elicitation and
`requiresUserInteraction` do not carry a pause across a human's absence; they guarantee that a tool
is *not silently approved*. Use them to make the refusal unbypassable, not to ask the question.

**That is now measured rather than reasoned (#531).** In a live run with a person at a real
terminal, the blocking call was held **162 seconds**, the operator answered out of band by opening
a URL in a browser, the server completed the call, and `agy` accepted the late result and reported
the tool executed successfully. The full loop — worker asks, call stays open, human answers
somewhere else entirely, worker resumes — works today, on the mechanism this record already chose.
Notably the elicitation played **no part**: it had been refused 162 seconds earlier.

**But build it to migrate, because the non-blocking gate is already standardized.**
[SEP-1036](https://modelcontextprotocol.io/community/seps/1036-url-mode-elicitation-for-secure-out-of-band-interactions)
(**Final**) adds `mode: "url"` elicitation: the server hands the client a URL, the human answers out
of band in a browser, and — the SEP states this outright as a design property — **the server does
not block**. Completion is reported by `notifications/elicitation/complete`, and
`URLElicitationRequiredError` (`-32042`) is the equivalent error form.

That is exactly the shape this design needs, and it dissolves the blocking gate's whole problem
class: no idle reaper, no `timeout` floor, no 200 s ceiling, because nothing is held open. It is
also what makes M28's own demonstration — quit the desktop app, answer on the phone, come back —
achievable rather than a race against an unknown timeout.

**The vendor split is spec-defined.** A bare `elicitation: {}` means form mode only, per the SEP's
backwards-compatibility clause. `claude` declares `{}`; `agy` declares `{'form': {}, 'url': {}}`.

**But declaring is not implementing, and on `agy` it is not implemented (#531).** A live run with a
person present measured `agy` 1.1.7 refusing **every** elicitation without surfacing it — form in
2.7 ms, url in 0.6 ms — in a session where that same person was answering agy's own permission
prompts. Sub-millisecond means no UI was ever attempted.

So the correction to this section is: **the better mechanism exists on no vendor today.** One
declares it and does not implement it; the other does not declare it. The migration this record
plans for is real and still right to build toward — SEP-1036 is Final and the shape is correct —
but it is blocked on a vendor shipping it, and no schedule should assume it.

A second-order note worth carrying: the model was told *"elicit_tool was refused (not approved)"*.
The client refused **on the user's behalf, without asking them**. Any AER surface that reports a
gate as "declined by the operator" must not confuse a vendor's auto-refusal with a person's answer.

**What follows for the design:** AER's gate must be able to answer a pending question **without the
originating tool call still being open**, because that is the shape both the URL-mode path and a
crash recovery require. Persisting at ask-time ([0015](0015-three-kinds-of-needs-you.md)) already
forces most of this; what this adds is that **releasing the call must be the normal path, not the
crash path**. A gate designed around holding the call open would have to be rebuilt to adopt
SEP-1036; a gate designed around a persisted question and a correlation id adopts it by adding a
transport.

**The hook is not optional, because it is the only mechanism covering vendor tools.** Per finding 1,
an MCP-only gate protects MCP tools. Any capability the model can reach through `Bash` is ungated
unless a hook gates it. AER must therefore ship a `PreToolUse` hook on every worker it spawns, not
only on workers whose flows declare a gate.

**Hook discovery constrains process launch.** Hooks load only from the process's own cwd `.claude/`,
with no parent-directory fallback, and `--add-dir` grants file access but loads **no** configuration
(`gate.add-dir-loads-no-config`). So **AER must control the worker's working directory or pass
`--settings` explicitly** — and with [#521](https://github.com/aer-works/aer-flow/issues/521)
(`--bare` disables hooks even via `--settings`) the viable combinations are narrow. On `agy` this is
sharper still: permission rules are global-only (`agy.permissions-are-global-only`), so a hook in
the workspace's `.agents/hooks.json` is the *only* way to gate an agy worker without writing to the
operator's own settings file — and it will not load without `--add-dir` pointed at the workspace
either, on either mechanism: an `agy` worker spawned without it has no gate loaded and says nothing
about it, the same shape as the constraint below.

**The MCP gate server must never be registered via project scope.** `claude` prompts for interactive
approval before loading a project-scoped `.mcp.json` server — a headless worker would start with
that server sitting at `⏸ Pending approval` forever, which is 0015's "configured, running, never
consulted" failure in its purest form. **AER must register its gate server with `--mcp-config` at
spawn time**, which loads without approval and touches no file the operator owns — never `.mcp.json`,
never the `user`-scope `~/.claude.json` entry either, since neither is per-spawn.

**Which makes the self-check below strictly more load-bearing on `agy` than on `claude`.** A broken
hook fails **open** on both (`gate.broken-hook-fails-open`, `agy.broken-hook-fails-open` — measured
separately, because `agy.force-ask-defeated-by-skip` is the same gate mechanism behaving in opposite
directions on the two vendors, and inferring one from the other is the mistake this audit keeps
finding). But on `claude` a dead hook still leaves the MCP callback and elicitation covering AER's
own tools. On `agy` it leaves **nothing**.

**What a dead hook costs on `claude` grew with [#649](https://github.com/aer-works/aer-flow/issues/649).**
The sentence above is about AER's *own* tools; the vendor's are covered by `--disallowedTools`, which
is where a withheld read, shell or network category still rides. Writes no longer do — they moved
onto this hook so it could allow the one write landing in `AER_OUTPUT_DIR` — so on `claude` a hook
that cannot start now means writes are ungated outright, where the flag previously caught them.
Nothing else changed vendor posture, and the direction of travel is the one this decision already
records: the `PreToolUse` hook is the only enforcement point over the toolset a worker actually has.
It is also why the per-spawn self-check is a prerequisite for that grant rather than a companion to
it.

*Scope note:* the failure is measured **silent** on `claude` only. On `agy` it is unmeasured — no
arm has produced a positive control for detecting agy's output about a hook, and agy's own hooks
documentation describes no channel that would carry one. The self-check does not rest on that: it is
required by `claude`'s measured silence, and AER runs one self-check per worker on either vendor.

**The gate must hold for a tree of unknown depth.** One level of subagent nesting runs with nothing
configured (`fanout.nesting-allowed-by-default` — the documentation claims the opposite), and a
subagent inherits the parent's permission mode and cannot be given a stricter one
(`fanout.parent-mode-covers-subagents`). AER must set
`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH` explicitly rather than trusting a default, and must never
assume a subagent is more constrained than the session that spawned it.

## Rests on

| fact | how we know | if false |
|---|---|---|
| `--allowedTools` pre-approves and does not restrict the toolset | **measured** — `pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling` (#529) | an MCP-only gate would suffice; the mandatory hook becomes optional and this record over-builds |
| A `PreToolUse` hook's `ask` forces a prompt in `auto` mode | **measured** — `--only gate.hook-ask-in-auto` | 0015's original pessimism was right: an operator's `auto` removes AER's permission surface entirely and AER must refuse to render one |
| A `PreToolUse` hook exiting 2 blocks a tool despite an allow rule | **measured** — `--only gate.hook-exit-2-beats-allow` | the hook is advisory, not an enforcement point; nothing covers vendor tools and the gate is MCP-only by necessity |
| `elicitation` is honoured and unbypassable on **both** vendors | **measured** — `--only gate.elicitation`, `--only agy.elicitation` | the portable refusal does not exist; the gate needs a per-vendor mechanism table and `requiresUserInteraction` is claude-only |
| A blocking `tools/call` survives long enough to be answered by a human | **measured to 200 s only** — the upper bound of the idle window is unknown | the durable gate has a ceiling shorter than a person's response time, and the pause must be persisted and the call released rather than held. *This is why the design releases the call by default rather than relying on the bound* |
| `agy` accepts and routes a SEP-1036 `mode: "url"` elicitation | **measured** — `--only agy.url-mode-elicitation` | the non-blocking migration path does not exist on any vendor today and the blocking call is the only option until one ships it |
| An interactive `agy` surfaces the URL to a person | **MEASURED FALSE 2026-07-25** (#531), with a human at a real terminal who was actively answering agy's *own* permission prompts in the same session. Both modes were refused before any UI could exist — form in **2.7 ms**, url in **0.6 ms** — and nothing was shown. Declared, not implemented | *this is the false case, and its consequence is the one predicted:* url-mode is accepted and routed but **is not a human channel**, so the non-blocking migration is blocked on the vendor rather than on AER |
| A blocking `tools/call` can be answered out of band and the client will accept the late result | **measured 2026-07-25** (#531) — the same human run. The call was held **162 s**, the operator opened the URL in a browser, the server completed, and `agy` reported the tool executed successfully | the durable gate does not survive a human's absence at all, and 0029's central mechanism claim fails |
| `claude` will gain `elicitation.url` | **assumed** — it declares form-only today; nothing commits it to adding url mode | the non-blocking gate stays agy-only and AER carries two gate transports indefinitely |
| Hooks load only from the process cwd `.claude/`, with no parent fallback | **measured** — `--only gate.add-dir-loads-no-config` | AER need not control the worker's cwd; the launch constraint above relaxes |
| One level of subagent nesting is permitted by default | **measured** — `--only fanout.nesting-allowed-by-default`, two independent runs | the vendor's documented default (off) holds and the explicit depth cap is belt-and-braces rather than required |
| A `PreToolUse` hook whose command **cannot execute** fails **open and silently** — the tool runs, and the CLI reports nothing | **measured on both vendors, separately** — `--only gate.broken-hook-fails-open`, `--only agy.broken-hook-fails-open` (#530). CRLF endings and a space in the path both survive on `claude`, so the vendor's documented Git Bash failure mode is *not* the cause | if it failed *closed*, the startup self-check below would be belt-and-braces instead of load-bearing, and a misconfigured worker would be safe rather than ungated |
| Two `CLAUDE_CONFIG_DIR` roots, each separately signed in, are usable at the same time | **measured 2026-07-25.** A fresh root was signed in interactively by the operator; **the pre-existing root kept `loggedIn: true`**, both roots then reported the same account, and two concurrent `-p` runs — one per root — both returned successfully with distinct session ids | per-worker config roots collapse to one, and worker isolation needs a different design. Not fatal: per-worker roots are an option 0029 lists, not something it requires |
| **`agy` has no equivalent, and worker identity there is host-global** | **measured 2026-07-25**, because assuming symmetry is this audit's most repeated mistake. `agy` documents no config-root variable and has **no `auth` subcommand at all** — so no per-worker root and no free readiness probe. A run with `HOME`, `USERPROFILE`, `LOCALAPPDATA` and `APPDATA` all redirected to empty directories **still authenticated**, creating a fresh `.gemini` tree it did not need. *Where* the credential lives was not investigated — Rule 4 puts that off-limits, and "can AER isolate identity" is already answered | nothing: this is the constraint, not a risk. Any design that hands agy workers distinct identities is unbuildable today, and per-worker roots must be described as a `claude`-only option |

## Consequences

**Easier.** "Is this gated?" becomes answerable per tool rather than per product: name the tool's
population, read the row. The mechanism that covers it either is or is not configured.

**Harder.** Three mechanisms mean three failure modes, and two of them fail *silently* — a hook that
never loaded and a callback disabled by `auto` both look exactly like a working gate. AER must
**verify its own gate at worker start** rather than assume configuration took effect: the discovery
control that made these measurements trustworthy is the same technique the product needs at runtime.

**This is measured, not precautionary (#530).** A hook whose command cannot execute — wrong path,
missing interpreter — lets the tool run on **both** vendors, and on `claude` the CLI says *nothing*:
no error, no warning, nothing in `--output-format json`. So the self-check is the only thing that
can detect a dead gate, and two properties fall out of *how* the failure presents:

- It must assert a **side effect the hook actually produced**, never that the settings file was
  written. The file is written in every failing arm.
- It must run **per worker spawn**, not once per configuration. The failure is a property of the
  process and its host — a path that does not resolve there — not of the config that looks fine.

Worth recording that the assumption this replaced named the **wrong cause**: CRLF line endings and
spaces in paths both survive. Normalising line endings would have felt like a fix and prevented
nothing.

**Obliges us to** ship a `PreToolUse` hook on every spawned worker; control the worker's cwd or pass
`--settings`; set the subagent depth cap explicitly; give every blocking MCP gate a `timeout` floor
or progress notifications; and never render a permission surface that AER has not confirmed can
fire — [0023](0023-effort-and-models-are-named-by-behaviour.md)'s disclosed-collapse rule applied to
the gate.

**Amends [0015](0015-three-kinds-of-needs-you.md)**; its three-kind split and its gate-durability
section stand unchanged. Does not touch [0004](0004-permission-scopes.md), which governs
pre-declared policy rather than runtime mechanism.

Related: #529 (tool restriction is not a boundary), #521 (`--bare` disables hooks), #527 (the audit),
#445 (the permission-request mechanism), #503 (fan-out limits).
