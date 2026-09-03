# Fix round for PR #1748 — changes

Opus review (`REVIEW-rev1748.tmp.md`, deleted before the final commit) returned BLOCK with nine
findings. All nine are fixed in this round.

## F1 (high) — a newline anywhere on the line disabled the pattern deny list

`src/Baton.Vendors/ShellCommandPatternMatcher.cs`'s `TrySegmentChainedCommand`: on the
`permissiveMetacharacters` (unscoped-with-deny) path, an unquoted `\n`/`\r` now closes the current
segment the way `;` does, instead of being fatal and forcing the whole-line fold. Corrected
`spec/baton.md` §9's "newline boundary" sentence to say what the code actually does (newline is now a
real segmenter boundary on this scope, not a fold trigger).

Test: `An_embedded_newline_ahead_of_a_denied_command_is_denied_not_folded_past`
(`tests/Baton.Vendors.Tests/ShellCommandPatternMatcherTests.cs`) plus one hook-level row in
`Unscoped_write_role_denies_label_merge_and_api_writes_from_the_catalog`
(`tests/Baton.Cli.Tests/HookCheckCommandTests.cs`), both exercising
`git status\ngh label create operator-merge` under `implement`'s real grant → denied.

## F2 (medium) — the whole-line fold matched only the head token

`IsDeniedByTokenizedHead` gained an `anyOffset` parameter (set by the fold branch in
`EvaluateChainedCommand`) that scans every token offset in the folded segment, not only offset 0, and
strips a leading backtick/`` $( ``/`(`/quote character off each compared token before matching.

Test: `A_denied_command_is_caught_on_the_whole_line_fold_regardless_of_wrapper_or_offset`
(`` `gh label create x` ``, `$(gh label create x)`, `(gh label create x)`,
`echo $(date) && gh label create x` — all denied). The escaped-space form `gh\ label create x` was
left unfixed and recorded in `spec/baton.md` §9 as part of the accepted `${IFS}` bypass family (no
backslash-space token-splitting fix attempted — same "needs real argv reconstruction" cost as the
other two named bypasses).

## F3 (medium) — stale "asymmetry" comment in `AgyHookCheckCommand.cs`

Rewrote the ~357-362 comment: both hooks now engage the deny rung on "either list non-empty"; agy's
remaining difference from claude is only the lack of a `--disallowedTools`-level backstop for the
unchained case. Dropped the word "asymmetry".

## F4 (low) — `Unparseable` still fires on this scope for an empty command line

Narrowed `spec/baton.md` §9's "no longer fires at all on this scope" to except the
empty-command-line guard, and dropped "scoped" from the reason string at
`ShellCommandPatternMatcher.cs`'s `EvaluateChainedCommand` (now `"unparseable (empty command line)"`).

## F5 (low) — stale remarks/param doc

Updated `EvaluateChainedCommand`'s `<remarks>` and the `allowedPatterns` param doc to scope the
fail-closed-to-`Unparseable` claim to a SCOPED grant and point at the fold for the unscoped-with-deny
case.

## F6 (low) — weak control assertion

`A_scoped_grant_still_fails_closed_exactly_as_before` now asserts the verdict per row:
`Unparseable` for the metacharacter row, `DeniedSegment` for the chain row.

## F7 (low) — undocumented glob-grammar divergence

Added two sentences to `IsDeniedByTokenizedHead`'s doc comment: a non-`*` deny entry matches a token
prefix (widening) rather than requiring whole-line equality, and a trailing `*` does not reach inside
a token (narrowing) the way `IsAllowed`'s does.

## F8 (low) — the `${IFS}` accepted bypass had no test

Added `Word_splitting_via_IFS_ahead_of_a_denied_command_is_the_other_accepted_bypass_on_an_unscoped_grant`
(`gh${IFS}label create x` → allowed) to `ShellCommandPatternMatcherTests.cs`, named as the accepted
bypass it is.

## F9 (low) — issue #1735 describes a reverted mechanism

Posted a comment on #1735 noting its "Fix 1" (`IsPermissivelySafeMetacharacter`) was superseded by the
operator ruling recorded at `spec/baton.md` §9, and that substitution forms now allow on
unscoped-with-deny grants by design.

## Not touched

The operator ruling itself and the scoped-grant path are unchanged — no widening or narrowing of
`spec/baton.md` §9's core ruling.
