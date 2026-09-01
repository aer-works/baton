# `baton dispatch` — the front door for driving a worker

`baton dispatch` runs **one worker role, one-shot**, against a vendor CLI that is already logged in on
this host, and drops the result into a room directory. It is the operator-facing front door to the
same engine `baton run` drives — a role is materialised into a single-step workflow, dispatched, and
its declared outputs are contract-checked exactly as a full run's are.

It is **not** the chat surface. A dispatch turn is non-interactive and runs to completion once; the
interactive session (chat) is a different path with a different prompt and a continuing turn.

```
baton dispatch <role> --spec <file> [--room-dir <dir>] [--adapter <vendor>] [--model <m>] [--effort <e>]
                    [--workspace <dir>] [--workflow-id <label>] [--output <path>] [--timeout <minutes>]
                    [--label <text>] [--attach <file>]...

baton dispatch --list-capabilities
```

## Flags

| Flag | Meaning |
|------|---------|
| `--spec <file>` | The task prompt for the worker — the file whose contents become the spec. |
| `--room-dir <dir>` | Where the run is recorded (created if absent) — this is the room. Optional: omitted, each invocation gets a fresh unique one at `$BATON_HOME/rooms/dispatch-<role>-<8 hex>` (`BATON_HOME` defaults to `~/.baton`, see `BatonPaths`) — outside any workspace a dispatch might audit (#1354/#1380), and fresh each time because a dispatch is one-shot and a stable derived directory would make the second `baton dispatch review` *resume* the first's terminal snapshot instead of running. |
| `--adapter <vendor>` | Run the role on a specific vendor (`claude` / `agy`) instead of its tier's default. The `--adapter` escape hatch; a role never names a vendor itself. |
| `--model <m>` | The model axis, independent of the role ([0017]/[0023]). Omitted keeps the tier's model — except on a vendor swap, where the tier's vendor-specific model is dropped for the new vendor's default (#1082). |
| `--effort <e>` | The effort axis, independent of the role. Omitted keeps the tier's effort; dropped on a vendor swap. |
| `--workspace <dir>` | The repository the worker's read access is scoped to. Defaults to the current directory. For a role whose grant is enforced as declared, this is literally the directory the worker runs in. For a role whose write grant is audited rather than enforced (a withheld-write role on a vendor whose withheld writes do not reach the outbox — today, the write-withholding roles on `agy`), dispatch instead auto-provisions a **fresh git worktree of this directory at `HEAD`** and hands the worker that (#1354/#1380) — the worker never sees uncommitted or staged changes in that case, only what HEAD already had. Bound explicitly because `agy -p` ignores the process working directory (#491). |
| `--workflow-id <label>` | A label forwarded to the run; defaults to the materialised template id. |
| `--output <path>` | Copy the role's primary declared output to `<path>` once the run reaches Terminal, in addition to leaving it under the room's own `artifacts/`. Role dispatch only — refused up front on a template dispatch, the same way `--spec` is. `<path>`'s filename is validated before anything is printed or written: it must name a file (not end in a separator), must not start with `.` (the engine's reserved namespace), must not collide with the engine's own `prompt.txt` capture, and must not collide with another output the same role already declares. |
| `--timeout <minutes>` | Override the dispatched role's own catalog timeout for just this dispatch — a role that legitimately needs longer than its fixed tier timebox (an orchestrator coordinating sub-lanes, say) does not have to die mid-flight. Role dispatch only — refused up front on a template dispatch, the same way `--output` is: each phase carries its own role's timeout, so there is no single one to override. Must be a positive whole number of minutes; rejected outright above a 24h ceiling (a non-interactive dispatch has no confirmation prompt to gate a larger value behind); merely flagged on stderr above 2h. |
| `--label <text>` | Display text only, e.g. `"the #1496 env-snapshot lane"` — so Fleet Glass shows something legible instead of the bare `dispatch-<role>-<8 hex>` directory name. Never part of the room directory's own name. Trimmed, newline-folded, capped at `DispatchOptionsParser.MaxLabelLength` chars; full contract in `spec/baton.md` §2. |
| `--attach <file>` | Repeatable (#1500). Copies `<file>` into the room's `artifacts/attachments/` directory before the worker starts, and appends one line to the prompt naming every attached file and that directory. Keeps a brief short instead of pasting context documents inline. Role dispatch only — refused up front on a template dispatch, the same way `--output`/`--timeout` are. Content is operator-supplied and **inbound**: it never passes the deliverable secret gate that governs a worker's own outputs. Each named file must exist; a missing one is a typed argument error before the room is created. |
| `--list-capabilities` | Prints every adapter's supported models and effort values, plus each catalog role's timebox default, and exits — no `<role>` or room required (#1500). `WorkerRoleCatalog.All` is the same catalog `ModelAndEffortValidationTests` reads directly; both vendors' effort tables come from `EffortTierMapping`, the exact static tables `ClaudeWorkerAdapter.Resolve`/`AgyWorkerAdapter.Resolve` call into on every `--effort` that test suite exercises — so the role and effort sections can never drift from what dispatch actually accepts. Claude's model aliases (`ClaudeWorkerAdapter.ModelAliases`) are read live too, but that specific list has no validation surface of its own — every alias always resolves to a vendor-current model, so nothing dispatch-side rejects one. agy has no equivalent alias catalog — its model names are suffix-parametrized (`gemini-<version>-<flash\|pro>-<low\|medium\|high>`), so the printed agy model examples are illustrative text, not a sourced table. |

#1355's acceptance criterion "one output path" is about `--output`/the printed fact above naming one
destination — not about a role declaring only one output (`review` declares two: `report.md` AND
`verdict.json`). `DispatchCommandEndToEndTests.Without_output_the_printed_fact_names_the_artifacts_directory_not_a_fabricated_file_path`
(pre-existing, #1354/#1380) is what pins the reading actually shipped.

Vendor, model, and effort are **three independent axes** over a role's instructions ([0017]):
the role carries a default bundle (its tier), and each axis overrides on its own.

Model and effort are validated at the adapter boundary before dispatch (#1090): a dot-delimited claude
id (`claude-opus-4.8`, a typo for `claude-opus-4-8`) is refused with the correction rather than run;
and on agy, where the effort suffix in the model name and `--effort` are one control, a disagreeing
pair is refused up-front naming the real cause instead of failing after the run has started.

### The spec/grant mismatch lint

Before a role's spec is dispatched, it is heuristically scanned for shell- or network-implying
instructions (`gh `, `git `, `dotnet `, `pixi `, `curl`, "run the", "execute", an `http(s)://` URL)
and compared against the resolved role's grant (#1500). A line implying a capability the grant
withholds prints a warning to stderr naming the line and the missing category, e.g.:

```
Warning: Spec line 4 ('gh issue view 1500') implies shell instructions (gh), but role 'advise' has no-shell grant.
```

This is a **warning, never a refusal** — the heuristic is not a parser and cannot know a matched line
is inert prose ("pixie dust") or that the worker will route around it; it only shortens the loop from
"the lane discovers its instructions are unexecutable mid-flight" to "the operator sees it before the
room exists." The named heuristics are `DispatchSpecLinter.Heuristics`, the single source both the
lint and its tests read.

### The auto-provisioned worktree, and what it costs

An audited role's dispatch prints the consequence before the run starts:

```
Workspace: worktree of <repo> at HEAD (<short-sha>) — uncommitted changes are not visible to the worker
```

The provisioned tree is torn down once the room reaches Terminal — **except** when it carries
uncommitted changes (a worker's own output written but not committed) or a removal is blocked (a
still-held file), in which case it is deliberately kept rather than discarded, and a Ctrl-C or crash
mid-run leaves it in place too. A kept tree is one more entry in the *workspace repository's* own `git
worktree list`, not something the operator asked for per invocation — `baton run`'s own worktree teardown
reporting (`worktree <outcome> at <path>`, printed to stderr) is what surfaces it.

### The printed grant line

Every dispatch also prints the least-privilege grant profile actually in force, one line per bound
worker (just one line for an ordinary single-role dispatch), before the run starts (#1355 — least
privilege default grants per role):

```
Grant: read, no-write, no-shell, no-network
```

Read left to right: `ReadFiles`, then `WriteFiles` (an `AuditedNotEnforced` write — the shape
`--workspace`'s row above describes — prints as `write (workspace-wide inside an isolated worktree;
audited against declared outputs after the run)` rather than a bare `write`: the grant is NOT scoped to
the declared outputs while the worker runs — the vendor hook cannot path-scope it, only confine writes
to the provisioned worktree — and declared-output confinement is checked only afterward, by the
post-run cleanliness audit; see `GrantAuditMode.AuditedNotEnforced`'s own doc), `RunShellCommands`,
`NetworkAccess`. This is the same category vocabulary the fake adapters in the test suite already use
for a grant, not a second one invented for this line — read it as what the invoking agent can honestly
relay to its own permission layer, not as a hardening claim about a vendor that was never asked.

Only printed for a bound worker whose adapter actually consumes a structured grant (implements
`IPermissionGrantTranslator`, `src/Baton.Vendors/WorkerBindingResolver.cs`'s own rule for which
adapters a grant governs). A composed template's capture step, say, spawns `git` directly and never
reads a grant at all — its phase gets no line printed, never a placeholder one.

**Read-shaped roles** (`review`, `fact-check` — both `write_files: false`) default to `claude`, whose
withheld writes still reach the outbox through AER's own hook rather than the `AuditedNotEnforced`
path above (`IWorkerAdapter.WithheldWritesReachTheOutbox`, `docs/decisions/0004-permission-scopes.md`)
— that path is only entered on `--adapter agy`.

`fact-check` stays `no-shell`/`no-network` outright. `review` no longer does (#1456, reversing
#1355's flat refusal for this role specifically — see spec/baton.md §9 for the full reasoning and the
network-honesty caveat): it now carries a scoped read-only `git`/`gh` shell grant. The exact
allow/deny pattern lists and the three catalog fields expressing them live canonically in
spec/baton.md §9; this page does not restate them. Enforced on claude via
`--allowedTools`/`--disallowedTools` pattern matching — a measured same-tool ceiling, not mere
pre-approval — not a `PreToolUse` hook change. `agy`'s `IPermissionGrantTranslator` still refuses
`RunShellCommands` without `NetworkAccess` with no scoped exception, so this shell grant does not
reach `--adapter agy`: `review` there now refuses to dispatch (`PermissionGrantUnsupportedException`)
rather than falling back to its old no-shell shape. `tools/baton-agy-loop/dispatch.py` is extended to
match — spec/baton.md §9's paragraph on that tool states what its extension covers.

`advise` and `patch` are the same shape by outcome (no unscoped shell or network) but not by
mechanism: `advise` keeps an explicit `write_files: true` (see its own `purpose` field in
`WorkerRoles.json` for why — narrowing it broke the dispatcher's grant_refusal() coherence check on
its default `agy` tier), and `patch` never grants a write in the first place —
its whole point is proposing a diff without mutating the workspace.

### The printed skill roster

Every dispatch also prints the worker's discovered skill roster, one line per bound worker whose
adapter is registered (or `Skills (<worker>)` for composed templates), before the run starts (#1512).
Like the Grant line above, a composed template's capture step gets no line, for the same "spawns `git`
directly, nothing to report" reason as the Grant exclusion — but drawn independently, since skill
discovery does not depend on whether an adapter consumes a permission grant (`src/Baton.Cli/DispatchCommand.cs`).

```
Skills: none discovered
```

or, when skills exist in the worker's environment (e.g. `~/.claude/skills/` or `<workspace>/.claude/skills/`
for Claude — also `<CLAUDE_CONFIG_DIR>/skills` when `BATON_CLAUDE_CONFIG_ROOT` is set, replacing the
`~/.claude` arm rather than adding to it — or `<workspace>/.agents/skills/` for agy):

```
Skills: artifact-design, run-checks
```

For a worktree-provisioned binding (an audited role), the roster scans the source repository rather
than the worker's not-yet-provisioned worktree, and says so —
`Skills (from <repo>; the worker runs in a fresh worktree at HEAD): …` — since an untracked skill it
finds there may not survive into the worker's actual checkout. Full rationale on the exclusion above
and this line: `src/Baton.Cli/DispatchCommand.cs`'s skill-roster block.

**Vendor coverage, honestly scoped.** The `<workspace>/.claude/skills` and personal-arm paths reflect
Claude Code's own documented `SKILL.md` convention; the `BATON_CLAUDE_CONFIG_ROOT` arm and the agy
`.agents/skills` path each rest on an unmeasured vendor fact, tracked in #1572 for agy and in the
adapters' own comments (`src/Baton.Vendors/ClaudeWorkerAdapter.cs`, `AgyWorkerAdapter.cs`) for both.

**Rule for briefs:** Dispatched workers run in their own process and do not inherit the conducting session's loaded skills. Briefs must inline what they need; a named skill only works if the worker's roster shows it. Skill forwarding is not performed by dispatch.

## Roles

Each role declares what it must produce; those declarations become the contract the engine enforces,
so a role that writes nothing fails loudly. The roles and their outputs are defined in
`src/Baton.Vendors/WorkerRoles.json` (authoritative); this table is a snapshot, pinned against that
catalog by `WorkerRoleCatalogTests`.

| Role | Tier | Writes | For |
|------|------|--------|-----|
| `advise` | standard | `advice.md` | Weighing an open design question before building — a second opinion. |
| `implement` | standard | `changes.md` | A bounded change whose approach is already decided; exercises the write path. |
| `review` | frontier | `report.md`, `verdict.json` | Adversarial review of a claim; the default for a PR touching `src/` or asserting something in `docs/`. |
| `patch` | frontier | `patch.diff` | Proposing code changes as an applyable diff without mutating the workspace. |
| `fact-check` | minimal | `findings.md` | Confirming an exhaustive, supplied list of facts against the repo — not for noticing what the list omits. |
| `janitor` | cheap | `janitor.md`, `branch.diff` | Running named mechanical checkers to green after an implementer, without changing behaviour. |
| `orchestrate` | orchestrator | `turn-actions.json` | A resident room turn that reads room state and emits turn actions. |

The prompt each worker receives is the spec followed by the role's own output instructions, so the
worker is told to produce exactly what the contract asserts. A dispatched worker is also told its turn
is one-shot (#1095): do the work to completion now and write the outputs before the turn ends — never
schedule background work or wait for a wake-up, because nothing resumes the turn.

## `baton redispatch` — rerunning a terminal room with an amended brief

```
baton redispatch <room-dir> [--spec <amended-brief>] [--adapter <vendor>] [--model <m>] [--effort <e>]
                          [--workspace <dir>] [--output <path>] [--timeout <minutes>] [--label <text>]
```

`<room-dir>` names the parent room to rerun. The full contract — what each flag inherits from that
room vs. overrides, the Terminal/single-role refusals, and where lineage is recorded — is
`spec/baton.md` §2; this page does not restate it.

## What a dispatch leaves in the room

The room directory accumulates the materialised workflow definition and its worker bindings, the
`flow.jsonl` event ledger (the append-only record of what the engine did), and an `artifacts/` tree
holding each step's declared outputs. The authoritative room layout is `spec/baton.md` §2;
`baton status <dir>` — the room directory is positional there, not a flag — reads the ledger and
reports where each step stands.

## The vendor premise

AER spawns the vendor's **own** first-party CLI, which authenticates itself against a **subscription**
— AER never handles a credential, and there are no API keys anywhere in this path (Architecture Rule
4). So a role runs only on a vendor that is already logged in on this host, and *which* vendor that is
is a fact of the host, not something a dispatch can provision. Dispatching a role to a vendor whose
CLI is not authenticated fails at that vendor's own login check, not inside AER.

If the vendor reports quota exhaustion, the engine paces a retry to the reported reset instant
(decision 0026) rather than burning attempts on a doomed retry. A foreground `baton dispatch` surfaces
that park — `Parked on vendor quota — the run resumes automatically at <time>` — and can be stopped
with Ctrl-C, which records a resumable state; re-running resumes it (#1094).
