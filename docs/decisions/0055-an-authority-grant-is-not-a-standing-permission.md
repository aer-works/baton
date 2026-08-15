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

**3. The type name `PermissionGrant` stays.** It reads as "grant *of a permission*", which is
accurate, and renaming a shipped record persisted in every room's `bindings.json` would cost a
migration to fix a word that is not actually wrong in context. The rule binds the *language*, which is
where the confusion happened; a compound identifier that carries its own disambiguator is not the
failure mode.

**4. Neither family migrates onto the other, and neither is deleted.** They are different concepts;
one is shipped and one is owed. No work on the authority family until M26/#778 picks it up.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The two families carry genuinely different content | `GrantLevel.cs` is authority levels L0–L3 with origination scope and spend bounds; `PermissionGrant.cs` is tool reach (read/write/shell/network) | they are one concept after all, and one implementation should be retired |
| The authority family is owed, not dead | 0049 term 4 ("owed, not yet held at HEAD"); room spec §5/§8; `OrchestratorTurnPrompt.RenderEvent:137-139` already renders the three events | it is dead code and 0049's authority model needs re-deciding, not this record |
| "Standing permission" is not a new coinage | 0022 §2 uses it | the naming half of this record is inventing vocabulary rather than recovering it |
| Renaming `PermissionGrant` is not required to fix the confusion | the confusion was in prose and issue titles, and the type name carries its own qualifier | a rename plus a `bindings.json` migration is owed |

## Consequences

**Easier.** #1238 (revoking a standing permission) can be built on the live path without appearing to
prejudge the authority model's future. A reader of either register can tell which object a sentence is
about from the sentence.

**Harder.** Nothing structurally, but the rule has to be applied to text that already exists. 0022's
own obligations list said "standing grant" twice; it is corrected with this record, and #1238's title
with it. One occurrence in 0022 is deliberately **left alone**: it sits inside a verbatim quotation of
the corpus stress test, and a quotation is not ours to edit — the word there is a record of what was
said, not a claim we are making. Any later drift is a `record-once` failure in the register that
defines the words, which is the same shape as the drift this fixes.

**Not covered.** How a standing permission is revoked (#1238), and whether the authority grant's
events need any change when the orchestrator arrives (M26/#778) — this record settles the nouns, not
either mechanism.

Related: [0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md) (the authority grant),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (the ladder whose persisting rungs write a
standing permission), [0004](0004-permission-scopes.md) (the scopes it persists),
[0002](0002-one-vocabulary.md) (the rule this applies), #497 and #1238.
