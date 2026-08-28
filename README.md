# Baton

Baton is a vendor-neutral worker-room engine an agent harness drives: it dispatches vendor CLI
agents (`claude`, `agy`) as workers inside durable, auditable rooms, and reports completion through
a machine contract.

Built in .NET, it reads structured workflow definitions, dispatches them to Workers (via `aer-core`), and bridges outputs back to the engine.

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

`aer` is distributed as a self-built, unpublished `dotnet tool` — there is no public NuGet feed;
a single-developer project doesn't need one. Build a local nupkg and install from it directly:

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
