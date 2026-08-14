# Aer.Mobile

The Flutter/Android **remote client** for AER Flow — the phone half of the daemon's remote-control
story (M21–M24). It pairs with a running `Aer.Daemon`, then drives real work from anywhere:

- **Pairing** — QR-code scan or manual host/code entry, over zero-config Tailscale (embedded `tsnet`
  via Go CGO — no separate Tailscale app install; see `docs/milestone-history.md`, M21).
- **Decisions, answered in the room** — Approve / Reject / send-back (Supersede, artifact-referenced,
  with no host filesystem access) and Stop, all on the room's own transcript rather than a separate
  inbox: a gate is answered where it was raised (#1226). The artifact under review is shown before
  deciding.
- **Live room & chat streaming** — room projection and in-turn progress pushed over WebSockets
  (`ChatScreen`, which every room opens in, workflow or chat), filtered per client so two devices can
  view different work.
- **Start work** — the built-in template picker and Unified Task Creation (Chat / Codebase session /
  Two-Vendor Dialogue) front doors (M22, M24).

## Building

Built as a debug/sideload APK, not through `dotnet`. Use the repo's mobile tasks (`pixi run
mobile-build` / `mobile-test`, or `scripts/mobile-build.sh`) rather than `flutter` directly — they
shim the environment the `tailscale` package's native cgo build hook needs. Journey (widget) tests
carry the `journey` tag and are excluded from the default `flutter test` run; see
`tests/Aer.Journeys.Tests/README.md` and `docs/runbooks/journey-tests.md`.
