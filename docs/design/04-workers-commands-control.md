# Workers, models, commands and control

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> The 2026-07-24 material is unchanged from the artifact of the same name; where a decision
> record and this document differ, **the record wins** — it is the reviewed extraction. Kept
> because the records deliberately capture decisions, and this also holds screen
> specifications, delights and demonstration criteria that are not decision-shaped and would
> otherwise exist nowhere. See [../README.md](../README.md#kept-current-not-frozen-added-2026-07-25)
> for why this corpus is maintained in place rather than staying a closed snapshot.

---

Baton — workers, commands, control

Interaction design · workers and control

## Workers, commands, control

The parts the first three passes left out: who exactly is answering, what you can ask of them, what they are allowed to do without asking, and what it is costing.

Three kinds of "needs you" Permissions Vendors and models
Commands and skills Files Context and cost
Composing The calls

### Three kinds of "needs you"

The earlier passes had one concept — a gate. That was wrong, and it is the most substantial correction in this document. Three different things stop work and want you , they carry different risk, and they deserve different affordances.

Kind | Example | What it needs from the design |

Permission | claude wants to run rm -rf build/ | Fast, safety-critical, and answered many times a day — so it needs scoping ("allow this command in this room") or it becomes a click-through reflex, which is worse than no prompt at all. |

Decision | Apply agy's correction, or keep claude's version? | Wants deliberation. This is the one where "ask someone else" matters, and where the argument being on screen is the whole point. |

Action | Review this diff before it is applied | A task for you rather than a question. Can often be done later; should not feel like a blocking alarm. |

Desktop · the "needs you" filter, grouped by kind

Needs you 4 items · 3 rooms

Permissions · 1

aer-flow · claude Run rm -rf build/ in the project folder?
Allow once Always allow in this room Deny

Decisions · 2

aer-flow · agy disagreed Apply agy's correction before continuing?
Apply Skip Ask someone…

payments-api · claude Which auth provider should the service wire up?
Answer… Ask someone…

Actions · 1

docs-sweep · claude Review 14 changed files before they are applied.
Review Later

Grouping by kind rather than by room is the point. Four items across three rooms sort by what they ask of you, not by where they came from — permissions are quick and safety-critical, decisions want thought, actions can wait. A flat list mixes a two-second yes/no with a five-minute judgement call and makes both feel the same.

Only one of the three is genuinely urgent. A permission blocks a worker that is otherwise ready to go, so it sits first. An action often blocks nothing at all — offering "Later" on it is honest, and refusing to treat it as an alarm is what keeps the list credible.

That's about ordering and how insistent a gate looks, not about whether it gets an OS push notification at all — those are two different questions. [0030](../decisions/0030-aer-is-its-own-notifier.md) emits a push from the same durable write that records any pause, regardless of kind; there's no carve-out for actions in that mechanism, and this record doesn't add one. An action still gets pushed — it just doesn't get to *jump the queue* or read as an alarm once you open the app. "Genuinely urgent" describes how it's presented, not whether you're told.

### Permissions, in detail

The hole in every previous pass, and a safety surface rather than a convenience one. The design problem is not showing the prompt — it is stopping the prompt from becoming a reflex.

Desktop · a permission request, and its scope

aer-flow claude +

claude The stale build output is causing the test failure. I need to clear it.

Permission · claude wants to run a command
rm -rf build/ in ~/source/repos/aer/aer-flow

Allow once Allow rm here Deny or press y / n

Allow scope

Just this once y

Any rm in this room

Any command in this room

Any rm in any room

Never

Deny once n

Always deny rm

you · denied Denied rm -rf build/ . claude was told and is continuing.

Reply… ⏎

A denial is a real answer, not a cancel. The worker is told it was refused and carries on with that knowledge — it does not silently retry, and it does not die. That is the difference between a permission system and an obstacle course.

Scope is the whole design. "Allow once" for everything trains people to click through; "allow everything forever" is not a permission system. The middle rungs — this command in this room, any command in this room — are what make it survivable, and they must be visible at the moment of asking rather than buried in settings.

Standing permissions are visible and revocable in Settings , listed per room, because a permission you granted three weeks ago and cannot find is indistinguishable from no permission system at all.

Keyboard-first. These are answered constantly, so y and n work without reaching for a mouse — and the destructive option is never the one that happens on a stray Enter.

### Vendors and models

"Any subscription's model in the room" means two levels of choice, not one. A vendor is who you have a subscription with ; a model is which brain you are spending it on .

Desktop · picking a worker

aer-flow claude agy + Add worker

claude · signed in

Opus 4.8 deep work

Sonnet 5 balanced

Haiku 4.5 fast

agy · signed in

Gemini 3 Pro

Gemini 3 Flash fast

Codex

not installed — how to add

you Add a fast reviewer.

claude · opus 4.8 Two workers can share a vendor — a second claude on a cheaper model is a normal thing to want for review.

Reply… ⏎

The model is shown on the chip, always. Which model answered is not a detail — it changes what an answer is worth, and a room where one worker is on a fast model and another on a deep one is the normal case, not an edge case.

The model still isn't a detail — that claim stands. Where it lives is the room-header chip
showing a bare worker's vendor alone, or `claude · 2 skills` if any are attached (see
[02-screens.md](02-screens.md#the-calls-made-here)), with the model one tap away in the same
picker shown above. Painting it onto the compact label at all times is what made a worker's chip
unreadable once skills and a standing permission were added to the same axes; the picker — not the
label — is where the model lives now.

Two workers may share a vendor. "claude on opus" and "claude on haiku" are two distinct participants in the room. This falls out of separating vendor from model, and it is what makes cheap-reviewer / expensive-author patterns possible on one subscription.

A template names roles and models, and whatever skills are attached to that step's worker, per
[02-screens.md](02-screens.md#the-calls-made-here) — not just vendors. "Draft on a deep model,
review on a fast one" is exactly the reusable shape templates exist to capture. Each step in the
shape editor gets the same two-level picker.

Never a raw model identifier. The picker says what a model is for — deep work, balanced, fast — because nobody should need to know which string the vendor's CLI wants this month.

### Commands and skills

A drop-in replacement has to carry these, and they raise a question single-agent tools never face: in a room with two workers, whose commands does / show?

Desktop · the command palette in a two-worker room

aer-flow claude agy

Room

/add — bring in a worker

/shape — see this room's shape

/files — what this room touched

/usage — what it has cost

claude

/review skill

/compact command

agy

/explain skill

Everyone

/ask-all — put this to every worker

you /ask-all does this migration look safe to run on production?

claude No — step 4 drops a column before the backfill completes.

agy Agreed on step 4. Also the index rebuild will lock writes for minutes.

/ for commands · @ for files ⏎

Commands are namespaced by who owns them. Room commands act on the room and always work. A vendor's own commands and skills are grouped under that vendor and go to that worker — so /compact is unambiguous even when two workers both have one, and nobody has to learn which tool a command came from.

/ask-all is the broadcast gesture , and it is the cheapest way to get value from a multi-worker room: one question, every worker answers, you compare. It is deliberately a command rather than a mode — you drop into it for one message and out again.

Skills and commands are shown together and marked , because the distinction is the vendor's, not the user's. What matters to a person is "what can I type here", not which mechanism implements it.

Room commands are the discoverable surface for everything else in this document. /files , /usage and /shape open the panels below — which means those surfaces have a keyboard path and do not depend on finding an icon.

Attached skills don't change this grouping. The command group above stays headed by the worker's
vendor — `claude`, `agy` — the same primary-identity rule the room-header chip follows (see
[02-screens.md](02-screens.md#the-calls-made-here)). A skill's own commands, if it has any, are
still that vendor's commands, grouped one level up.

### Files — the project's, and the room's

You asked whether AER's own documents should be abstracted away. The mechanism should be; the documents should not. Nobody should ever see an execution directory — but a plan you can read, version and hand to another vendor is the work itself.

Desktop · everything this room touched

aer-flow · files 6 touched · 2 working documents

src/Aer.Ui/MainWindow.axaml.cs claude · 2m ago in your project

src/Aer.Ui.Core/RoomsViewModel.cs claude · 2m ago in your project

tests/…/RoomsViewModelTests.cs claude · 5m ago in your project

plan.md claude → agy · v4 working document

review-notes.md agy · 1h ago working document

plan.md · v4 Written by claude, edited by agy 1h ago. Not part of your project — it lives with this room.
Open Compare v3 → v4 Send to a worker… Save into the project…

Reply… ⏎

One list, one distinction that actually matters: is this in your project, or not? That difference is real and consequential — one is in your git history and the other is not. "Which folder AER stashed it in" never appears anywhere, and neither does an execution number.

Both kinds get the same affordances — versions, attribution, diffs, send-to-a-worker. That uniformity is what makes cross-vendor work feel like one product rather than a pipeline: handing agy a source file and handing it a plan are the same gesture.

"Save into the project" is the one-way door, made explicit. A working document becomes a real file only when you say so. That is the moment something enters your repository, and it should be a decision rather than a side effect.

### Context and cost

Two questions with one home: how much room is left in this conversation , and what am I spending across vendors . Both are invisible today and both bite.

Desktop · /usage

aer-flow · usage this room all rooms

claude · opus 4.8

128k

of 200k context · 64%

agy · gemini 3 pro

41k

of 1M context · 4%

Claude plan

72%

of this week's limit

Turns today

34

across 5 rooms

claude · context filling up This room is at 64% of opus's context. Older turns will be summarised to make space.
Summarise now Start a fresh room from here Leave it

Reply… ⏎

Context is per worker, not per room — that is the fact a single-agent tool never has to express. Two workers in one room have completely different amounts of headroom, and a room can be comfortable for one and nearly full for the other.

Subscription limits are the number people actually worry about. Not dollars — this product runs on plans, so "72% of this week" is the honest unit, and it is the one that changes behaviour.

Running out of context is offered as a choice before it becomes an event. Summarise, branch a fresh room carrying the conclusion, or do nothing — announced at a threshold rather than discovered when quality quietly degrades.

### Composing a message

Small surface, disproportionately used. Four things it must do beyond accepting text.

you · editing Check @TasksViewModel.cs — the ordering there disagrees with the daemon.
Save and resend Cancel resending discards the replies below

/ commands · @ files · ⇧⏎ newline ⏎ send

@ mention a file

TasksViewModel.cs src/

RoomProjectionLoader.cs src/

plan.md working

Also

Paste an image

Attach a file…

@ mentions any file the room can see — project files and working documents in one picker, same as the files list. Mentioning is how you point without pasting, and it keeps the message readable.

Editing a sent message is allowed, and honest about the cost. Resending discards the replies that came after it, because they were answers to something that no longer exists — said plainly at the moment of editing rather than discovered afterwards.

Images paste directly. A screenshot of a broken UI is often the fastest possible bug report, and this product's own recent history is the argument for it.

Enter sends, shift-enter breaks a line. Worth stating because getting it backwards is a daily irritation, and because the phone must do the same thing.

### The calls made here

Three kinds, not one gate Permission, decision, action. Different risk, different urgency, different affordances — and the "needs you" list groups by kind rather than by room.

Scoped permissions Allow once / this command here / anything here , visible at the moment of asking. Unscoped prompts become a click-through reflex, which is worse than no prompt.

Denial is an answer A refused worker is told and continues. It does not silently retry and does not die.

Model on the chip Vendor and model are separate choices, both always visible. Two workers can share a vendor on different models — that is a normal room, not an edge case.

Model in the picker, not the label Amends the call above: the compact chip shows a bare worker's vendor, or `vendor · N skills` if any are attached — the model itself moved into the picker one tap away, for the same reason model/effort/permission all live in a worker's popover rather than its chip label. See 02-screens.md.

Effort is the third axis Vendor, model, and how hard it should think — all three on the chip, all three per room, all three in a template. Named by behaviour (quick / standard / careful / exhaustive), never a token budget or a vendor's flag.

No slash palette on a phone The same commands become an Actions sheet from the room header. Typing / to discover things is a keyboard idiom that does not survive a touch keyboard.

Purpose, not identifiers Models are offered as deep / balanced / fast. Nobody should need to know this month's model string.

Commands are namespaced Room commands, then each vendor's own. Resolves the two-worker ambiguity that single-agent tools never face.

Broadcast is a command /ask-all puts one question to every worker. A gesture you drop into for one message, not a mode.

Documents stay, plumbing goes One file list; the only distinction is "in your project" or not. Execution directories are never surfaced; working documents are first-class.

Context is per worker Each worker has its own headroom , and running out is offered as a choice before it becomes an event.

Limits, not dollars Spend is shown against the subscription's own limits , because that is the unit this product actually runs on.

Fourth pass. With the definition, the screens and the depth pass, this is intended to be the complete design surface — anything still missing should be added here before it is built.
