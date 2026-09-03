# Fix round for PR #1748 — changes

Opus review (`REVIEW-rev1748.tmp.md`, deleted before the final commit) returned BLOCK with nine
findings. All nine are fixed in this round; reasoning for each lives at its cited location, not
restated here.

## F1 (high) — newline handling in the segmenter

`src/Baton.Vendors/ShellCommandPatternMatcher.cs`, `TrySegmentChainedCommand`. `spec/baton.md` §9
corrected to match.

Test: `An_embedded_newline_ahead_of_a_denied_command_is_denied_not_folded_past`
(`tests/Baton.Vendors.Tests/ShellCommandPatternMatcherTests.cs`) and one added row in
`Unscoped_write_role_denies_label_merge_and_api_writes_from_the_catalog`
(`tests/Baton.Cli.Tests/HookCheckCommandTests.cs`).

## F2 (medium) — the whole-line fold's head-only match

`IsDeniedByTokenizedHead` gained an `anyOffset` parameter, set by the fold branch in
`EvaluateChainedCommand`, plus a token-wrapper strip.

Test: `A_denied_command_is_caught_on_the_whole_line_fold_regardless_of_wrapper_or_offset`
(same file). The escaped-space form was left as an accepted bypass, recorded in `spec/baton.md` §9
alongside the existing `${IFS}` one.

## F3 (medium) — stale comment in `AgyHookCheckCommand.cs`

Rewrote the ~357-362 comment to match the current shape (`src/Baton.Cli/AgyHookCheckCommand.cs`).

## F4 (low) — empty-command-line guard

`spec/baton.md` §9 narrowed; reason string at `ShellCommandPatternMatcher.cs`'s
`EvaluateChainedCommand` no longer says "scoped".

## F5 (low) — stale remarks/param doc

`EvaluateChainedCommand`'s `<remarks>` and `allowedPatterns` doc updated to scope the fail-closed
claim correctly.

## F6 (low) — weak control assertion

`A_scoped_grant_still_fails_closed_exactly_as_before` now asserts the verdict per row.

## F7 (low) — undocumented glob-grammar divergence

Two sentences added to `IsDeniedByTokenizedHead`'s doc comment.

## F8 (low) — missing `${IFS}` bypass test

Added `Word_splitting_via_IFS_ahead_of_a_denied_command_is_the_other_accepted_bypass_on_an_unscoped_grant`.

## F9 (low) — issue #1735

Commented on #1735 noting its "Fix 1" was superseded — see `spec/baton.md` §9 for the current
mechanism.

## Not touched

The operator ruling itself and the scoped-grant path are unchanged.
