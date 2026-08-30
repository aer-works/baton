# 0035 — `aer yield` is a structured MCP tool call, not a text sentinel

Status: accepted
Date: 2026-07-26

## Context

The M27 UX design dialogue (workflows/addressing/dialoguing, `#575`) converged on replacing
`Aer.Workers.Dialogue`'s stop-condition mechanism — currently a plain substring match on a turn's own
output text (`DialogueRunner.TryStripStopSentinel`: `text.IndexOf(stopSentinel, ...)`) — with a
structured tool return, provisionally named `aer yield`, that a worker calls when it believes the
exchange should end. The dialogue's own reviewer flagged the text-match mechanism as inspecting
conversation content to make a control-flow decision — the same category of fragility CLAUDE.md's
Architecture Rule 1 forbids at the Flow-engine level, even though `Aer.Workers.Dialogue` is itself a
Worker from Flow's perspective, not Flow's own routing code, so the rule does not literally reach it.
Either way, a sentinel is brittle: a participant discussing the concept of consensus, or quoting the
sentinel string itself, can trigger a false stop; a formatting quirk can suppress an intended one.

**Corrected 2026-07-31 (#820).** This record's present tense is now historical: everything it
proposed has shipped, and the mechanism it replaced is gone. The MCP server host and `aer yield`
landed with #585 (`Aer.Mcp`/`Aer.Mcp.Host`, wired per participant by `DialogueYieldWiring`), which
also deleted `DialogueRunner.TryStripStopSentinel`; #820 then removed the `StopSentinel` field and
every authoring surface that wrote it. The Context section and the first `Rests on` row describe
the world as measured on this record's date — read them as the problem statement this decision
solved, not as current code.

Checking what mechanism is actually available to build this on (not assumed from the dialogue's own
text, which had no source access): [0029](0029-the-gate-is-three-mechanisms.md) already establishes
that **AER's own MCP tools are the one channel that carries a structured signal from a worker without
parsing its prose** — the "blocking `tools/call`" row in 0029's mechanism table. But checking the real
source tree found this channel is **measured as viable, never built**: `ClaudeWorkerAdapter`'s own
`--mcp-config` plumbing exists but points at a deliberately empty file (*"declares no servers, so this
adds nothing beyond what claude would otherwise discover on its own"*), and `GeminiWorkerAdapter` has
no MCP wiring of any kind. Every measurement 0029 cites came from `tools/vendor-verify`'s own
ad-hoc probe server, not a production component.

## Decision

**`aer yield` is realized as a real MCP tool call, hosted by AER's own (currently unbuilt) MCP server,
using the same per-vendor wiring 0029 already measured — and it does not need 0029's hardest problem.**

- **The tool itself is simple: a synchronous request, not a held-open one.** 0029's blocking
  `tools/call` mechanism exists to survive a *human's* absence — the 200-second ceiling, the
  SEP-1036 migration, the crash-recovery obligations all follow from that. `aer yield` waits on
  nothing: the model calls it, AER's MCP server acknowledges immediately, and `DialogueRunner` reads
  the captured call arguments (e.g. `{"status": "consensus"}`) to decide whether to end the exchange.
  None of 0029's held-open machinery applies here — this is the easy case that mechanism table already
  describes, not a new one.
- **Per-vendor wiring is already measured, just not wired up**: `claude` via `--mcp-config <path>
  --strict-mcp-config` (the exact flags `ClaudeWorkerAdapter` already passes, currently pointed at an
  empty server list); `agy` via a workspace-local `.agents/mcp_config.json` loaded through `--add-dir`
  (per `docs/vendor-doc-audit.md`'s `agy` MCP findings) — `agy` has no per-invocation flag equivalent
  to `--mcp-config`, so `Aer.Workers.Dialogue` must stand up a real workspace directory for this,
  unlike `claude`'s path-anywhere config file.
- **This is genuinely new infrastructure, not a per-dialogue tweak.** No production MCP server host
  exists in `src/` today for either vendor. Building `aer yield` means building AER's first real MCP
  server (likely shared infrastructure the permission gate's own eventual blocking-`tools/call`
  implementation will also need, per 0029), not a bespoke mechanism scoped to
  `Aer.Workers.Dialogue` alone.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The current stop condition is a substring match on a turn's own output text | **measured** — `DialogueRunner.TryStripStopSentinel`, read directly | there is no fragility to replace, and this record is solving a problem that does not exist |
| Both vendors declare MCP `elicitation` and honour it under `-p` | **measured, and this is the load-bearing one** — `gate.elicitation-capability` (claude) and `agy.elicitation-capability` (agy); `gate.elicitation-hook-event-fires` remains the untested row 0030 already flags | a structured tool return is not portable, the mechanism is claude-only, and `aer yield` cannot be the vendor-neutral primitive this record makes it |
| AER can host its own MCP server | **assumed** — the server is unbuilt, tracked as #585 | `aer yield` has no host, and the decision is unimplementable as written until that lands |

## Consequences

**Easier.** Termination becomes a fact AER's own code observes directly (a tool call arrived, with
these arguments) rather than a string it goes looking for — genuinely more robust, and immune to a
participant's prose ever accidentally matching or evading the signal.

**Harder.** This cannot ship as part of the Dialogue-shape UX alone. It requires a real MCP server
host neither adapter has today, per-vendor wiring that differs in kind (a config *path* for `claude`
vs. a config *directory* for `agy`), and `Aer.Workers.Dialogue` gains a dependency it does not have
now (spawning/hosting a server alongside each participant process, not just a bare CLI invocation).

**Obliges us to.** Scope this as its own piece of engineering work (filed as `#585`), sequenced before
any Dialogue-shape UI ships — already reflected in `docs/plan.md`'s M27 criteria, which blocks the
Dialogue-shape demonstration on this and on `#581`/`#582`. Whoever builds AER's first production MCP
server should build it as shared infrastructure, not scoped narrowly to `aer yield`, since the
permission gate's own blocking mechanism (0029) will need the identical host later.

Relates: [0029](0029-the-gate-is-three-mechanisms.md) (the mechanism this reuses, and the harder case
this explicitly is not), [0003](0003-templates-collapse-to-three-shapes.md) (the Dialogue shape this
unblocks). CLAUDE.md Architecture Rule 1 (the framing this replaces a sentinel-match to honor, even
though the rule does not literally bind `Aer.Workers.Dialogue`'s own internal loop).
