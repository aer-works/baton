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

**First install, or refreshing an already-installed tool: `pixi run tool-refresh`.** It drains — a
marker file that the lane-starting verbs refuse under, plus a refusal (or `--wait`) while any room
under `~/.baton/rooms` still looks live; `spec/baton.md`'s C-10 *Installation and versioning* paragraph
states that contract in full, including which verbs refuse, and `pixi run tool-refresh --abort` clears
a marker a killed refresh left behind — then packs, uninstalls, purges the NuGet cache for that version (mandatory — NuGet
otherwise silently keeps serving the stale same-version package), installs from `bin/pack`, and
verifies the reinstall actually took (`baton --version` matches, `baton templates --json` runs)
before printing a resume hint. `--dry-run` prints every command it would run without executing any
of them. See `tools/tool-refresh/refresh.py` for the drain predicate and `spec/baton.md`'s C-10 for
what the front-door drift WARN (`baton dispatch`/`baton status` warning when the installed tool is
behind this checkout) covers.

`pixi run verify-pack` runs the underlying install → run → uninstall round trip end to end against a
trivial fixture (no live vendor call) — it's the same check CI runs unattended on every push. The
individual commands `tool-refresh` wraps (`pixi run pack`, `dotnet tool install --global --add-source
bin/pack baton`, `dotnet tool uninstall --global baton`) still work by hand if you need one step in
isolation.
