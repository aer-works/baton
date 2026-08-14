# What Baton is

> **Design corpus — started 2026-07-24 during the M25 design pause, kept current since.**
> This is the *source* the decision records were written from; where a record and this document
> differ, **the record wins** — it is the reviewed extraction, not the ruling. See
> [`README.md`](README.md#kept-current-not-frozen-added-2026-07-25-policy-corrected-2026-07-26)
> for the corpus's current-state policy.

---

Baton — what it is

Product definition · draft for markup

## Baton

A drop-in replacement for Claude Code that puts more than one model in the room , and lets you leave the room without losing it.

Agreed 24 July 2026. There were 11 decisions about parts and 9 journeys about behaviours, but nothing saying what the product is — and every defect from the last manual run traced back to that absence, not to wrong code. This is that missing document; the screen designs derive from it.

### The three things it has to be

Each of these carries weight the others don't. They are how arguments get settled later: a proposal either serves one of them, or it doesn't belong.

The baseline

##### A real coding agent

You point it at a directory and talk to it; it edits, runs, and reports. This is the hardest constraint — drop-in means someone moving across from Claude Code loses nothing they had.

The shape

##### A messenger, not a console

Workers are presences in a sidebar — present or not, busy or idle — that you talk to one at a time or address together, and reach from wherever you are rather than only at the machine they run on.

The leverage

##### Shapes you can draw

Defining a repeatable piece of work should be visual and quick — draw it once, save it, start it in a click, and look at its shape again only if you want to.

### What it is, and what it isn't

The right side is the useful half. A definition that rules nothing out isn't a definition — it's a wish.

It is

- A coding agent you point at a folder. The single-agent path stays first-class and fast. Multi-model is an escalation, never a tax on the simple case.

- A room. More than one worker in one conversation, on your own subscriptions, live.

- Operable from elsewhere. Start on the desktop, decide from the phone. Remote is a property of the product, not an add-on.

- Honest about state. Anything running is visible, nameable, and interruptible.

It isn't

- An API product. It runs against subscriptions through vendor CLIs. No key handling, by design.

- A workflow builder you live in. Graphs are for authoring and inspecting templates, not for daily work. You should be able to draw one easily, save it, and never look at it again unless you want to.

- A router or a judge. Flow never reads conversation content to decide anything. Discipline in Flow, intelligence in Workers.

- A team tool. One operator, several agents. Not multiplayer.

### The nouns

Small on purpose. Every noun added here becomes a thing the person has to learn, so each has to earn its place. Two new nouns from the M27 pass — **Skill** and **Room orchestrator** — are appended after Template below.

Room A conversation against a directory, with one or more workers in it. The main noun — what you start, return to, and what the sidebar lists. There is deliberately no second noun for "a room with more than one worker": adding a worker changes who is present, not what kind of thing you have. ("Session" names something narrower: the vendor CLI's own resumable thread, an adapter concern never presented as the thing you opened — [0013](../decisions/0013-room-is-the-user-facing-noun.md).)

Worker One vendor's CLI running under your subscription. `claude`, `agy`. Interchangeable by design , and present or absent like a person in a thread.

Gate Where work stops and asks you — the only thing allowed to block , and the unit the "needs you" list carries and the phone answers. Comes in three kinds, because they ask different things of you: a permission (may I run this command), a decision (which of these), and an action (review this). Two already exist in the engine as ReadyForReview and NeedsInput ; permission is the one genuinely new kind.

Template A saved shape of work — draft→review→gate — defined on a graph and started in one click . The graph is how you author and inspect it, never where you live day to day.

Skill *(added 2026-07-25, M27)* Instructions plus tool requirements plus bundled assets — a capability a worker attaches directly, not a separate preset object over the worker chip. A worker can attach zero, one, or several at once; there is no shipped default set — every skill is user-authored, discovered when needed ([0033](../decisions/0033-skills-attach-directly-no-persona.md)). Realized per-vendor by the adapter ([0010](../decisions/0010-skills-and-advisor.md)); account-wide, one library per person ([0031](../decisions/0031-skills-are-account-wide.md)). See [02-screens.md](02-screens.md) for the attachment UI.

Room orchestrator *(added 2026-07-25, M27)* Which worker in the room is the default addressee — where an otherwise-ambiguous routing choice or an unattributed artifact/action is credited. **Not** a worker authorized to call `aer decide` on another's gate — [0038](../decisions/0038-a-reviewer-verdict-never-calls-aer-decide.md) forecloses that for every worker, orchestrator included. A room always has exactly one orchestrator; the first worker added becomes it by default, and removing the current holder is refused until the role is reassigned to someone else first ([0032](../decisions/0032-room-orchestrator-is-mandatory.md)). The authority itself is an ordinary attached Skill, auto-bound to whoever holds the role — not a special-cased flag. See [02-screens.md](02-screens.md) for the reassignment control.

### The one flow that matters

If this path is good, the product is good. Everything else is in service of it — and today it is the path that breaks.

flowchart LR
A["Point at a folder"] --> B["Talk to one agent"]
B --> C{"Worth more
than one?"}
C -- no --> B
C -- yes --> D["Add a worker
to the room"]
D --> E["They work
you watch"]
B --> E
E --> F{"Needs
you?"}
F -- no --> E
F -- yes --> G["Gate:
decide"]
G -.-> H["from the phone"]
G -.-> I["from the desktop"]
H --> E
I --> E
E --> J["Done"]

Two claims worth testing. The chat is where you stay — escalating to a second worker must not move you to a different screen. And a gate is answerable from whichever surface you happen to be holding, which is why remote can't be bolted on later.

### A room's life

Written as states because the last run produced two surfaces disagreeing about which state a room was in — the header said "no task open" while the thing was running.

stateDiagram-v2
[*] --> Idle: created
Idle --> Working: you send / it runs
Working --> NeedsInput: hits a gate
NeedsInput --> Working: you decide
Working --> Finished: completes
Working --> Failed: errors
Working --> Cancelled: you stop it
Working --> Stopped: its process dies
Finished --> Working: you send again
Cancelled --> Working: you send again
Stopped --> Working: you resume
Failed --> Working: retry

One source of truth per room. Every surface — switcher row, header, inbox, phone — renders this state and nothing derived independently. Cancelled and Failed are states, not absences: a stopped room must never read as "Finished."

Stopped was added 2026-08-14 (#1219, amending [0020](../decisions/0020-one-state-machine.md)) and is the one state no reading of the journal can produce — a room whose process died records exactly what a running one does. It is separated by the room's own lock, which the OS drops when its holder exits. Note "a stopped room" in the sentence above is the general sense, halted without finishing, and covers all three of Cancelled, Failed and Stopped.

### The surface this implies

Not the current app — what the definition above asks for. Rooms always visible, the current one in the middle, and a gate answered where you already are.

● Baton

▤ ✎ ◈ ⚙

Rooms + New

◗ aer-flow Needs you · 2 workers

◔ payments-api Working · 4m

✓ docs-sweep Finished · 1h

✕ migration Failed · 3h

— spike-cache Cancelled · 1d

aer-flow claude + agy  ·  + Add worker

you Rework the switcher so a new room shows up immediately.

claude Two causes — the list only refreshed at startup, and a task registered only on a successful run. Patch ready.

Needs you · agy reviewed Approve the change to the push fan-out?
Approve Changes Reject

Reply… ⏎

What changed versus today. The gate is answered inline in the conversation rather than on a separate decision screen; adding a worker is a control in the room's header rather than a different noun to create; and every room's state is legible without leaving the one you're in.

### Settled

Four questions, answered. These are now constraints on everything downstream, not opinions to relitigate per screen.

Resolved

#### Graphs author templates; they are not the day job

What makes visual workflow definition worth having is that it is easy — so keep that, and put it where it pays: you draw a shape once, save it as a template, and start a room from it in a click. You can visualise a running room's shape whenever you want, including after it has started. What you don't do is live on a canvas.

Consequence: the DAG stops being a destination and becomes two things — an authoring surface and an optional view of a room. A meaningful slice of the backlog is scoped against the old assumption and has to be re-read.

Resolved

#### One noun, not two — a room just has more workers

A room with two workers is still a room; adding one changes who is present, not what kind of object you're holding ([0001](../decisions/0001-two-nouns-workflow-and-session.md)/[0013](../decisions/0013-room-is-the-user-facing-noun.md)). One fewer concept to teach, and it kills the question "is this a session or a room?" before anyone can ask it.

Consequence: decision 0001's two nouns are workflow and room.

Resolved

#### The UI layers are rebuilt; the rest is touched wherever a journey needs it

Aer.Ui and Aer.Mobile get rebuilt against this definition rather than patched toward it. The engine, adapters, daemon and protocol are not frozen by that — they're touched wherever a milestone's own required journeys need it (`docs/plan.md`), not walled off by which layer the original five manual-run defects happened to live in.

Consequence: open UI issues are no longer bug reports against code that will exist. They become requirements on the rebuild, or they get closed. An engine/daemon defect blocking a required journey is in scope too, not deferred past this plan.

Resolved

#### Both surfaces get designed before either gets built

Desktop and phone are genuinely different views of one product, not one layout at two widths — so the mockups cover both, and the differences are decided on paper rather than discovered in code.

Definition agreed; screen-level design in progress. This becomes a numbered decision in docs/decisions/ , the nine journeys get re-derived from it, and the whole backlog is audited against it — including issues that stop earning their place.
