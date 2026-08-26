# Baton

Baton is the AER (Agent Execution Runtime) ecosystem's workflow tool, built on `aer-flow`, its
workflow execution engine layer.

Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

## Documentation

**Start here:** [Decision records](docs/decisions/) — why the product is shaped the way it is.
[0012](docs/decisions/0012-what-aer-flow-is.md) says what Baton *is*; 0013–0018 follow from it.

> **If a document is in the live tree, it is current.** Anything superseded lives in
> [`docs/archive/`](docs/archive/) and is never an authority for current work. There is no
> "trust this one, but not that one" — a document that cannot be trusted gets fixed or archived.

- [The plan](docs/plan.md) - The living, gated plan: the bar, the decisions in force, and the work by phase.
- [Milestone history & decisions of record](docs/milestone-history.md) - What each completed milestone shipped and the durable decisions it left behind.
- [Agent Instructions](CLAUDE.md) - Architectural rules and development workflows for AI agents.
- [Vendor capabilities](docs/vendor-capabilities.md) - What each worker CLI can actually enforce and
  ask, every claim observed rather than assumed.
- [Behavioral Specs](spec/) - The source of truth for engine routing and adapter behaviors. The
  **engine** spec is current; the **UI** spec is superseded and marked as such — the UI is being rebuilt.
- [Walkthroughs](docs/archive/walkthroughs/) - Guided, end-to-end usage of the shipped stack. Currently
  historical: they teach the outgoing UI.
- [Runbooks](docs/runbooks/) - Manual, key-gated operational procedures not covered by CI.

## Vendor authentication

Baton does not authenticate to any model provider. It spawns the vendor's own first-party CLI
(`claude`, `agy`) as a subprocess, and that CLI uses whatever login the operator already established
on their own machine.

**AER never reads, copies, forwards, or stores a vendor credential** — no API keys, no OAuth tokens,
no access to the OS credential store, and it never places a credential into a config directory. This
is an enforced invariant, not an intention: see
[`VendorCredentialIsolationTests`](tests/Aer.Architecture.Tests/VendorCredentialIsolationTests.cs).

Baton is a personal tool. It is not offered as a product or a service, and it does not provide,
resell, or proxy access to any provider — you bring a CLI you have already signed into yourself.
Each vendor CLI remains subject to its own provider's terms, between the operator and that provider.

## Prerequisites

- **[pixi](https://pixi.sh)** — task runner.
- **.NET 10 SDK** — install separately (not managed by pixi), same as aer-core:
  - Windows: `winget install Microsoft.DotNet.SDK.10`
  - macOS: `brew install dotnet-sdk` or the official installer
  - Linux: follow [Microsoft's install guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux)

## Quickstart
```bash
# Install the Pixi environment
pixi install

# Run tests
pixi run test

# Format code
pixi run fmt
```

## Installing `aer`

`aer` is distributed as a self-built, unpublished `dotnet tool` — there is no public NuGet feed
(a single-developer project doesn't need one; see `spec/AER Overview.md` §6). Build a local nupkg
and install from it directly:

```bash
# Build the nupkg (embeds the native aer_core library for every OS CI already built one for)
pixi run pack

# Install it as a global tool from that local folder
dotnet tool install --global --add-source bin/pack aer

# Run it
aer run <workflow-file> --bindings <bindings-file>

# Remove it
dotnet tool uninstall --global aer
```

`pixi run verify-pack` runs this exact install → run → uninstall round trip end to end against a
trivial fixture (no live vendor call) — it's the same check CI runs unattended on every push.
