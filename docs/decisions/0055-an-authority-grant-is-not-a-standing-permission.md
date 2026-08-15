# 0055 — An authority grant is not a standing permission

Status: accepted
Date: 2026-08-14

## Context

Two families in the tree are both called "grant", and they answer different questions.

**The authority family** — `RoomEvent.GrantRecorded`/`GrantAmended`/`GrantRevoked`,
`RoomMutationInterface.RecordGrantAsync`/`AmendGrantAsync`/`RevokeGrantAsync`, projected into
`RoomState.ActiveGrants` — carries authority *levels* (`GrantLevel`: L0 Observe, L1 Dispatch,
L2 Tend, L3 ShipRoutine), template origination scope, and spend bounds. It is
[0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md)'s object: what a delegate may **decide and
originate on its own**.

**The permission family** — `PermissionGrant`, persisted in a room's `bindings.json` by
`RuntimePermissionGrantAmender` when the operator answers a persisting rung of
[0022](0022-permission-ladder-and-denial-is-an-answer.md)'s ladder — carries tool reach:
`ReadFiles`, `WriteFiles`, `RunShellCommands`, `ShellCommandPatterns`, `NetworkAccess`,
`DeniedShellCommandPatterns`. It is [0004](0004-permission-scopes.md)'s scopes made
durable: what a worker may **touch while it runs**.

The two were read as one concept in two implementations while slicing #1238, and the wrong
conclusion followed easily — that one must be dead code, or that one should migrate onto the other.
Neither is true, and the reason the mistake was available is that they share a word.
[0002](0002-one-vocabulary.md) is what forbids that, and this record is the correction.

The authority family currently has zero production callers. That is not evidence it is dead: 0049
term 4 records signed actions as "owed, not yet held at HEAD", the room spec places origination
authority in the wake bridge (§5) and the orchestrator (§8, M26/#778), and
`OrchestratorTurnPrompt.RenderEvent` already renders all three grant events into the orchestrator's
turn input. The consumer is the orchestrator that has not moved in yet.

## Decision

**1. Bare "grant" means the authority grant.** 0049's object, and only that. A grant has a level, a
scope of templates it may originate, and spend bounds. It is recorded, amended and revoked as room
events.

**2. The 0022 object is a permission; persisted, it is a standing permission.** That is 0022 §2's own
phrase, so this names what the design already said rather than coining a word. Prose, UI copy, error
messages, issue titles and commit subjects say "permission" or "standing permission" — never bare
"grant" — for anything about tool reach.

**3. The type name `PermissionGrant` stays, and this is a cost decision, not a principle.**
[0002](0002-one-vocabulary.md)'s *decision* text is broader than the line drawn here — "code and UI
use the same words… rename the code to the plain word wherever a good one exists" — and only its
*enforcement* (the #315 lint) is scoped to user-facing strings. So this record is leaning on the
narrower reading, deliberately and with that stated: the identifier already carries its own
disambiguator ("grant *of a permission*"), and renaming a record persisted in every room's
`bindings.json` buys a migration to fix a word that is not wrong in context. If that trade is ever
judged wrong, the rename is the remedy and nothing here argues against it.

**3a. The rule binds new and edited text, and existing records are corrected where both objects can be
in view.** 0022 and 0052 are corrected here: both are about the ladder that now sits in a register
beside an authority model, so a bare "grant" there is genuinely ambiguous.
[0004](0004-permission-scopes.md) is deliberately **not** rewritten — it predates the distinction and
is wholly about the permission object, so its own formulations ("effective grant = project ∩ room ∩
step", "grants fail closed") are unambiguous inside it, and rewriting a ratified record's core
formulation costs more than the ambiguity it would remove. That it was considered and left is recorded
here so the next reader neither "fixes" it nor reads it as an oversight.

**4. Neither family migrates onto the other, and neither is deleted.** They are different concepts;
one is shipped and one is owed. No work on the authority family until M26/#778 picks it up.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The two families carry genuinely different content | `GrantLevel.cs` is authority levels L0–L3 with origination scope and spend bounds; `PermissionGrant.cs` is tool reach (read/write/shell/network) | they are one concept after all, and one implementation should be retired |
| The authority family is owed, not dead | 0049 term 4 ("owed, not yet held at HEAD"); room spec §5/§8; `OrchestratorTurnPrompt.RenderEvent:137-139` already renders the three events | it is dead code and 0049's authority model needs re-deciding, not this record |
| "Standing permission" is not a new coinage | 0022 §2 uses it | the naming half of this record is inventing vocabulary rather than recovering it |
| Renaming `PermissionGrant` is not worth its migration (**assumed**, not measured — a cost judgement against 0002's broader decision text; see decision 3) | the confusion observed was in prose and issue titles, and the identifier carries its own qualifier | a rename plus a `bindings.json` migration is owed |

## Consequences

**Easier.** #1238 (revoking a standing permission) can be built on the live path without appearing to
prejudge the authority model's future. A reader of either register can tell which object a sentence is
about from the sentence.

**Harder.** The rule has to be applied to text that already exists, and **this record does not claim
the register is now consistent — only that the words are now defined.** What is corrected here: 0022's
obligations list, 0052 throughout (the cross-room rung is the same permission object), 0034's one
reference, and #1238's title. What is deliberately left: the occurrence inside 0022's verbatim
quotation of the corpus stress test — a quotation records what was said and is not ours to edit — and
0004 in full, per decision 3a.

**What has not been swept.** `grep -rniE '\bgrant(s|ed|ing)?\b' docs/` matches ~41 files. Only the
records most directly about the 0022 object were classified and corrected; the rest mix three
populations — the authority family (correct), ordinary English ("granted visibility"), and genuine
drift — and were not read closely enough to tell them apart. The hit rate in the files that *were*
checked was high, so more drift should be expected there rather than less. Anyone relying on "the
register uses these words consistently" is relying on something this record did not establish.

**Not covered.** How a standing permission is revoked (#1238), and whether the authority grant's
events need any change when the orchestrator arrives (M26/#778) — this record settles the nouns, not
either mechanism.

Related: [0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md) (the authority grant),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (the ladder whose persisting rungs write a
standing permission), [0004](0004-permission-scopes.md) (the scopes it persists),
[0002](0002-one-vocabulary.md) (the rule this applies), #497 and #1238.
