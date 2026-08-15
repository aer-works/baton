# Screens — every shape the product needs, both surfaces

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> Four screens for M27 (skill attachment, room orchestration) are appended after **Phone**. This
> is the *source* the decision records were written from; where a record and this document
> differ, **the record wins** — it is the reviewed extraction, not the ruling. See
> [`README.md`](README.md#kept-current-not-frozen-added-2026-07-25-policy-corrected-2026-07-26)
> for the corpus's current-state policy.

---

Baton — screens

Screen design · draft 3 · complete set

## Screens

Every shape the product needs, on both surfaces. Desktop and phone are different views of one thing, not one layout at two widths — where they diverge, the divergence is written down here rather than discovered in code.

First run The daily driver Two workers, a gate
When it fails Starting from a template Drawing a shape
Settings Phone The calls

M27 addition — Skill attachment Skill creation Room-header controls Workflow toggle

Resident-room addition — Spend controls Dormant Waiting on a lock Escalation

### First run

One screen, one action, and the answer to the question that actually breaks first installs: are my CLIs even being found? Onboarding and diagnosis are the same screen because they are the same worry.

Desktop · nothing yet

Baton

▤ ◱ ⚙

Point Baton at a folder

A room is a conversation about one folder. Open one and start talking; add a second worker whenever it is worth it.

Choose a folder… Start from a template

Workers found ✓ claude ✓ agy — agy not installed

The readiness line is the real feature here. Every vendor CLI is authenticated outside this product, so "it can't find claude" is the most likely first failure and the least self-evident. Showing what was detected — and naming what wasn't, without treating it as an error — turns a dead end into a fact. Nothing else is on this screen. No tour, no sample project, no checklist: one sentence explaining what a room is, and the button that makes one.

### Desktop · the daily driver

One room, one worker. This is the screen you look at most, so it has to be boring and fast. Nothing here is a mode you enter.

> **Amendment (2026-08-14, #1204 / umbrella #1196):** A **workflow** room now opens in this screen too,
> not on a separate one — its decisions are answered in the transcript where they were raised, and its
> shape (steps, evidence, lineage, diff) sits beside the transcript rather than instead of it. The
> drawings below are of a chat room, and they are unchanged for that case.
>
> The one thing they do not cover is the composer in a workflow room, and #1196 settled it: **present
> but disabled**, with a sentence saying why — "This room's workers aren't conversational yet." Absent
> was the alternative and it is the wrong one: a composer that vanishes reads as a capability that was
> taken away, where a disabled one reads as a capability that has not arrived. It becomes live when
> 0054's participant and turn identity land and a worker can be addressed. Until then the transcript
> of a workflow room carries the room's own events — pause, decision, failure, cancellation — and not
> the workers' turns, which is thin on purpose and disclosed rather than hidden.

> **Amendment (2026-08-14, #1215 / umbrella #1196):** The room header no longer carries file paths or a
> **Run** button. A workflow file and a bindings file are engine plumbing; the header is the room's.
>
> **What replaced Run**, which is the fork #1196 reserved for an amendment rather than a decision
> record. Run did two jobs. Starting a room was never only its — Author's "Save & Run" and the template
> picker both do it, and both survive. Resuming a room that was running when its process died was its
> alone, and nothing else in the desktop does it: every other caller starts a fresh room, and nothing
> rehydrates a crashed one on startup. So Run could not simply go.
>
> It is now an offer on the stopped room's own transcript — *"This room stopped mid-run — Resume"* —
> the same put-the-offer-on-the-turn move dormancy's Wake and #617's "Try again" already make. A
> finished room gets the matching offer in the same place, *"Run it again"*, which starts a fresh room
> cloned from it and leaves the finished one as it is.
>
> **Auto-resuming a stopped room when it is opened was considered and rejected** (owner, 2026-08-14):
> opening a room would then spend vendor budget nobody asked for, and what is spent is the operator's
> call. Resume is a click.
>
> **A room waiting on a decision gets no such offer.** It is also not running, and it is not stopped —
> its next move is already on screen as the decision itself, and a second offer beside it would be two
> answers to one turn. Note this is why "stopped" cannot be read off the journal: `WorkflowStatus`
> defines `Running` as *"still in flight **or** Flow crashed before recording its outcome"*, so the
> record cannot tell a live room from a dead one. What can is the room's §15 lock, which the OS
> releases the instant its holder exits.
>
> **Stop stays in the header**, present whether or not anything is running — "Stop is always present
> and always distinct from a gate" (`03-interaction-depth.md`). Slice 3 had moved it into the
> collapsible Shape panel, where it was only findable if you had already opened a panel that is closed
> by default.

> **Amendment (2026-08-15, #1224 / umbrella #1196): the room header spans the transcript and the
> shape panel.** It used to be the top row of the transcript column, so opening `Shape` took its
> 460px out of the header's own width and clipped it — `Stop` was the control that vanished first.
> The arrangement was not the flaw; the column was. At the supported 900px floor the nav rail (62),
> the switcher sidebar (260), the shape panel (460) and the transcript's own margins (48) leave that
> column **70px** — far less than the controls alone need — so *every* fix that kept the header
> inside it failed at the floor, including simply ellipsizing the name.
>
> *(The figure in the ruling this amendment records was ~130px; it does not reproduce from the
> dimensions the code defines, and a second reader caught it. The arithmetic above is the measured
> one. It changes nothing about the conclusion — 70px and 130px are both far short — but a number
> nobody can re-derive is how a record goes quietly wrong.)*
>
> Opening `Shape` now takes its width from the transcript and can never take any from the header.
> Inside the header **the room name is the only element that yields** — ellipsis, full name in the
> tooltip — and the controls never shrink, drop, scroll or wrap. One line, one glance, at every
> supported width. The switch's refusal sentence moves to its own caption line beneath the row: it is
> prose of unbounded length, and the only way it fitted inline was a 360px cap the controls beside it
> cannot spare at the floor.
>
> Four alternatives are rejected, not deferred. Wrapping spends the glance permanently. A scroller
> re-adds what #1204 removed *and* puts `Stop` behind a hunt. Dropping controls by priority makes one
> of them absent, and all three candidates are ones that must not be. Narrowing the panel defeats the
> only reason its width is fixed.
>
> **`Stop` is inviolable, and that is not new** — `03-interaction-depth.md` already says it is always
> present and never a hunt. What this records is the clarification: **present means rendered and
> hittable at every supported width; clipped is absent.** That binds every surface carrying `Stop`,
> the phone's room header included.

> **Amendment (2026-08-15, #1240 / umbrella #1196):** The phone carries this card too — before it, a
> finished room on the phone rendered *nothing*, which under 0018 is not calm but silent. One clause
> governs what crosses: **the headline always; a body sentence only where the action it describes
> exists.** The Finished and Cancelled bodies above are captions for *Run it again*, and the phone has
> no run flow to caption; the stopped-mid-run body keeps its first sentence, which states the room's
> condition, and drops *"Resume picks it up where it left off."* Printing an offer this surface cannot
> honor would be worse than saying less. This is a subset, not a second vocabulary (0002): no state is
> given different *words* here, only fewer.
>
> The lock reading the paragraph above turns on is a local file, so the derived status is put on the
> wire for a remote client rather than re-derived there — a client deciding a room's state for itself
> is how the switcher and the room screen came to disagree (#976/#1219). An absent status means the
> daemon could not say, and renders as no card: never as *finished*.
>
> A **failed** room still gets no terminal card, on either surface — #617's failed-step banner says
> what broke and offers the worker that broke it, which is strictly more. The phone did not have that
> banner on the day of this amendment (#1245), and a failed room there was blank; the amendment below
> is where that half lands.

> **Amendment (2026-08-15, #1245 / umbrella #1196):** The phone's half of #617's failed-step banner.
> It is filed and amended separately from the card above because it answers a different question —
> that one says a room ended, this one says a step broke — and the two can never appear together:
> `HomeViewModel.DeriveStatus` reaches Failed before any status the terminal card speaks for whenever
> a step has failed for a reason other than exhaustion.
>
> The clause governing what crosses is the one above, applied to a banner rather than a card, and it
> subtracts more here. The desktop banner offers **three** things, and they do not all fall for the
> same reason. *Try again* and *Ask the worker to fix it* both need a run the phone cannot start, so
> the clause above disposes of them. ***Show full output* does not** — it previews an artifact already
> written, and the phone has the very mechanism it would need, since the paused-step card already
> fetches an output file the same way. It is left out because this amendment is about saying what
> broke, not about reading the wreckage, and one is worth shipping without the other; it is filed as
> **#1254** rather than justified away.
>
> What crosses is the naming — which step, which worker, why — and the stderr excerpt, kept as its own
> block rather than folded into the sentence, because the separator between them is a format the
> engine writes and a reader who saw one run-on string could not tell which half was the engine's
> account and which the worker's output.
>
> **An out-of-plan step is not drawn as failed**, on this surface for the same reason as on the
> desktop (0026, and #1116's must-fix): it is waiting on quota, not broken, and the calm word is the
> entire point of saying "Out of plan" at all.
>
> Where the shape names no worker for a failed step the clause is dropped rather than filled with the
> step id — a name the person would go looking for and not find is worse than one fact fewer.

Desktop · one worker

Baton

▤ ◱ ⚙

Rooms + New

◔ aer-flow Working · claude

◗ payments-api Needs you

✓ docs-sweep Finished · 1h

— spike-cache Cancelled · 1d

aer-flow claude + Add worker

you Why does a new room not show up in the list?

claude Two causes. The list only refreshed at startup, and a room was registered only when its first run returned 2xx — so a refused run left a real folder nothing knew about.

you Fix both.

claude · working Editing MainWindow.axaml.cs …

Reply… ⏎

The sidebar is a presence list, not a file browser. Name, state mark, and what that worker is doing right now — the three things that let you decide whether to switch. It is always there, so a room you are not watching is never invisible. "+ Add worker" is a control in the header , not a new object to create: that is what keeps "room" a single noun.

### Desktop · two workers and a gate

The escalation. You added a reviewer, it disagreed, and now something needs you. Note what did not happen: you did not move to a different screen.

Desktop · two workers · decision inline

Baton

▤ ◱ ⚙

Rooms + New

◗ aer-flow Needs you · 2 workers

◔ payments-api Working · 4m

✕ migration Failed · 3h

aer-flow claude agy +

claude Patch ready: refresh both lists through one call, and register a room when it is created.

agy · reviewing The refresh is right. But the picker path is not the only entry point — the CLI still registers only on success.

Needs you Apply agy's correction before continuing?
Apply Skip Ask claude to respond

Reply… ⏎

Shape Hide

draft · claude · 2 skills

↓

review · agy · 1 skill

↓

gate · you

↓

apply

A gate is answered where it was raised. It renders as a turn in the conversation, because that is where the context is — the argument you are ruling on is directly above it. It also appears on the phone and in the "needs you" filter; same object, several entry points, never several copies of the state. The shape panel is optional , showing where this room is in the template it was started from — dismissible, and absent entirely for a room you just started by talking.

A workflow step's binding can be a bare vendor name or a vendor with skills attached (shown as `vendor · N skills`, the same vendor-first rule the room-header chip follows) — the same duality as a room's worker chip. This document shows the skills-attached case across template and shape views for clarity, though bare vendor bindings remain fully valid.

### When it fails

A failure is a state, not an absence. The rule this screen exists to enforce: a failed room reads as failed everywhere, and the reason is on screen rather than behind a drill-in.

Desktop · failed room

Baton

▤ ◱ ⚙

Rooms + New

✕ migration Failed · 3h

◔ aer-flow Working · claude

migration claude

you Run the schema migration.

Failed · claude · 3h ago The worker exited before finishing.
migrate: connect ECONNREFUSED 127.0.0.1:5432
at TCPConnectWrap.afterConnect

Try again Ask claude to fix it Show full output

Reply… ⏎

The error text is the content, not a detail. A failure that says only "failed" forces a hunt through logs; the first few lines of what actually broke are almost always enough to know whether it is your problem or the agent's. Full output stays one click away for when it isn't. "Ask claude to fix it" is the interesting affordance — the worker that failed is right there and has the error in context, so the most common next action should not require you to retype the problem.

### Starting from a template

Shaped work has to be about as cheap to start as a bare conversation, or nobody will ever use it. Three fields and a button.

Desktop · new room from a template

Baton · New room

▤ ◱ ⚙

Templates + New

◆ draft → review 2 workers · 1 gate

◆ just talk 1 worker

◆ triage sweep 3 workers

draft → review Edit shape

Folder

~/source/repos/aer/aer-flow    Choose…

Who runs it

draft · claude · 2 skills     review · agy · 1 skill

Start room Save as my default

A template names the shape and the roles. It does not name the folder — that is chosen per room.

"Edit shape" is the only door to the editor — and the editor edits a template, never a running room. That separation is what stops the graph creeping back into the daily path. A template deliberately does not remember a folder, so one shape serves every project.

**Amendment (2026-08-14, #1222 / umbrella #1196):** Typing a workflow file's path into Home's room box used to render that template's shape full width, read-only, under a "not a running room" banner. That rendering is gone, and this is a design call, not a consequence of the passages above — "Edit shape" being the only door to the editor governs the editor, and this route never was one. It goes because it was a third place a template's graph appeared, reachable only by putting a file where a directory belongs, and Author already shows the same graph and can edit it. A file path in the room box now gets a plain correction pointing at Author. A mistake deserves a sentence, not a screen.

### Drawing a shape

The one place the product asks you to think structurally. It gets the strongest opinion in this document: it is not a freeform canvas.

Desktop · template editor

Baton · draft → review

▤ ◱ ⚙

Templates + New

◆ draft → review editing

◆ just talk 1 worker

draft → review Done

draft claude · 2 skills ask me first ○

+ step

review agy · 1 skill ask me first ●

+ step

apply claude · 2 skills ask me first ○

+ step

By default each step runs after the one above it. Turn on "ask me first" to put a gate before a step;
name a different blocker to fan a step out or in.

Preview

draft · claude · 2 skills

↓

gate · you

↓

review · agy · 1 skill

↓

apply · claude · 2 skills

A list that renders as a graph, not a canvas you drag on. Freeform node editors are the reason visual workflow tools feel like work: you spend your attention on layout — arranging boxes, routing edges — rather than on the actual decision, which is who does what, in what order, and where do I want a say. A vertical list of steps expresses every shape this product realistically needs, is keyboard-navigable, diffs cleanly in version control, and cannot produce an unreadable tangle.

A gate is a property of a step, not a node you add. "Ask me first" is the entire mental model for human oversight — one toggle, in the place you are already looking. That is also what makes the shape readable at a glance: the gates are the highlighted rows.

**Corrected — fan-out is not rare, and it needs no second affordance.** This originally called
genuinely parallel fan-out an edge case worth deferring behind a later gesture. [0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md)
overrides that: naming a step's blocker is already a list operation, the default (blocked by the step
above) keeps a linear shape free, and the engine already runs anything whose blockers are satisfied.

### Settings

Three groups, one screen, no tabs. Settings should be somewhere you visit rarely and leave quickly.

Desktop · settings

Baton · Settings

▤ ◱ ⚙

Workers

claude ✓ found · signed in

agy ✓ found · signed in

agy not installed · how to add

AER runs whichever CLI is already signed in on this machine. It never stores keys.

Your phone

Pixel 8 paired · last seen 2m ago   Unpair

Pair another device Show code

Appearance

Theme Light Dark System

Density Comfortable Compact

"Workers" is the same information as the first-run readiness line , in the place you would go looking for it later — one source, two contexts, so a CLI that stops working has an obvious home. A missing worker offers a way to fix it rather than only reporting its absence. The line about never storing keys sits here because this is where people expect to be asked for one, and the answer is that they never will be.

### Phone

> **Amendment (2026-08-13, #1149):** Shipped pairing reality diverges deliberately from the drawing below — QR is primary, the code field is free-text, and a Host field exists that the original drawing omitted; recorded as accepted 2026-08-12 (owner decision), tracked by #1149. Note that the "Settings → Your phone" copy divergence is a code defect being fixed separately rather than an intended spec amendment.

Same product, held differently. Rooms is the root here as on the desktop — you are visiting your work, not working a queue.

Phone · first run · rooms · a gate · a notification

9:41 ▮▮▮

Connect First run

Open Baton on your computer, go to Settings → Your phone, and enter the code it shows.

4 7 2 · · ·

Codes expire after a minute.

Scan a QR instead

9:41 ▮▮▮

Rooms 2 need you · 1 running

◗ aer-flow Needs you · claude + agy

◗ payments-api Needs you · schema change

◔ docs-sweep Working · 4m

✕ migration Failed · 3h

— spike-cache Cancelled · 1d

Rooms Needs you Settings

9:41 ▮▮▮

‹ aer-flow claude + agy

claude Patch ready: refresh both lists through one call.

agy The CLI entry point still registers only on success.

Needs you Apply agy's correction?
Apply Skip

Reply… ↑

9:41 ▮▮▮

Locked Notification

Baton · aer-flow agy corrected claude's patch — a decision is waiting.
Open

Baton · migration Failed — the worker exited before finishing.

A notification says enough to judge whether it is worth opening, and never decides anything.

The phone's first run is pairing, and nothing else. It has no folders of its own and no CLIs installed, so until it is connected to a computer there is genuinely nothing it can do — pretending otherwise with an empty rooms list would be worse than saying so. Notifications inform, they never decide: one tap opens the gate beside the argument you are ruling on, because approving an agent's work from a lock screen is one mis-tap from approving something you never read. Template authoring is out of scope for the phone's *earliest* runs — the pairing/chat-only milestones before shapes exist as a capability at all, not a standing exclusion; see [0041](../decisions/0041-phone-authoring-lands-with-shapes-not-after.md) for exactly when it stops being out of scope. The step-list model above is what makes a small-screen editor tractable where a canvas would not have been.

### M27 addition — screens for skill attachment and room orchestration

Everything below this line is new: skill attachment and the room orchestrator, decided in the M27
design pass. A worker attaches Skills directly — there is no separate named-preset object
mediating it ([0033](../decisions/0033-skills-attach-directly-no-persona.md)). A matching mockup
lives in [`mockups/02-screens.html`](mockups/02-screens.html).

**A bare worker chip — just a vendor name, exactly as drawn in "The daily driver" and "Two workers,
a gate" above — stays completely valid.** Attaching a skill is optional and additive, not a
replacement for the chip; adding a worker with nothing attached looks and works exactly as those
earlier screens already show. What follows describes the skills-attached case specifically, and
deliberately does **not** repeat model tier and effort in the chip's visible label, and keeps the
worker's vendor identity as the primary label: **`claude · 2 skills`, never a skill's name standing
in for the worker.** "Worker" stays the one noun a person has to track in the room header — attached
skills qualify what that worker can do without becoming a second identity competing with it. The
raw axes (model tier, effort, permissions) live in the popover below, one tap away, not duplicated in the
label.

### Skill attachment on the worker chip

Today's worker chip (0017) is three dependent dropdowns: vendor gates model gates effort. Attaching
a skill sets nothing on those three — it adds a capability alongside them, without destroying the
underlying worker chip. The chip keeps showing the vendor first (`claude`, `agy`) and adds a count
of attached skills as a short qualifier, so the worker is always identifiable at a glance and the
count reads as what's configured on it, not as a replacement for it.

Desktop · worker chip skill-attach popover

```
Baton · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 claude · 2 skills ▼]  [+ Add worker]
                      ┌────────────────────────────────────────────────────────┐
                      │ Attached Skills                                        │
                      │ [✓] Code & security review                             │
                      │ [✓] Commit message style                               │
                      │ [ ] Thorough test coverage                             │
                      │ [ ] Quick reconnaissance                               │
                      │                                                        │
                      │ + Create new skill…                                    │
                      │                                                        │
                      │ Vendor     [ claude                                  ▼]│
                      │ Model tier [ balanced                                ▼]│
                      │ Effort     [ careful                                 ▼]│
                      │ Permissions[ Project ∩ Session ∩ Step (Read, Write)  ▼]│
                      └────────────────────────────────────────────────────────┘
```

Phone · skill-attach bottom sheet

```
9:41 ▮▮▮

‹ aer-flow · Workers

Worker 1
[👑 claude · 2 skills ▼]

┌────────────────────────────────────────┐
│ Attach Skills                          │
│                                        │
│ [✓] Code & security review             │
│ [✓] Commit message style               │
│ [ ] Thorough test coverage             │
│ [ ] Quick reconnaissance               │
│                                        │
│ + Create new skill…                    │
└────────────────────────────────────────┘
```

The worker chip shows the vendor, plus a count of attached skills as a qualifier — never a skill's
name in place of the vendor, and never the raw axes repeated next to it. Clicking the chip opens
the popover, where vendor, model tier and effort each get their own row; that's also where model
tier is named by *purpose* (deep/balanced/fast), never a specific version string, per 0023.
Toggling a skill on attaches it immediately — no naming, saving, or preset step involved, because
attaching is not creating an instance of anything; the worker's current set of attached skills *is*
its current configuration.

The skill list — desktop and phone alike — is flat and searchable, not organized by a
model×effort grid: a skill is vendor/model/effort-agnostic content, not a point on that grid
([0033](../decisions/0033-skills-attach-directly-no-persona.md)). It shows what each skill *does*
("code & security review," "quick reconnaissance") so a person picks by need, not by decoding a
coordinate.

There is no modified state to track. Attaching or detaching a skill applies immediately to that
worker's chip in the room — no asterisk, no "reset," no "save as new." The library skill itself is
untouched by attaching or detaching it anywhere; editing a library skill's own instructions is a
separate action, reachable from "Create new skill…" on an existing entry.

On phone, the picker expands as a standard bottom sheet covering the worker list.

### The skill-creation flow

A skill is account-wide the moment it's created ([0031](../decisions/0031-skills-are-account-wide.md))
— there is no private/shared distinction and no promotion step, because there is no second,
narrower place for it to start out living.

Desktop · skill creation drawer (progressive disclosure)

```
Baton · Create Skill
▤ ◱ ⚙

┌──────────────────────────────────────────────────────────────────────────────┐
│ Create Skill                                                               ✕ │
├──────────────────────────────────────────────────────────────────────────────┤
│ 1. Instructions                                                              │
│    [ Review every diff for OWASP-class issues; flag exploit context for     ]│
│    [ each finding before approving.                                         ]│
│                                                                              │
│ 2. Name                                                                      │
│    [ Security Review Standard                                              ]│
│                                                                              │
│ 3. Tool Requirements (optional)                                              │
│    [ ] Run shell commands   [ ] Write files   [✓] Read files                │
│    Checked against the room's actual grant when attached — never widens it. │
│                                                                              │
│                                            [ Cancel ]  [ Save Skill ]        │
└──────────────────────────────────────────────────────────────────────────────┘
```

The flow uses progressive disclosure inside a single side drawer rather than a multi-step wizard
dialog. Multi-step wizards hide full context, break keyboard navigation, and turn simple edits into
step-through obstacle courses.

Step 1 is instructions — nothing else. There's no separate identity/voice field: tone and
personality are just more instructions, so an author who wants a particular voice writes it here
([0033](../decisions/0033-skills-attach-directly-no-persona.md)). There's no model-purpose or
effort step either — a skill doesn't bind to either; vendor, model tier and effort stay independent
worker-chip axes a person sets separately, on any worker, regardless of which skills it carries.

Step 3's declared tool requirements are a request, not a permission. Attaching a skill that wants `Bash`
to a worker in a read-only room fails to attach, with a clear reason — the room's actual effective
permission ([0004](../decisions/0004-permission-scopes.md)) is never silently widened by what a skill
asks for.

"Save Skill" and "Cancel" are a plain create/discard pair, not a safeguard 0028 governs — nothing
about naming and saving a skill grants, applies, overwrites or dismisses anything by itself (tool
requirements are checked at attach time, not at save time). They're drawn with calm, comparable
weight because that's this corpus's general register, not because 0028 specifically requires it
here.

### Room-header controls: reassigning the orchestrator, and adding/removing a worker mid-room

The room orchestrator is a role held by exactly one worker in the room, always
([0032](../decisions/0032-room-orchestrator-is-mandatory.md)) — a room cannot exist without one,
and the current holder cannot be removed directly. Mid-room modifications — reassigning the
orchestrator or removing a worker — must preserve room state integrity and enforce failure safety.

Desktop · room header with orchestrator pin & mid-gate blocked state

```
Baton · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 claude · 2 skills]  [agy · 1 skill]  [+ Add]
                      ┌────────────────────────────────────────────────────────┐
                      │ Room Workers & Orchestrator                            │
                      │                                                        │
                      │ 👑 claude              [ Active Orchestrator ]         │
                      │    agy                 [ Make Orchestrator ]           │
                      │                                                        │
                      │ 🔒 Reassignment blocked: Decision gate #3 is open.     │
                      │    Resolve or abandon the gate before swapping.        │
                      └────────────────────────────────────────────────────────┘

you · gate #3         Needs you · permission requested
                      claude requests execution of `rm -rf ./build`
                      [ Allow once ]  [ Deny ]
```

Desktop · removing the current orchestrator is refused outright

```
Baton · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 claude · 2 skills ✕]  [agy · 1 skill]
                      ┌────────────────────────────────────────────────────────┐
                      │ ✕ Cannot remove 'claude': it is this room's            │
                      │    orchestrator, and a room always has one.            │
                      │    Make another worker the orchestrator first,         │
                      │    then remove this one.                               │
                      │                                                        │
                      │                                          [ OK ]        │
                      └────────────────────────────────────────────────────────┘
```

Desktop · removing a non-orchestrator worker with in-flight work & DAG dependency refusal

```
Baton · aer-flow
▤ ◱ ⚙

Rooms + New            aer-flow  [👑 claude · 2 skills]  [agy · 1 skill ✕]
                      ┌────────────────────────────────────────────────────────┐
                      │ Remove Worker 'agy'?                                   │
                      │                                                        │
                      │ ⚡ In-flight work detected: Running security sweep...   │
                      │    Stopping worker via InFlightExecutionRegistry...    │
                      │                                                        │
                      │ ✕ Cannot remove 'agy':                                  │
                      │    Active workflow step 2 (Security Audit) requires   │
                      │    this worker. Stop workflow or edit shape first.     │
                      │                                                        │
                      │ [ Stop Workflow & Remove ]              [ Cancel ]     │
                      └────────────────────────────────────────────────────────┘
```

Orchestrator reassignment is human-only and singular. A human clicks the orchestrator pin (`👑`)
next to any worker chip to hand off the role. If a decision gate (an `aer decide` pause point,
or a permission request) is open, reassignment is **blocked mid-gate** — the lock badge explains
that the pending gate must be resolved or abandoned first, so a swap can never orphan a pending
decision.

Removing a worker mid-room checks three things, in order:
1. **Is it the orchestrator?** Refused outright, unconditionally — reassign the role to a
   different worker first, then remove ([0032](../decisions/0032-room-orchestrator-is-mandatory.md)).
   There is no override for this one; a room cannot be left without an orchestrator, even
   momentarily.
2. **In-flight execution stop.** If the worker is currently executing a task, Baton invokes
   `InFlightExecutionRegistry.RequestCancellationAsync` to halt the CLI worker before updating room
   state — the real, already-existing mechanism, not a new one.
3. **DAG dependency check (v1 refusal).** If the room has an active workflow where a downstream step
   relies on the targeted worker, removal is **refused with a clear reason** ("Active workflow step
   2 requires this worker"). The graph is not silently repaired or mutated. To proceed, the person
   can choose "Stop Workflow & Remove" (toggles the workflow off and removes the worker) or
   cancel. This pair genuinely is a 0028 case — "Stop Workflow & Remove" is destructive and carries
   no more visual weight than "Cancel."

### The workflow-toggle-off control

A room does not require a workflow (0001). Toggling a room's workflow off removes the structured
execution graph while leaving every worker, and whatever skills are attached to it, intact in the
room as free-form conversation partners.

Desktop · room header with workflow toggle ON vs OFF

```
Desktop · Workflow ON (shape panel visible)

Baton · aer-flow                                          Workflow [● ON ]
▤ ◱ ⚙

aer-flow  [👑 claude · 2 skills]  [agy · 1 skill]

you Fix the auth bug and run security audit.

claude · working Editing auth.ts...                           Shape
                                                              draft · claude
                                                              ↓
                                                              review · agy
                                                              ↓
                                                              gate · you

──────────────────────────────────────────────────────────────────────────────

Desktop · Workflow OFF (shape panel hidden, workers remain as free-form workers)

Baton · aer-flow                                          Workflow [○ OFF]
▤ ◱ ⚙

aer-flow  [👑 claude · 2 skills]  [agy · 1 skill]

you Fix the auth bug and run security audit.

claude Editing auth.ts...

you @agy review the auth changes in auth.ts.

agy Reviewing auth.ts... No issues found.

Reply… ⏎
```

Toggling the workflow switch in the room header is a visual non-event, not a mode transition. The
right-hand shape panel slides away or fades out, reflecting that step-by-step DAG execution is no
longer active.

The worker chips in the room header (`[👑 claude · 2 skills]`, `[agy · 1 skill]`) do **not** change,
disconnect, or enter an artificial "idle" state. They stay in the room as ordinary free-form
workers — because a room without an active workflow is already free-form by 0001's own model.
Turning off a workflow strips away the graph overlay; it does not touch who's present.

**Amendment (2026-08-14, #1216 / umbrella #1196).** The drawings above settle what the switch *shows*
and leave two things open that only appear once it is built: what happens when it is thrown while the
room is busy, and what happens when it is turned back on. Both are ratified here.

**Thrown while the room has work in flight: refused, with a reason.** The person stops the room first.
This mirrors the DAG dependency check at `:616-621`, which refuses rather than silently repairing the
graph, and it is what keeps "non-event" true in the strong sense — nothing is destroyed because
nothing is touched. Note the escape hatch already drawn there, "Stop Workflow & Remove", is exactly
this rule's other half rather than a counterexample: it *is* a toggle-off during live work, and it is
available only as an explicitly destructive, confirmed action carrying no more visual weight than
Cancel. The bare switch in the header is not that, so it refuses. Having the switch cancel in-flight
work on its own was considered and rejected: it would make a toggle destructive, which is precisely
what calling it a non-event says it is not.

"Work in flight" deliberately does **not** mean a step whose recorded status is running. A workflow is
`Running` when an attempt is live *or* when Flow crashed before recording the outcome (behavioural
spec §6), so that test would leave a room whose process died days ago permanently unable to switch its
workflow off. It means what #1219 established for the same reason: the room's §15 flow lock is held
(only a live pump holds it, and the OS drops it the instant its holder exits), or a step is genuinely
paused awaiting a person. A room parked on a vendor quota is therefore refused — its pump is alive —
and a dead one is not.

**Turned back on: nothing happens but the shape reappearing.** The switch governs whether the room has
a workflow attached and whether its shape is shown, and the graph comes back in whatever state it was
left. Running it again is a separate, deliberate act through the Resume / Run it again card (#1215).
Resuming on a flick of the switch was rejected for the reason auto-resume-on-open was rejected in
#1215: it spends the operator's subscription on a gesture nobody meant as a run.

Two consequences worth stating, since the drawings do not. When the workflow is off, the `Shape`
toggle goes with the panel rather than staying on screen to open an empty one, and a panel that was
already open closes.

And **a room with its workflow off offers no way to run it** — the Resume / Run it again card goes
with the shape. Nothing in the engine yet refuses a dispatch because the switch is off (routing is
later work in #1196), so leaving the offer up would let a room whose header reads `Workflow OFF` run
the very graph the header says is not attached. Hiding it is honest about what the room has, rather
than implying an enforcement that does not exist; when routing lands, the offer's absence and the
engine's refusal will be saying the same thing.

### Resident-room addition (2026-08-04) — spend controls, dormancy, waiting on a lock

Everything below this line is new: the screens for a room whose orchestrator is *resident* —
taking machine-triggered turns on its own cadence (#778). The design constraint they all serve:
the room spends the operator's subscription while nobody is watching, so **what it may spend, and
what it has spent, are visible where the room is** — never in a settings screen you would have to
remember exists. The glyphs drawn here are stand-ins; marks are shapes from the token set — #458's rule, recorded
in `design/tokens.json`'s own status notes: a mark is a shape, never a codepoint.

#### The spend controls

Desktop · a resident room's control surface

```
Baton · aer-flow                        Resident [● ON ] · machine turns 3/10 this hour
▤ ◱ ⚙

aer-flow  [👑 claude · 2 skills]  [agy · 1 skill]  [+ Add]
                      ┌────────────────────────────────────────────────┐
                      │ Spend controls · this room                     │
                      │                                                │
                      │ Gap between machine turns      [  60 s ]       │
                      │ Machine turns per hour         [  10   ]       │
                      │ Turns without progress, then   [   3   ]       │
                      │ the room goes dormant                          │
                      │                                                │
                      │ Used this hour  ▮▮▮○○○○○○○  3 of 10            │
                      │                                                │
                      │ Your own messages are never throttled.         │
                      │ These are the room's own numbers               │
                      │ (turn-throttles.json) — edit them here or in   │
                      │ the file; both are the same thing.             │
                      └────────────────────────────────────────────────┘
```

The values and the live count sit on the room, one click deep, editable in place. There is no
second store: the panel edits the room's own `turn-throttles.json`, the same file an operator can
open in an editor, and a hand edit shows up here on the next read. Deleting the file is safe and
means "the defaults" — the panel keeps working and says so. The header shows the one number that
predicts spend (turns used this hour) whenever the room is resident, so the panel is confirmation,
not discovery.

Phone · the counter, and the sheet behind it

```
9:41 ▮▮▮

Rooms 1 needs you · 1 dormant

◗ payments-api Needs you · schema change

◔ aer-flow Working · turns 9/10 this hour

⧗ migration Waiting · payments-api holds its folder

⏾ docs-sweep Dormant · 3 turns, no progress

Rooms Needs you Settings
```

```
9:41 ▮▮▮

‹ aer-flow · Spend controls

Used this hour  ▮▮▮▮▮▮▮▮▮○  9 of 10

Gap between machine turns 60 s ›
Machine turns per hour 10 ›
Turns to dormant 3 ›

Your own messages are never throttled.
```

On the phone, **state is always visible and editors are one tap away** — the room card's second
line carries the state's own key fact: the holder for a waiting room, the no-progress count for a
dormant one, and the hourly meter for a working resident room only once it reaches **80% of its
cap**. Below that, quiet, per 0006: a healthy resident room's card looks exactly like any working
room's. Tapping into the room's control sheet gets the same three values the desktop panel shows,
one field per row.

#### Dormant

Desktop · the room stopped itself

```
Rooms + New

◗ payments-api Needs you

⏾ aer-flow Dormant · 3 turns, no progress

✓ docs-sweep Finished · 1h

aer-flow  [👑 claude · 2 skills]  [agy]                       ⏾ Dormant

⏾ Dormant · stopped after 3 machine turns without progress
The last three turns tried to fix the failing build and committed nothing.
[ Wake claude ]  [ Swap orchestrator… ]

you how's it going?

⏾ Still dormant — waking is yours to choose.
[ Wake claude ]  [ Swap orchestrator… ]

Reply… ⏎
```

Dormancy renders as a turn, in the transcript, where the history that led to it sits directly
above — the same rule gates follow. It says what stopped, why, and offers the two ways forward.
"Swap orchestrator…" is the existing 0032 reassignment control from the room header, surfaced on
the turn for convenience — not a second mechanism.
The room list gives dormant rooms their own group beside "needs you", because a dormant room *is*
waiting on you — but it is a wait, not a failure, and never drawn as one. And the second turn in
the drawing is the load-bearing behaviour: your message did not wake the loop. The product
answered with the state. A room that stopped because its last three turns went nowhere would
otherwise resume burning turns the moment you asked after it.

#### Waiting on another room's lock

Desktop · two rooms, one folder

```
Rooms + New

◔ payments-api Working · claude

⧗ aer-flow Waiting · payments-api holds this folder

aer-flow  [👑 claude]                                          ⧗ Waiting

⧗ Waiting on payments-api — both rooms point at this folder, and
turns take strict turns. [ Go to payments-api ]

Reply… ⏎   (queues, sends when the folder frees)
```

A blocked room previously looked identical to a slow one, which is the worst presentation: your
model of what the product is doing goes wrong, and the fix is undiscoverable. The wait names its
holder and links to it. It is information, not an error (0006). Typing still queues — the founding
rule that a busy anything is never a reason you cannot act. Opening a *second* room on a folder
that already has one warns at creation: legal, occasionally right, and a choice to make knowingly.
Naming the holder as a *room* rather than a folder path rests on the lock growing a room-name
field — the rider accepted with the thirteen-state ratification on #495, riding with #480's build
(grouped under #752); until it ships, the engine knows only the path.

#### Escalation is a gate

Desktop · a resident turn reaches past the room's floor

```
claude · machine turn Ran the failing test, found the fix, wants to push.

Needs you Push to origin/main is beyond this room's grant.
[ Allow once ]  [ Deny ]  [ Ask someone… ]
```

A resident orchestrator runs at the room's grant floor; anything beyond it renders as the gate
this corpus already has — inline in the transcript, in the "needs you" filter, on the phone, one
object with several entry points. No new escalation surface exists, because the gate already *is*
the product's way of asking past a boundary, and a machine turn asking is no different from a
worker asking mid-conversation. The turn label says it was a machine turn, so "why is this room
asking at 3am" answers itself.

One noun Adding a worker never creates a new object. The header chip changes; the room is the same room. "Session" retreats to its technical meaning — the vendor CLI's resumable session — and stops appearing in the interface at all.

Gates inline Decisions render in the conversation that produced them , and are also reachable from the "needs you" filter and the phone. Several entry points, one piece of state. The separate decision surface goes away.

Steps, not a canvas Shapes are authored as an ordered list that renders as a graph. A step names its blocker to fan out or in — [0014](../decisions/0014-shapes-are-a-list-not-a-canvas.md) — buying keyboard navigation, clean diffs and no tangle without deferring parallelism.

Gate as a toggle "Ask me first" is a property of a step, not a node type. One switch is the entire mental model for human oversight.

State first, then recency The room list groups by state — needs you, working, earlier — and orders by recency inside each group. An earlier draft said recency alone; the stress test showed that at a hundred rooms the three that need you get buried among ninety-one finished ones. Grouping is what keeps the list stable and useful.

Same root Both surfaces open on rooms. "Needs you" is a filter, not the front door — a product that greets you with a queue feels like a chore list rather than a place your work lives.

Notifications inform No approve or reject from the lock screen. It says enough to judge whether to open, then takes you to the decision in context.

Errors are content A failure shows what broke, in the room. Not a status word with the reason behind a drill-in — and the worker that failed is right there to be asked about it.

Readiness up front Which CLIs were detected is shown at first run and in Settings. The most likely first failure is the least self-evident one, so it gets stated rather than discovered.

State is one thing Every surface renders the room's state machine and none derives its own — which is what makes "no task open" while running impossible rather than merely fixed.

M27 addition — the calls added with the four new screens above.

Skills attach, they aren't picked Attaching a skill sets nothing but itself — no model tier, effort, or permission side effects — because it isn't a preset over the other axes, just an addition alongside them.

Bare chips are unchanged A worker with nothing attached looks exactly like it does in "The daily driver" and "Two workers, a gate" above — just the vendor name. Attached skills add a short count next to that name; they never replace it or add a second visual language beside it.

Worker first, skill count as a qualifier, no named identity to protect An earlier revision of this design put vendor, model tier and effort on the chip itself — busy on desktop, worse on a phone's narrow header. A later revision introduced a named preset ("Persona") whose name could stand in for the chip's label entirely — which fixed the busyness but let the chip's primary identity silently become the preset instead of the worker. Removing the preset object altogether removes the failure mode at its root: the chip shows the vendor first, a count of attached skills as a short qualifier (plus the crown for the orchestrator) — `claude · 2 skills` — so a person always knows which worker they're looking at, and there is no named thing that could compete with that identity. The raw axes live one tap away in the popover, where model tier is still named by purpose (deep/balanced/fast), never a specific vendor version — 0023's rule.

Pick by what it does, not its coordinate The skill list shows what each skill *does* ("quick reconnaissance," "code & security review"), not a model×effort grid position — there is no grid, because a skill isn't bound to either axis.

No modified state to track Attaching or detaching a skill applies immediately to the worker's chip. There's nothing to diverge from and nothing to reset, because a worker's current skill set was never an instance of something else.

Single drawer for creation Skill creation uses progressive disclosure in a single side drawer to keep context visible, rather than a multi-step wizard.

No promotion step A skill is account-wide the moment it's created — there's no private/shared distinction to promote between, so the drawer has no "save to library" checkbox at all.

Orchestrator removal is refused outright, not guarded A room always has exactly one orchestrator. Removing the current holder isn't a guarded destructive action with an override — it's refused unconditionally, with the fix stated (reassign first).

Reassignment blocked mid-gate The orchestrator pin cannot be reassigned while a decision gate is open, so a swap can never orphan a pending decision.

In-flight cancellation on removal Removing an active worker halts execution via the real `InFlightExecutionRegistry.RequestCancellationAsync` before updating room state.

DAG removal refused in v1 Removing a worker required by an active workflow step is refused with an explicit reason; silent DAG repair is deferred, not built.

Workflow toggle is a non-event Turning off a room's workflow hides the shape panel while every worker, whatever skills it carries, stays in the room exactly as before.

Resident-room addition (2026-08-04) — the calls added with the screens above.

Spend lives where the room is Throttle values and the used-this-hour count sit on the room, editable in place, one register with the room's own `turn-throttles.json`. Not a settings page, not a second copy.

Phone shows state, not editors The room card carries a throttle counter only when the number is informative — near cap, waiting, dormant — and stays quiet otherwise. Editing is one tap behind, in the control sheet.

Your messages are never throttled Machine turns have a gap, a cap and a breaker; a person talking to their own room does not.

Dormancy is a turn The stop, its reason and the wake control render in the transcript where the history sits — and the loop never resumes as a side effect of asking after it. What a dormant room says when spoken to is the state's own row in [03-interaction-depth.md](03-interaction-depth.md#states-everything-must-handle).

A wait names its holder Waiting on another room's lock is a distinct state that links to the holding room. Information, not an error; typing queues as always.

Escalation is a gate A resident turn reaching past the room's grant floor renders as the existing gate object — inline, in "needs you", on the phone. The turn label says it was a machine turn. No new escalation surface.

Complete set, draft 3 — the shapes and the calls, not the pixels. Mark it up and I'll take another pass before any of it becomes a decision record or touches the backlog.

M27 addendum — four more screens added for skill attachment and room orchestration; draft 3
above is otherwise unchanged. Mark this section up the same way before any of it becomes a
decision record.
