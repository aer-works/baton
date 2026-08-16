# Answers — every open question closed

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material is unchanged from the artifact of the same name; where a decision
> record and this document differ, **the record wins** — it is the reviewed extraction. Kept
> because the records deliberately capture decisions, and this also holds screen
> specifications, delights and demonstration criteria that are not decision-shaped and would
> otherwise exist nowhere. See [../README.md](../README.md#kept-current-not-frozen-added-2026-07-25)
> for why this corpus is maintained in place rather than staying a closed snapshot.

---

Baton — answers

Design · every open question closed

## Answers

Every question the previous five passes left open, answered with a decision rather than an option list. Where an answer was checked against the code rather than reasoned, it says so.

### What a newly-added worker sees

The gap under "ask someone else" — a worker brought in at turn 104 to answer one question.

Was open

#### Nothing, a summary, or all 103 turns?

A summary of the room, plus the turns you attached in full, plus the ability to ask for more. Not one of the three options — the first two together, with the third as the escape hatch.

Full history is expensive and mostly irrelevant: a reviewer asked about one patch does not need 103 turns of exploration. Nothing at all makes the answer worthless, which defeats the point of a second opinion. So the default is a room summary plus the raising turn and its attachments verbatim — the evidence in full, the context compressed.

What it is being given is shown before you send. This is the part that makes it trustworthy rather than magic: the menu states it plainly, and every item can be removed or added.

The escape hatch is #424 — AER exposing the room's own state as a context source the worker can query. That turns "did it have enough context" from a guess into something the worker resolves itself when it needs to.

you · asking agy Is claude right that the correction is unnecessary?
agy will receive — a summary of 103 turns · the last 3 turns in full · plan.md · the diff

Change what it sees

Decided: summary + attached evidence in full + queryable state. Always disclosed before sending.

### Two workers, one file

Was open

#### What if claude and agy edit the same file at once?

Impossible by construction, and the design should say so rather than defend against it. Checked in the code: the daemon's turn lock is keyed on the directory path ( SessionTurnLockKey → AerPaths.RecordKey ), not on the room. Turns against one folder serialise — including turns from two different rooms pointed at the same folder.

So the real risk is not corruption, it is a room that appears hung because it is waiting on a lock held elsewhere . That is a UI obligation: a room waiting its turn says so, and names what it is waiting for.

Opening a second room on a folder that already has one warns first and offers the existing room, because two rooms on one folder is almost always a mistake rather than an intent.

waiting payments-api is running a turn in this folder. claude will start when it finishes.
Open payments-api Cancel

Decided: serialised already — surface the wait, name the holder, warn on a duplicate room.

### A queued message when a permission appears

Was open

#### The worker "finishes" by asking permission — does the queued message send?

No. A permission is not the end of a turn — the worker is still mid-turn and blocked on you. The queued message stays queued, and its label changes to say what it is now waiting for.

Sending into a blocked worker would be the worst outcome: the message would land as the answer to a permission prompt, which is not what you typed it for.

The queued message sends when the turn genuinely ends — after the permission is answered and the work completes. "Send now" still interrupts , which in this state means answering the permission with a denial and delivering your message instead. That is stated on the control rather than left to be discovered.

Permission · claude Run rm -rf build/ ?
Allow once Deny

queued · waiting on the permission above Also check the CLI entry point.
Send now — denies the permission Remove

Decided: the queue waits on the whole turn, permission included, and says what it is waiting for.

### A step's instruction

Was missing entirely

#### The shape editor had no prompt field

Every step has an instruction, and it is the step's main content — not a field tucked behind a disclosure. This was the worst omission in the design: a step named "review" has to tell its worker what reviewing means.

A step receives the previous step's output automatically , so the instruction says what to do with it rather than plumbing it in. No variables, no templating language — that is the complexity that makes workflow tools miserable, and the whole reason this is a list rather than a canvas.

The row shows the name, who runs it, and whether it gates; the instruction sits underneath as the body. A step with no instruction is invalid and says so at edit time rather than at run time.

draft claude

Write a plan for the change described in the room. Be specific about files and order of work.

review agy ask me first

Critique the plan above. Name anything that will not work, and say why. Do not rewrite it.

Decided: instruction is the step's body; previous output flows in implicitly; no template language.

### Saving a working document into the project

Was open

#### What if the file already exists, or has changed since?

Never a silent overwrite. If the target does not exist, it is written and the room says where. If it exists and differs, the diff is shown and the choice is explicit: replace, save alongside as a new name, or cancel.

If the project's copy changed after the working document was derived from it, that is called out specifically — "the project's version has changed since this was written" — because that is the case where replacing quietly destroys someone's work.

This is a merge problem, and the honest design is to refuse to guess: show both, let the person choose, and never make the destructive option the default button.

Decided: diff-and-choose, never overwrite by default, and flag divergence since derivation.

### Stopping a worker that is waiting on you

Was open

#### Stop, while a permission is pending

Stop cancels the turn and the permission with it. The permission belongs to the turn — it cannot outlive it. It disappears from the room, from the "needs you" list, and from the phone at the same moment, because they are three views of one object rather than three copies.

The room reads Cancelled , and the transcript records that a permission was pending when it was stopped — so the history explains itself later.

A notification already delivered for that permission is withdrawn where the platform allows it, and opening a stale one lands on the room saying the request no longer exists rather than on a dead prompt.

Decided: the permission dies with its turn, everywhere at once, and the transcript says why.

### Worker memory — the biggest gap

Backlog · #442

#### Memory falls out of the working directory and splits per vendor

The room owns the memory, not the worker. That single move fixes the split: one memory document, held by the room, given to every worker in it regardless of vendor. What claude learned in turn 12 is available to agy in turn 104 — which is the entire premise of putting them in a room together and is impossible while memory lives in per-vendor files.

It is a working document, so it needs no new concept. It appears in the files list, has versions and attribution, can be opened and edited by you, and can be saved into the project if you want it to become a real file. Memory being visible and editable is the difference between a feature and a haunting.

The project's own vendor files are still honoured — a repository's CLAUDE.md is still read by claude, because drop-in replacement means someone's existing setup keeps working. Room memory is additional and shared, never a replacement for what the project already has.

Writing to it is an explicit act, not an inference. A worker proposes a memory addition and it appears as an action to accept — the product never decides on its own what is worth remembering, which is both a correctness rule and the thing that keeps the document from filling with noise.

Action · claude proposes remembering "Tests are run with pixi run test , never bare dotnet."
Add to room memory Edit… No

Decided: memory is a shared, visible, editable working document owned by the room; vendor files still honoured; additions are accepted, never inferred.

### Derived rooms, roles, and skills

Backlog · #340

#### Starting a review from inside a room

A room can spawn a child room, and the child reports back into the parent as a turn. The parent stays live throughout — that is the whole point, and it is the journey the product currently fails.

Children appear in the sidebar indented under their parent, carry the parent's name plus their own, and are ordinary rooms in every other respect: they have workers, files, memory, and their own state. No new noun — a child room is a room whose origin is another room.

Spawning is a room command ( /review , or any template) and takes the current state as its input, so "have this reviewed properly" is one gesture rather than a setup ritual.

Decided: child rooms, nested in the list, reporting back as a turn; parent never blocks.

Backlog · #385

#### Is an "advisor" a new kind of participant?

No — it is a Skill, a standing instruction a worker attaches directly
([01-definition.md](01-definition.md), [0033](../decisions/0033-skills-attach-directly-no-persona.md)).
Adding "my advisor" to a room is attaching that skill to a worker already in it, or to one being
added — the same gesture as adding any worker. There is no shipped default "advisor" skill today;
it is an example of one someone could author. Whether a starter library ships at all is undecided
([0033](../decisions/0033-skills-attach-directly-no-persona.md)).

Resisting a new participant type matters: every new kind of thing in the room is another concept to explain, and everything an advisor needs is already expressible as a worker with an instruction.

Decided: a Skill, not a new noun. Roles are instructions.

Backlog · #386

#### Whose skills are they?

Two tiers, both visible, clearly marked. Skills that AER defines are realised per vendor and appear under "Room" — they work with whoever you address, which is what makes them worth defining once. A vendor's own native skills appear under that vendor and go only to it.

The palette already namespaces by owner, so this needs no new structure — it needs the room tier to be populated by AER's canonical skills rather than only by built-in commands.

Still true, and one addition since:
[04-workers-commands-control.md](04-workers-commands-control.md#the-calls-made-here) notes that
attached skills don't change this grouping — the command group stays headed by the worker's
vendor regardless of what's attached to it. The two tiers and their ownership rule are unchanged.

Decided: canonical skills under Room, native skills under their vendor, marked as such.

### The keyboard model

Backlog #268. Keyboard was appearing piecemeal; here it is as one map. Every one of these works from anywhere in the shell.

⌘K | Search rooms, files, and commands. The one shortcut worth memorising. |

j / k | Move down / up the room list without leaving the keyboard. |

1 – 9 | Jump to the nth room that needs you. Triage without reading. |

y / n | Allow / deny a permission. Only active when one is focused, and never bound to Enter. |

⌘↑ / ⌘↓ | Jump to the previous / next gate or failure in a long room — the event rail, driven from the keyboard. |

/ | Commands. @ for files. Both from the composer. |

⏎ / ⇧⏎ | Send / newline. The same on the phone. |

Esc | Close the thing that is open; never destroys typed text. |

The rule behind the map: a destructive action never sits on a key you might hit by reflex. Enter sends; it never approves, never denies, never deletes.

### The remaining small ones

Backlog · #405

#### The working-status verb

The specific action whenever it is known — "Reading TasksViewModel.cs ", "Running pixi run test ". Specific beats charming, every time, because it is information rather than decoration.

For the genuinely generic wait, M27 uses a plain deterministic label ("Working", "Needs input", "Executing step 2 "), always with elapsed time beside it — not a curated whimsical set. A playful verb for that wait is real design work (a hand-written string set, a locale story, a per-state mapping) that doesn't exist yet; shipping a plain label now rather than inventing that mid-pass is the M27 scope call
([#578](https://github.com/aer-works/baton/issues/578) tracks bringing a curated set back once
that's actually specified).

Decided: the specific action whenever it is known; a plain deterministic label for the genuinely generic wait, always with elapsed time, always switchable — playful verbs are a later, separately-specified addition, not M27's default.

Backlog · #282

#### Desktop notifications

Permissions only, by default. They are the kind that blocks a worker outright, and on the desktop you are usually present for everything else. Decisions and actions are configurable but off, because a notification for something you can already see in the sidebar is noise that teaches people to disable notifications entirely.

Same rule as the phone: a notification informs and opens; it never decides.

Decided: permissions by default, the rest opt-in, never actionable from the notification.

Backlog · #266

#### Curved Bézier DAG edges, hover tracing, motion

Close it. It is polish for a freeform canvas, and there is no longer a freeform canvas. The shape surfaces that survive — a vertical step list and a stage strip — need neither curved edges nor edge-hover tracing.

Anything genuinely wanted from it (a running step being legible at a glance) is already carried by the status marks.

Decided: obsolete. Close rather than implement.

From the stress test

#### The two paths that were three levels deep

Both fixed the same way: remember the last choice and offer it first.

Starting shaped work drops from four actions to two — a pinned default template plus the last folder, so "New room" with Enter does the thing you did last time. Changing a model puts recent combinations at the top of the picker, so the common swap is one click rather than three levels of menu.

Decided: a pinned default template, and recents at the top of the worker picker.

### What is still genuinely open

Two things I will not pretend to have closed, because both need something other than a design decision.

Needs a measurement

#### Whether permissions can be raised at all

The whole permission design rests on #445 — AER hosting an MCP server a worker calls to ask the user something mid-turn. The premise of its predecessor was already disproved once by measurement ( claude -p auto-approves MCP tools rather than surfacing them), which is why the seam inverted. Before any of this is built, that mechanism needs a live probe against each vendor CLI — the same discipline that stopped a whole feature being built on a mechanism that did not exist.

**Resolved 2026-07-25**, by [0029](../decisions/0029-the-gate-is-three-mechanisms.md) and the
probes it rests on (`#472`, `#529`, `#530`, `#531`). The mechanism exists and is measured, not
single but threefold — a `PreToolUse` hook, a blocking `tools/call`, and elicitation — each
covering a different population of tools, on both vendors. It is not simple: at least one
mechanism fails silently when broken, `agy` has strictly less coverage than `claude`, and the
non-blocking migration path (SEP-1036) is blocked on a vendor shipping it. This entry's original
verdict — "verify the mechanism per vendor before building the surface" — is exactly what
happened. See 0029 for the honest version.

Resolved 2026-07-25: three mechanisms, measured on both vendors, per 0029 — not simple, but no longer unverified.

Needs you

#### Does a room live in one folder forever?

Everything designed assumes a room is bound to one directory for life. That is clean, and it may be wrong: work that spans two repositories is normal, and today it would mean two rooms that cannot see each other. I have no strong instinct here, and it changes the object model rather than a screen — so it is a genuine question rather than a decision I should make quietly.

Open: your call — one folder per room, or several.

Every question raised across six passes is now either decided above or explicitly named as open. The next step is turning this into numbered decision records and re-deriving the journeys and the backlog from them.
