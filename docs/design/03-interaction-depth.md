# Interaction depth — what a room does under the surface

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material is unchanged from the artifact of the same name; where a decision
> record and this document differ, **the record wins** — it is the reviewed extraction. Kept
> because the records deliberately capture decisions, and this also holds screen
> specifications, delights and demonstration criteria that are not decision-shaped and would
> otherwise exist nowhere. See [../README.md](../README.md#kept-current-not-frozen-added-2026-07-25)
> for why this corpus is maintained in place rather than staying a closed snapshot.

---

Baton — interaction depth

Interaction design · depth pass

## How it behaves

The shapes are settled. This is the layer underneath them — what happens while work runs, how output is rendered, how files move between vendors, and what every surface must handle. Written to be complete enough that implementation has nothing left to invent.

Background work Asking someone else Code and executions
Artifacts Shape editor on a phone States everything must handle
The inventory The calls

### Background work

The founding rule: a worker being busy is never a reason you cannot act. Everything here follows from refusing to let the product block you, because losing the ability to interject is the specific failure this product exists to avoid.

Desktop · a worker is running and you keep typing

aer-flow claude · working 2m agy +

you Rework the switcher so a new room shows up immediately.

claude · working Reading TasksViewModel.cs …
pixi run build ✓ 41s

queued · sends when claude finishes Also check the CLI entry point.
Send now, interrupt Remove

Type anything — it queues ■ Stop claude

Typing while a worker runs queues by default and never blocks. The queued message is visible as a real thing in the transcript, with the two escapes beside it: send it now and interrupt what is running, or take it back. Queueing is the default because the overwhelmingly common case is remembering a second instruction, not urgently needing to stop — but interrupting must always be one click, never a hunt.

Stop is always present and always distinct from a gate. A gate is the work asking you; Stop is you interrupting the work. They must never look alike, which is the whole reason cancelled is a dash rather than a square — a square is universally a stop control, and a state that looks like an action is a trap.

Nothing is modal, ever. No spinner that owns the window, no dialog that must be dismissed before you can look at another room. Progress belongs in the row, the chip, and the turn.

#### Work continues when you leave

Closing the window does not stop a worker — the daemon owns the run, which is what makes the phone useful at all. Reopening the app rejoins whatever happened while you were gone; a room that finished, failed, or hit a gate meanwhile shows that in its row, and the notification that fired is the same object as the gate you now see.

### Asking someone else

The feature the room model exists for, and the one most likely to be under-built: when something asks for your input, you are not limited to answering the worker that asked . You can put the question to anyone in the room, or bring someone in specifically to answer it.

Desktop · redirecting a question at a gate

aer-flow claude agy +

agy · reviewing The picker path is not the only entry point — the CLI still registers a room only on success.

Needs you ⋯ Apply agy's correction before continuing?
Apply Skip Ask someone…

Ask about this

claude · in this room

agy · in this room

Bring in

agy

a second claude

Include

✓ agy's review

✓ the diff

claude · answering your question agy is right that the CLI path exists, but it registers through the same call — so the correction is unnecessary here and would double-register.

Needs you · still waiting Apply agy's correction before continuing?
Apply Skip Ask someone…

Reply… ⏎

Asking a question does not answer the gate. The gate stays open and visible underneath, because consulting is not deciding — this is the single most important behaviour on this screen. You can ask three workers and still not have applied anything.

You choose what the asked worker sees. The turn that raised the question and its attachments are included by default; you can add or drop. This is what makes cross-examination real rather than a game of telephone — the second opinion is formed on the same evidence, not on a summary.

Bringing someone in is the same gesture as asking. A worker who is not yet in the room appears in the same menu; picking one adds them and puts the question to them in one action. That is how a one-worker room becomes a two-worker room in practice — not by a separate "manage participants" surface.

This is a person's explicit choice, never inference. Nothing in the product reads the conversation to decide who should answer — a rule the engine holds absolutely, and this design honours by making routing a control you operate.

Removing a worker mid-room gets its own control, in the room header rather than a separate
surface: it runs two guardrails in sequence — stopping any in-flight execution via the real
`InFlightExecutionRegistry.RequestCancellationAsync`, then refusing (with a clear reason, never a
silent repair) if an active workflow step still depends on that worker, or if that worker is the
room's current orchestrator ([0032](../decisions/0032-room-orchestrator-is-mandatory.md)). See
[02-screens.md](02-screens.md#the-calls-made-here) for the full sequence.

### Code and executions

Most of what a worker says is not prose. Three kinds of block carry almost all of it — written code, a change to a file, and a command that ran — and each earns different treatment.

Desktop · the three block types

claude Both lists now refresh through one call, and a room registers when it is created.
▸ C# · RefreshRecordListsAsync · 6 lines Copy

▸ MainWindow.axaml.cs +14 −3 · Review

▸ pixi run build ✓ 41s

▾ pixi run test ✕ exit 1 · 1m 4s

Failed! - Failed: 1, Passed: 337
Two_spellings_of_one_directory_resolve_to_the_same_row
Expected: "Finished" Actual: "Idle"

Reply… ⏎

Code sits on its own surface, never at a larger size. Separation comes from the ground it sits on — scaling code up overflows a phone's width and forces sideways scrolling, which is worse than the problem it solves.

A successful command collapses to one line; a failed one opens itself. Nobody reads the output of a build that passed, and everybody needs the output of one that didn't. Exit status and duration are always visible even when collapsed, because "it ran and took four minutes" is itself information.

A file change is a diff with a name, not a wall of code. Only changed lines and their immediate surroundings; "Review" opens the full file. The header carries the counts, so the size of a change is legible before you read any of it.

Phone · the same three blocks, opened rather than inlined

9:41 ▮▮▮

‹ aer-flow claude + agy

claude Both lists now refresh through one call:
C# · 5 lines Open

private async Task RefreshRecord…

MainWindow.axaml.cs +14 −3

pixi run test ✕ exit 1

Reply… ↑

9:41 ▮▮▮

‹ MainWindow.axaml.cs +14 −3 · by claude

- await RefreshHomeAsync(ct);
+ await RefreshRecordListsAsync(ct);

if (_session.LastLoadSucceeded)
{
+ await RegisterRoomAsync(path);
}

Looks right Ask about this

9:41 ▮▮▮

‹ pixi run test ✕ exit 1 · 1m 4s

Failed! - Failed: 1, Passed: 337

Two_spellings_of_one_directory_
resolve_to_the_same_row

Expected: "Finished"
Actual: "Idle"

at TasksViewModelTests.cs:257

Ask claude to fix Copy

On a phone a block is a door, not a panel. Inline it shows its header and at most a couple of lines; tapping opens it full-screen where it can be read and scrolled horizontally without fighting the conversation. This is the one place the two surfaces genuinely differ in structure rather than size, and it is why the phone needs its own screens for diff and output.

An opened block still offers the next action. A diff offers "Looks right" and "Ask about this"; a failed run offers "Ask claude to fix". Reaching a dead end where all you can do is go back is the failure mode to avoid.

### Artifacts, and moving them between vendors

Workers produce files — a plan, a review, a patch. Those files are the product's real currency, and they must be vendor-neutral : anything one worker made, any other worker can be handed.

Desktop · an artifact produced, then handed to a different vendor

aer-flow claude agy +

claude Plan written.
◆ plan.md 4.1 KB · by claude · 2m ago

you Have agy review the plan.
◆ plan.md attached

plan.md

Open

Compare with version 1

Send to a worker…

Save a copy…

3 versions

v3 · claude · 2m ago

v2 · agy · 1h ago

agy Reviewed. Two problems in step 4.
◆ plan.md v4 · edited by agy

plan.md · what agy changed +6 −2

Reply… ⏎

An artifact is a file on disk, not a message in a vendor's transcript. That is what makes it portable: claude writes it, agy edits it, agy reads it, and none of them needs the others' conversation format. The engine already stores artifacts this way per execution — the design work is making them visible objects you can pick up and hand over.

Versions are first-class and attributed. Every artifact carries who produced each version and when, so "what did agy actually change" is one click and a diff rather than a reread. Handing a file to a second vendor is worthless if you cannot see what came back different.

Attaching is explicit and visible. The message that carries a file shows it as an attachment before it is sent, because "which version of the plan did agy actually see" is exactly the question that becomes unanswerable if this is implicit.

On the phone an artifact opens as its own screen , with the same version list and the same "send to a worker" action — reviewing on a phone is realistic, editing is not.

### The shape editor on a phone

Choosing an ordered list over a canvas paid off immediately: a list is entirely at home on a phone. This is the affordance that was going to be impossible, and now is not.

Phone · editing a template

9:41 ▮▮▮

‹ draft → review 3 steps · 1 gate

draft claude ⣿

+ step

review agy ⣿

+ step

apply claude ⣿

Preview shape Done

9:41 ▮▮▮

‹ review Step 2 of 3

Name

review

Who runs it

agy ›

Before this step

Ask me first on

Delete step Done

Drag to reorder, tap to edit, one step per screen. A phone is actually better than a mouse at reordering a list, so the primary structural gesture is the one the device is best at. Editing a step pushes its own screen rather than cramming four fields into a row.

Preview is a screen you ask for, not a permanent panel. On desktop the graph sits beside the list because there is room; on a phone it is a button, because the list already tells you the order and the graph is a confirmation rather than a working surface.

### States everything must handle

The last rebuild drifted because these were decided per screen, late, by whoever was implementing. Deciding them once, here, is the actual drift protection.

<!-- generated: interaction-states (pixi run gen-states; edits here are overwritten) -->

State | What the surface does |

Empty | Says what would be here and offers the one action that creates it. Never a bare "no items". *(M25 design pass, 2026-07-24)* |

Loading | Keeps the previous content and marks it stale rather than blanking. A list that empties itself while refreshing reads as data loss. *(M25 design pass, 2026-07-24; staleness rule 0018)* |

Disconnected | The phone says so at the top, keeps showing the last known rooms marked stale, and queues what you type. Work continues on the computer regardless — that is the point of the daemon owning the run. *(M25 design pass, 2026-07-24)* |

Worker missing | A room whose vendor CLI is gone says which one and how to fix it. It is not a failure of the room. *(M25 design pass, 2026-07-24)* |

Folder gone | The room is greyed and marked unavailable, never an error dialog, and never silently dropped from the list. *(M25 design pass, 2026-07-24)* |

Cancelled | Reads as cancelled — a distinct state with its own mark, never collapsed into finished. *(Promoted from the #461 defect; second live copy fixed as #976)* |

Failed | Reads as failed, shows the error text in place, and offers the failing worker as the first way to fix it. *(M25 design pass, 2026-07-24; #617 deepens it)* |

Archived | Out of the default list, still searchable, restorable in one action. *(M25 design pass, 2026-07-24)* |

Long output | Truncated with an explicit "showing first N lines" and a way to see all of it. Never silently cut. *(M25 design pass, 2026-07-24)* |

Reduced motion | Every animated state degrades to a correct still frame — the working mark is a spinner's static frame by design, not an absence. *(M25 design pass, 2026-07-24)* |

Gate unverified | A worker whose permission mechanism could not be confirmed working at start says so before any tool runs, rather than silently rendering a gate that might never fire — a broken hook or a disabled callback both look exactly like a working one otherwise. *(0029, added 2026-07-25)* |

Waiting on another room's lock | Reads as a wait, never as an error and never as generic working: names the room that holds this folder, linked, so the choice — wait, or go there — is discoverable. Opening a second room on a folder that already has one warns first; legal, but a choice made knowingly. *(#480 (grouped under #752), ratified 2026-08-04 on #495)* |

Dormant | The room stopped machine turns after repeated turns that committed nothing, and says so in the transcript with the reason and the wake control. A message to a dormant room is answered with this state — waking is your explicit action, never a side effect of asking how it's going. *(#778 turn-throttle addendum, ratified 2026-08-04 on #495)* |

Out of plan | Displays quota/subscription exhaustion with its reset time when known ("Out of plan — resumes {local time}") or an explicit unknown ("Out of plan — reset unknown"), distinct from failure. *(0026 decision record, 2026-07-25 (#1123 rooms, #1185 chat))* |

Unexpected app error | The app stays up and says what went wrong in the failure text already on screen — never a dialog, never a silent disappearance, and never a window that vanishes mid-task. The detail is kept on disk so the failure is still there to read afterwards. *(#1176, surfaced by #1175's second reader; decided 2026-08-13)* |

<!-- /generated: interaction-states -->

Two of these are the defects from the last manual run , promoted from bugs to rules: cancelled reading as finished, and a room that existed but appeared nowhere. Writing them here is what stops them being rediscovered.

> **Added 2026-07-25.** A third promoted rule, from
> [0029](../decisions/0029-the-gate-is-three-mechanisms.md): a permission gate is enforced by
> different mechanisms depending on which tool population is at risk, at least one of which fails
> silently when broken. AER verifies its own gate at every worker's start rather than assuming
> configuration took effect — the row above is that verification surfacing as a state, not a new
> mechanism of its own.

> **Added 2026-08-04.** Two more states from the resident-room design, ratified on
> [#495](https://github.com/aer-works/baton/issues/495): a room serialised behind another room's
> turn lock (#480 — the engine behaviour already exists and is verified; the state makes the wait
> visible instead of indistinguishable from slow), and the turn-throttle circuit breaker's visible
> face (#778 — the throttle numbers themselves live in the room's `turn-throttles.json`, never
> restated here). That makes the recorded inventory **thirteen**.

> **#616, same day.** The table above is now a *rendering* of the authoritative register,
> [`design/interaction-states.json`](../../design/interaction-states.json), regenerated by
> `pixi run gen-states` and checked by `audit-completeness` — edit the register, not the table.
> What else the register generates and enforces is recorded once, in
> [0020's amendment](../decisions/0020-one-state-machine.md).

### The inventory

Every surface the product needs, and which views have it. Anything not on this list is out of scope until it is added here — that is the point of writing it down.

Surface | Desktop | Phone | Notes |

First run | folder + readiness | pairing only | The phone has no folders and no CLIs until it is paired. |

Room list | permanent sidebar | root screen | Same ordering rule: most recently active first, stable under refresh. |

Needs you | filter | tab | A filter over the same list, never a separate store of state. |

Room | centre pane | pushed screen | The conversation, with workers as chips in the header. |

Gate | inline turn | inline turn | Plus "ask someone" on both. |

Diff | inline | own screen | The main structural divergence between the views. |

Command output | inline, collapsed | own screen | Collapsed when it succeeded, open when it failed. |

Artifact | attachment + viewer | own screen | Versions and attribution on both. |

Template list | yes | yes | Starting a room from one works on both. |

Shape editor | list + preview | list, preview on demand | Reordering is better on a phone than with a mouse. |

Settings | three groups | workers read-only | The phone cannot install a CLI, so it reports rather than fixes. |

Pairing | shows the code | enters the code | Two halves of one flow. |

Archive | bulk, from the list | per room | Bulk management stays a desktop affordance. |

Notifications | later | inform only | Desktop notifications are worth having but are not in this pass. |

Spend controls | inline in the control surface | counter when informative, sheet to edit | Machine-turn throttles: values and live usage visible where the room is. The numbers are the room file's own, edited in place — never a second copy. |

Search | not yet | not yet | Deliberately unscoped. Revisit once there is enough history to need it. |

### The calls made here

Queue, don't block Typing while a worker runs queues the message visibly , with interrupt and remove beside it. Nothing in the product is ever modal.

Consulting ≠ deciding Asking another worker about a gate leaves the gate open. You can ask three and still have decided nothing — the single most important behaviour in the room model.

Routing is a control You choose who answers, and what they see. The product never reads the conversation to decide who should respond.

Adding = asking Bringing a new worker in is the same gesture as asking a question. No separate worker-management surface.

Removing gets a control (added 2026-07-25, M27) Unlike adding, removing a worker mid-room is not inferred from asking — it is an explicit room-header action, gated by an in-flight-execution stop and a DAG dependency check that refuses rather than silently repairing. See 02-screens.md.

Artifacts are files Vendor-neutral, versioned, attributed, explicitly attached. What one worker made, any other can be handed.

Success collapses A command that passed shows one line; one that failed opens itself. Status and duration stay visible either way.

Blocks are doors on a phone Diffs and output get their own screens , and each still offers the next action rather than only "back".

Stale, not blank Refreshing never empties a list. Previous content stays and is marked stale — blanking reads as data loss.

A wait names its holder (added 2026-08-04) Waiting on another room's turn lock is its own state, never generic working: the holder is named and linked, so "go close the other room" is discoverable rather than folklore.

Dormancy answers, it never resumes (added 2026-08-04) A message to a dormant room is answered by the product with the dormancy state and the wake control. Waking the room is your explicit action — a stuck loop can never resume spending because you asked how it's going.

Depth pass complete. With the definition and the screen set, this is intended to be enough that implementation invents nothing — anything genuinely missing should be added here first rather than decided at the keyboard.
