# Provenance

This directory is a snapshot import of [`aer-works/aer-core`](https://github.com/aer-works/aer-core)
at commit `7762242c959e6312eccdd91750a0dafb3a6c1a1e` (tag `aer-core-v0.6.0-3-g7762242`, 2026-07-18),
folded into this repo as plain tracked files (#1458). It replaces the `external/aer-core` git
submodule that pinned the same commit.

This is a snapshot copy, not a `git subtree` import — the commit history is not carried over. The
source repository stays archived on GitHub as the historical record; this file exists so that
record stays reachable from the folded-in tree.

Excluded from the copy: `.git/` (submodule metadata), `target/` (Cargo build output, gitignored
here at its new path), and `.github/` (aer-core's own CI workflows — GitHub only reads workflows
from a repo root's `.github/`, so these would have been silently inert at this nested path; the
one still-needed job, `cargo test`, was folded into this repo's own `.github/workflows/ci.yml`
instead — see that file's `test` job).
