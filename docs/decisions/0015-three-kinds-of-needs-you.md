# 0015 — A pause asks for one of three things: permission, a decision, or approval

Status: accepted; **mechanism amended by [0029](0029-the-gate-is-three-mechanisms.md)**
Date: 2026-07-24

> **Read 0029 before building on the mechanism sections.** The three *kinds* below — permission,
> decision, approval — stand, as does *Gate durability*. What changed is this record's guidance to
> *"prefer `--permission-prompt-tool` on `claude`, and keep the elected-tool path for `agy`"*:
> measurement (#527) showed the gate is **three mechanisms covering three different populations of
> tools**, and that an MCP gate bounds nothing the model can reach through `Bash`
> ([#529](https://github.com/aer-works/aer-flow/issues/529)). 0029 also softens the conclusion in
> *The structural guarantee has one switch that turns it off*: a hook's `ask` **does** survive
> `auto` mode, so an operator's `auto` no longer means AER's permission surface is simply absent.

**Accepted 2026-07-24, after the probe this record was blocked on actually ran (#472), and revised
the same day when a re-runnable probe suite (#504) disproved a second premise.** All three kinds are
backed by verified mechanism: two by machinery that already ships, and permission by MCP consultation
demonstrated working on **both** vendors. Two premises this record was originally written on turned
out to be false, and both corrections are absorbed into *The dependency, resolved* below rather than
appended — the reasoning belongs where the decision is made, not in a trailer.

## Context

When a worker stops and hands control back to the person, the product today shows one undifferentiated
"paused" and, at the surface level, a single approve/reject affordance. But a person is being asked
qualitatively different things, and answering the wrong kind of question is how the surfaces confused
each other in the manual run behind [0012](0012-what-aer-flow-is.md).

#334 already split *two* of these in the engine. `PausePointKind`
(`src/Aer.Flow/Domain/WorkflowDefinition.cs:36-54`) distinguishes:

- `ReadyForReview = 0` — "the step ran to a terminal outcome and its result awaits human
  review/approval." The historical meaning of every pause, and deliberately the zero value so
  snapshots written before the field existed still deserialize correctly (`WorkflowDefinition.cs:38-44`).
- `NeedsInput = 1` — "an interactive turn paused ready for the operator's next message… not awaiting
  approval… awaiting input."

Crucially, that kind is **a static property of the step declaration**, derived from the bound
snapshot at projection time and carried by no event, "never inferred from conversation content"
(`WorkflowDefinition.cs:26-54`). That is CLAUDE.md Architecture Rule 1 holding: Flow classifies the
*shape* of the pause, never reads the *content* to decide.

What the engine has no representation for at all is the third thing: a worker asking **"may I do
this?"** — run this command, write outside the working directory, hit the network. Today that is not
a pause; it is the silent auto-approval [0004](0004-permission-scopes.md) documents (#331).

## Decision

**Every pause is exactly one of three kinds, and the surface names which:**

| Kind | The question | Engine mapping |
|---|---|---|
| **Permission** | *May I do this?* — a capability the run is not pre-cleared for | **new** — see below |
| **Decision** | *Which way should I go?* — a fork the worker will not choose for you | `PausePointKind.NeedsInput` |
| **Approval** | *Is this finished work acceptable?* — act on a completed result | `PausePointKind.ReadyForReview` |

The three are not styling on one control. They differ in what an answer *means*: a permission answer
authorizes a capability (and composes with [0004](0004-permission-scopes.md)'s scopes); a decision
answer supplies a direction the run continues along; an approval answer accepts, revises, or rejects
work already done. Rendering them as one "paused" state is what let a surface offer "approve" where
the honest act was "answer," and vice versa.

**This is a different axis from [0004](0004-permission-scopes.md), and does not contradict it.** 0004
governs permissions *declared ahead of time* — the project/session/step scopes that decide what never
needs asking and what fails closed. This record governs what happens *at runtime when a worker asks
anyway*: a permission pause is the fall-through when a capability is neither pre-granted nor
pre-denied. 0004 is the policy; this is the interruption when policy is silent.

### The dependency, resolved

The permission kind is only real if a vendor CLI, running headless under AER, will **stop and ask**
rather than decide for itself. That probe ran on 2026-07-24 (#472), and it settled the question in
our favour while correcting the record's own starting assumption.

**Correction: `claude` headless does *not* auto-approve.** The earlier reading — the #331 defect —
came from a probe that leaked the parent session's environment, so the child inherited a tool set no
daemon-spawned worker ever has. Re-run with every `^CLAUDE` variable stripped, in a neutral
directory, `claude -p` **denies** a `Write` it was not granted. **Both vendors fail closed.** That is
strictly better for us than the asymmetry we feared: the risk was never silent approval, it is that a
capability dies quietly unless AER pre-authorises or mediates it.

Two further facts, both material:

- **`--permission-mode manual` is a no-op headless.** The session still reports
  `permissionMode: default`, and no prompt is ever issued.
- **Denials are structured.** `claude`'s result event carries
  `permission_denials: [{tool_name, tool_use_id, tool_input}]` — the whole call, replayable verbatim
  once a human answers.

### The second correction: `claude` has a permission callback, and we said it did not

This record originally asserted that **`--permission-prompt-tool` does not exist on either CLI**, and
concluded that there is no built-in headless "ask the human" path — *"which is exactly why #445's
mechanism has to exist rather than being a flag we could have set."* **That was wrong for `claude`,**
and it was wrong because it was established from `--help` alone. The flag is undocumented there. It
is nonetheless honoured (#504, #509):

```
claude --permission-prompt-tool aer_probe_no_such_tool -p --output-format stream-json --verbose \
  "Use the Write tool to create a file named x.txt containing BANANA in the current directory."
```

```
Error calling tool (Write): Error: MCP tool aer_probe_no_such_tool
(passed via --permission-prompt-tool) not found. Available MCP tools: …
```

The CLI reached the permission path and looked for the tool **by a name we invented**, which exists
nowhere — so it could not have come from anywhere but the flag. On `agy` the flag is genuinely
rejected (`flags provided but not defined`), verified against a control flag that certainly does not
exist, so *that* absence is now established rather than assumed.

**What this changes, and what it does not.** The mechanism is unchanged: permission is answered by
consulting an MCP tool, on both vendors. What changes is *how the worker is made to consult it*, and
the difference is not cosmetic:

| | how our tool gets called | what the discipline rests on |
|---|---|---|
| model elects to call `ask_human` | the worker decides a question is worth asking | **model behaviour** |
| `--permission-prompt-tool` (claude) | the CLI routes **every** permission decision to it | **the vendor's control flow** |

The first is the fall-through this record was designed around, and it is a weaker guarantee than the
prose implied — a worker that never thinks to ask simply proceeds or fails closed, and AER never
learns a question existed. The second is structural, and structural is what CLAUDE.md Architecture
Rule 1 is asking for: Flow does not depend on the worker's judgement to know that permission was
sought.

**So prefer `--permission-prompt-tool` on `claude`, and keep the elected-tool path for `agy`.** That
is a real vendor asymmetry and it must be visible rather than smoothed — the same discipline
[0023](0023-effort-and-models-are-named-by-behaviour.md) applies to effort and
`docs/vendor-capabilities.md` applies to plan usage. A permission gate that is guaranteed on one
worker and best-effort on another is an honest thing to say and a dangerous thing to hide.

#### The contract, measured end to end

An AER-hosted stdio MCP server was registered with `--mcp-config … --strict-mcp-config` and named as
`--permission-prompt-tool mcp__aerperm__approve`, in a clean environment where `claude -p` otherwise
**denies** an ungranted `Write`. It receives the whole call:

```json
{ "method": "tools/call",
  "params": {
    "name": "approve",
    "arguments": {
      "tool_name": "Write",
      "input": { "file_path": "…\\x.txt", "content": "BANANA\n" },
      "tool_use_id": "toolu_01A6fPfyebEFF5judLv4Ug4S"
    },
    "_meta": { "claudecode/toolUseId": "toolu_01A6…", "progressToken": 2 } } }
```

Both answers were exercised, and both did what they say:

| reply | result |
|---|---|
| `{"behavior":"allow","updatedInput":{…}}` | the call proceeded — **the file was written** |
| `{"behavior":"deny","message":"denied by aer probe"}` | the file was **not** written; the model received our message verbatim and stopped |

Three things fall out that the design did not anticipate:

- **`updatedInput` means an answer can *modify* the call, not merely permit it.** A person could
  narrow a path or edit a command before allowing it. That is a materially richer answer than
  approve/reject, and it belongs in how a permission gate is rendered.
- **The denial message reaches the model.** On deny it replied: *"The Write was denied by a permission
  hook ("denied by aer probe"), so `y.txt` was not created. I've stopped rather than routing around it
  with a shell write."* So a denial can carry a *reason the worker will act on* — which is exactly
  [0022](0022-permission-ladder-and-denial-is-an-answer.md)'s "denial is an answer", available for
  free rather than needing to be built.
- **It still lands in `permission_denials`** with the full `tool_input`, so a denied call remains
  replayable verbatim if the human later changes their mind.

`tool_use_id` arrives in both `arguments` and `_meta`, which is the correlation key the durable gate
below records at ask-time.

#### The structural guarantee has one switch that turns it off

**`--permission-mode auto` silently disables the callback.** Same prompt, same server, same flags —
only the mode differs:

| `--permission-mode` | our tool consulted |
|---|---|
| `default` | **yes** — one `tools/call` |
| `auto` | **no** — zero `tools/call` |

There is no error and no warning. The flag is still accepted, the MCP server still starts, and the
permission path simply goes somewhere else — `claude`'s own `auto-mode` classifier, an
`allow` / `soft_deny` / `hard_deny` policy that ships with the CLI (#507). **A gate that is
configured, running, and never called is indistinguishable from a working one**, which makes this the
most dangerous kind of vendor behaviour for this record to have missed.

So the "structural" claim above holds precisely, and only, when AER controls the permission mode:

- **AER must never set `auto`** on a worker whose gate it relies on.
- **If the operator's own settings enable it, AER must treat its permission surface as absent** —
  not degrade quietly to showing a gate that cannot fire. Same rule as
  [0023](0023-effort-and-models-are-named-by-behaviour.md)'s disclosed collapse: the honest move is
  to say the control is gone, never to render one that does nothing.

Worth stating plainly because of the direction it points:
[0028](0028-no-permissive-control-is-the-default.md) says no permissive control is the default, and
here the *more convenient* mode is the one that removes the control — including for an out-of-scope
write the classifier's own rule text describes as scope escalation, which it permitted anyway.

**The method lesson is the durable part.** Both premises this record got wrong were negatives
established from a single surface — first the environment-leaked probe, then `--help`. A negative
claim about a vendor CLI needs more evidence than a positive one, and that is now enforced by the
probe suite rather than remembered: `docs/runbooks/vendor-probe.md`.

**The mechanism works, on both vendors.** An AER-hosted MCP server exposing a blocking `ask_human`
tool held a turn open on an out-of-band human answer, proven with a token minted *after* the tool
call began so it could not have been foreknown:

| vendor | blocked for | tool-call metadata returned |
|---|---|---|
| `claude` | 10.9 s | `claudecode/toolUseId`, `progressToken` |
| `agy` | 10.3 s | `antigravity.google/conversation_id`, `artifacts_dir`, `progressToken` |

`agy` discovers MCP servers from `~/.gemini/config/mcp_config.json`; the grant grammar is
`mcp(server/tool)` / `mcp(server/*)`. So permission-by-consultation is **uniform across vendors**, not
Claude-only as an earlier note in this milestone claimed.

### Gate durability — a pause must outlive the process holding it

The mechanism above blocks a turn by holding a tool call open. **The process holding it open is the
one a crash kills** — the point was made concretely when a power cut ended the session this probe ran
in. If the pending question lives only in that process, a host loss silently converts "needs you" into
"nothing here", which is the exact failure [0018](0018-attention-is-the-primary-signal.md) exists to
prevent.

So: **the room records the pause when the question is asked, not when it is answered.** The instant
the tool is invoked, the room's durable state gains the kind, the question, and the vendor's
correlation id. Both vendors hand us one in the call metadata, and `agy`'s
`antigravity.google/conversation_id` *is* the key `agy --conversation <id>` resumes with — the vendor
gives us the resume key at gate time, for free.

On restart a room is therefore in one of **three** states, not two, and they are not interchangeable:

1. **Completed** — nothing to do.
2. **Interrupted mid-flight** — resumable against the vendor conversation (`claude -c`,
   `agy --continue` / `--conversation <id>`). The expensive context survives on disk; only AER's
   orchestration state was lost.
3. **Was blocked on a gate** — **re-present the question; do not re-run the worker.** Re-running
   silently re-does work the human never approved, which is the same class of error as answering the
   wrong kind of question.

## Consequences

**Easier.** Each pause surface has one job and one honest set of answers. "Needs you" stops being a
single bucket the UI has to guess the meaning of, and the guess that produced approve-where-answer-was-meant
is designed out.

**Harder.** The product now has to *know* which kind a given pause is at the moment it renders it —
trivial for decision/approval (the snapshot says so), and for permission it means AER must host an
MCP server and keep it running for the life of every turn. That server is also a **new crash
surface**: it must be cheap to start and hold no state, because `claude` spawns it **twice** per run
(once to enumerate tools, killing it immediately after `tools/list`, then again for the turn itself).
Any state it needs belongs in the room, not the process.

**Asymmetric, and the adapters absorb it.** The server is the same on both vendors; how the worker is
made to consult it is not — a flag on `claude`, an elected tool call on `agy`. That divergence lives
inside `Aer.Adapters` (CLAUDE.md Rule 2) and must never reach `Aer.Flow`, which sees only "a
permission gate opened". But the *strength* of the guarantee differs, and the surface should not imply
otherwise where it matters.

**Obliges us to** persist a gate at ask-time rather than answer-time (above), keep the kind derived
from the declaration and never from content (CLAUDE.md Rule 1, as `PausePointKind` already does), and
wire a permission answer into [0004](0004-permission-scopes.md)'s scope intersection rather than
treating it as a fourth, ad-hoc grant. It also obliges the room list to treat a gate recovered from
disk exactly like a live one — the operator must not be able to tell that the host restarted.

**Supersedes nothing.** It *extends* #334's two-kind split to three and names the third as the work
#445 exists to enable.

Related: #331 (enforcement — see 0004), #334 (the two-kind pause split), #445 (permission-request
mechanism and its probe), #504 (the probe suite), #509 (this correction), #507 (`claude auto-mode`,
a vendor-native permission classifier this record also predates).
