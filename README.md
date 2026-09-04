# Baton

Baton is a vendor-neutral worker-room engine an agent harness drives: it dispatches vendor CLI
agents (`claude`, `agy`) as workers inside durable, auditable rooms, and reports completion through
a machine contract.

Built in .NET, it parses a declared workflow, hands each step's work to a Worker, and folds the result back into the run.

## Documentation

**Start here:** [`spec/baton.md`](spec/baton.md) — the spec, and the sole register for what the
system is: the dispatch unit, the completion contract, gates, Fleet Glass observability, the
narrowed daemon, and bindings/permissions. If this README and the spec disagree, the spec wins.

- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Invoking Baton](docs/agents/invoking-baton.md) - For an agent whose job is to *run* a Baton lane
  against some other repo rather than develop Baton: the invocation that works today, a complete
  workflow+bindings pair, and the edges it will hit.
- [Vendor capabilities](docs/vendor-capabilities.md) - What each worker CLI can actually enforce and
  ask, every claim observed rather than assumed.
- [Runbooks](docs/runbooks/) - Manual, key-gated operational procedures not covered by CI.

## Verbs

| Verb | What it does |
|---|---|
| `baton run` / `baton dispatch` / `baton redispatch` | Start a workflow room, or rerun a terminal one with an amended brief. |
| `baton cancel` / `baton decide` / `baton resolve` / `baton resume` / `baton supply` | Mutate an already-started room — cancel a lane, record a pause decision, resolve a captured response, resume a stalled pump, supply a supplementary output. |
| `baton status` | Read-only projection of a room's current state. |
| `baton keep` / `baton unkeep` | Mark/unmark a room exempt from `RoomRetentionSweep`'s artifact pruning. |
| `baton deliver <file> [--title <text>] [--room <room-dir>]` (`--room-dir` also accepted) | Deliver an orchestrator artifact into a room (defaults to standing conductor room) so it reaches the Fleet Glass inbox. |
| `baton room delete <room-dir> [--keep-deliverables] [--force]` | Remove one room for good: its directory, its `room-registry.jsonl` lines, and (best-effort) a deliverables tombstone. Refuses a non-terminal room unless `--force` — see `spec/baton.md` §8. |
| `baton rooms prune --terminal [--older-than <days>] [--state <state>] [--dry-run] [--yes]` | Batch form of `room delete`, plus unconditional registry hygiene (dedupe, drop lines whose directory is gone). Lists candidates by default; `--yes` actually deletes. |
| `baton templates` | List the built-in workflow template catalog. |
| `baton mcp` / `baton daemon` | The stdio MCP server workers connect to (`fleet_status`, `yield`, `memory-edit-proposal`, `promote-artifact`, `room_detail`), and the narrowed background daemon (`spec/baton.md` §7). |

`spec/baton.md` is the authority on every verb's exact contract — this table is an index, not a
restatement.

## Vendor authentication

Baton does not authenticate to any model provider. It spawns the vendor's own first-party CLI
(`claude`, `agy`) as a subprocess, and that CLI uses whatever login the operator already established
on their own machine.

**Baton never reads, copies, forwards, or stores a vendor credential** — no API keys, no OAuth tokens,
no access to the OS credential store, and it never places a credential into a config directory. This
is an enforced invariant, not an intention: see
[`VendorCredentialIsolationTests`](tests/Baton.Architecture.Tests/VendorCredentialIsolationTests.cs).

Baton is a personal tool. It is not offered as a product or a service, and it does not provide,
resell, or proxy access to any provider — you bring a CLI you have already signed into yourself.
Each vendor CLI remains subject to its own provider's terms, between the operator and that provider.

## Prerequisites

- **[pixi](https://pixi.sh)** — task runner.
- **.NET 10 SDK** — install separately (not managed by pixi):
  - Windows: `winget install Microsoft.DotNet.SDK.10`

## Quickstart
```bash
# Install the Pixi environment
pixi install

# Run tests
pixi run test

# Format code
pixi run fmt
```

## Installing `baton`

`baton` is distributed as a self-built, unpublished `dotnet tool` — there is no public NuGet feed;
a single-developer project doesn't need one.

**First install, or refreshing an already-installed tool: `pixi run tool-refresh`.** Installs side-by-side
per-commit versions under `~/.baton/tools/<sha>` with a lightweight PATH launcher in `~/.dotnet/tools`
resolving `current` at process start — see [`spec/baton.md`](spec/baton.md) §8 (*Installation and versioning*)
for the authoritative directory structure, launcher details, and automatic pruning policy.

`pixi run verify-pack` runs the underlying install → run → uninstall round trip end to end against a
trivial fixture (no live vendor call) — it's the same check CI runs unattended on every push.

