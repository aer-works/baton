# AER — Product journeys

> What AER promises a person. The behavioural spec says what the **engine** does; this says what the
> **product** does — the layer that was never written, and where every defect the M25 evaluation
> found actually lives.

This is a living document, not a versioned snapshot. Its home was chosen for legibility, not for
adjacency to the frozen behavioural specs; the doc scrub (#367) may relocate it further. It is the
artifact issues cite and the target-design spec rewrite is written against.

## Reading these

- **A journey is a promise stated as a person's outcome that crosses surfaces** — not a screen, not a
  feature. Milestones were capability-shaped; no milestone was ever *"start work at your desk and
  approve it from your phone,"* so that path was nobody's deliverable. It is the broken one.
- **Steps illustrate, they don't certify.** Each journey's *Passes when* line is the acceptance bar;
  the *Path* just shows one way there and is explicitly **not** a completion checklist.
- **Status is machine-kept.** A journey's status is derived from its test (#313) and enforced by CI
  (#314): a recorded status that contradicts its test breaks the build, and a journey with no test
  cannot claim to pass. Human-gated journeys carry a dated sign-off the same check guards. This is the
  teeth that stops these promises from rotting the way the old docs did.
- **A milestone is done when its journeys pass.** Passing means the automated test drives the *real*
  surface end to end; only where something genuinely needs a person — live vendor auth, physical
  device pairing — does a dated human sign-off stand in, flagged per journey.

## Status today

Baseline **2026-07-24** — **Fails 15 · Partial 3 · Passes 0.** The honest starting line the rebuild
moves. Written against the target product; today's product fails most of these, which is the point.

**Revised 2026-07-25 (#527) — Fails 16 · Partial 2 · Passes 0.** J6 moved *backwards*, from Partial
to Fails, and that is the audit's sharpest single result. It was Partial because `--disallowedTools`
shipped and its engine test passed; measurement then showed the flag never bounded the capability at
all — a model denied `Write` writes the file through `Bash`
([#529](https://github.com/aer-works/baton/issues/529)). **A journey status can be too generous
as well as too stale**, and a green test on the mechanism is what hid it. The reconcile gate (#489)
compares declared status against test results; it cannot catch a test that asserts the wrong thing.

**Revised 2026-07-30 (#806) — Fails 17 · Partial 2 · Passes 0.** J19 was added and J2 amended after
the operating-surface audit (#806, measurement there): the phone turned out to be where the
operator actually runs things, while these journeys' phone was an approve-and-author surface —
never the place a room is *run* from. J19 names the operating loop (its absence meant that path was
nobody's deliverable, the exact failure this file's preamble warns about); J2 stops assuming the
desk is where you talk; J9, J14 and J16 each gained a phone clause in Verify.

**J10–J18 were added 2026-07-24** from the M25 design corpus, whose nine claims it states are
*“journey-shaped on purpose”* — each a claim plus the condition under which it counts as
demonstrated. They are **additive**: two look like duplicates of J1 and J6 and are not, for reasons
each one's *Today* and *Verify* notes give. The corpus's argument for writing them this way is this
document's own: *“a claim that can only be demonstrated end to end cannot be quietly satisfied by a
passing unit test.”*

---

## J1 — Start work on the desktop, approve it from your phone

**Status:** Fails — automated

You kick off a piece of work at your desk, walk away, and later approve it from your phone — without
going back to the machine.

- **Spans** — desktop → daemon → paired phone · *seam: the phone's decision inbox ↔ the daemon's open
  work*
- **Passes when** — a desk-started run that pauses at a decision gate appears on the paired phone;
  approving it there advances the run to completion, and the desktop reflects the new state without a
  manual reload.
- **Path** *(illustrative)* — start a review-run on the desktop · it pauses at its gate · the phone
  shows it waiting on you · you decide and approve · it resumes and finishes · the desk updates.
- **Today** — the phone's inbox is scoped to the daemon's single open room, so a desk-started run
  often isn't there to approve.
- **Serves** — #335, #319, #330

## J2 — Open a folder, talk to an agent, and grow the room without leaving the chat

**Status:** Partial — automated + live

You point the product at a directory and start talking to an agent. When it's worth more, you bring
another worker into the room or spin off a gated review as a child — and the chat stays the place you
are. It never becomes the review.

- **Spans** — desktop or phone — the chat is wherever you are holding it (#806) · *the room model: a
  session is a multi-participant conversation that spawns child sessions (decisions 0001 / 0008 /
  0009)*
- **Passes when** — from a live chat you can either **add a second worker to the same room** or
  **spin off a clearly-marked child** (draft→review→gate) that reports its result back into the chat;
  the chat stays live throughout (async), and the child shows both inline and in the inbox, marked as
  a child. The chat surface may be the desktop or a paired phone; the promise does not shrink with
  the screen.
- **Path** *(illustrative)* — open a folder · chat with the agent · add a reviewer to the room, or
  spin off a two-vendor review as a child · it runs (you can watch and interject) · it reports back at
  its gate.
- **Verify** — spawn / host / gate and async liveness automated; the live-vendor quality of a review
  is a human / live-smoke check (vendor auth can't be automated).
- **Today** — sessions and review-runs exist only in isolation; the room model — several workers,
  spawn-and-hold child sessions, staying live while a child runs — isn't built. The *daemon-side*
  half of that is no longer the obstacle: #335 keyed host state per session, so one daemon can hold a
  chat and a child running at once. What is missing is the spawn itself (#340) and a surface that can
  show both (#336/#337).
- **Serves** — #333, #335, #340, decisions 0001/0008/0009

## J3 — Come back after a day and immediately see what needs you

**Status:** Fails — automated

You reopen the product after being away and, without hunting, see the things waiting on your decision
first — held apart from what's still running and what already finished.

- **Spans** — desktop + phone · *seam: list UI ↔ projection / state*
- **Passes when** — on reopening either surface, work is legibly separated into **waiting on you** /
  **running** / **finished**, with waiting-on-you first; and a **failed** piece of work reads as
  failed, not as "finished."
- **Path** *(illustrative)* — reopen the app · the first thing you see is the short list of decisions
  waiting · running work is visible but secondary · finished work (failures correctly labelled) is
  available, not in your face.
- **Today** — the phone now lands on the switcher (shipped PR #1046; former #337 gap), and failed/finished separation has since improved (0018 four-band sort PR #1134, out-of-plan band PR #1136); remaining edges are the parameterized "Nothing is waiting on you" line at `inbox_screen.dart:701` and whatever umbrella #752 (status truthfulness, former #355) still tracks. The vendor audit (#527) settled *where the signal
  comes from*, which this journey had never pinned down: both vendor events that could have announced
  a pause — `PermissionRequest` and `Notification` — are silent under `-p`, so
  [0030](../docs/decisions/0030-aer-is-its-own-notifier.md) makes AER the notifier. It follows that
  **"Nothing is waiting on you" must be evidenced by AER's own gate state, never by the absence of a
  vendor event** — an absent signal from a silent source is the calm-screen failure 0018 names. (corrected 2026-08-13, #1149)
- **Serves** — #337, #355, #334, decisions 0018, **0030**

## J4 — Pair a phone from scratch on an ordinary network

**Status:** Partial — human pairing

A brand-new phone on the same normal Wi-Fi as your machine — not enrolled in any tailnet — pairs and
starts working together in one pass.

- **Spans** — phone + daemon · *seam: pairing / discovery, the tailnet-vs-LAN address gap*
- **Passes when** — a fresh phone on the same LAN reaches the daemon at an address it's actually
  given, completes the handshake within the code's lifetime, and makes a first authenticated
  round-trip; a daemon port change doesn't permanently strand it.
- **Path** *(illustrative)* — fresh phone · enter the reachable host · mint and enter the code in one
  pass · handshake completes · first authenticated call succeeds.
- **Verify** — the real cross-device pairing is a human walk (physical device on a real LAN, per the
  runbook); the code-lifecycle and port-stability logic is automated.
- **Today** — works on a tailnet; on a plain LAN the daemon advertises only its Tailscale address, the
  phone persists host:port verbatim with no rediscovery, and a restarted daemon on a new port can
  strand every device (#347, #349).
- **Serves** — #347, #349, #346

## J5 — Start the same piece of work from either surface and see it on both

**Status:** Fails — automated

Whether you start something at your desk or on your phone, it shows up live on the other — the same
object, the same state, not two disconnected views.

- **Spans** — desktop ↔ daemon ↔ phone · *seam: the broadcast path*
- **Passes when** — work started on one surface appears on the other with no manual refresh; both
  render the same object identity and its live status; and this holds for **every** kind of work, not
  just chat.
- **Path** *(illustrative)* — start work on the desktop · the phone shows it appear and track state
  live · (and the reverse) · both agree on what it is and where it's at.
- **Today** — desktop-started work never broadcasts, so paired phones never see it (#330); starting a
  non-chat template from the phone leaves it on "No room is open" while the daemon reports it running
  (#348).
- **Serves** — #330, #348, #335

## J6 — Deny a tool and have it actually blocked

**Status:** Fails — automated · safety

When you withhold a capability from a piece of work, the work genuinely cannot use it — the permission
is enforced, not merely displayed.

- **Spans** — engine · *seam: permission grant ↔ enforcement*
- **Passes when** — a capability the user has not granted cannot be exercised by the worker **by any
  route**; an attempt to use a denied tool is refused at the boundary and recorded — not silently
  allowed. **On every adapter**, either by an enforcing flag or by refusing to build the dispatch at
  all. The withheld *capability* is what must be unreachable, not merely the named tool: writing a
  file through `Bash` when `Write` was withheld fails this journey.
- **Path** *(illustrative)* — start work with a tool withheld · the worker attempts to use it · the
  attempt is refused and surfaced · the withheld capability never runs, **including through a
  different tool that could achieve the same effect**.
- **Today** — **Fails, and the reason changed on 2026-07-25.** #331 landed `--disallowedTools` on
  `ClaudeWorkerAdapter` and the engine leg is green, which is why this read *Partial*. The vendor
  audit (#527) then measured that **tool restriction is not a capability boundary**: a model denied
  `Write` reaches for `Bash` and writes the file
  ([#529](https://github.com/aer-works/baton/issues/529),
  `pixi run vendor-verify -- --only gate.allowedtools-is-preapproval-not-ceiling`). So the flag that
  made this Partial never enforced the promise as written above.
  [0029](../docs/decisions/0029-the-gate-is-three-mechanisms.md) is the answer: a `PreToolUse` hook
  is the only mechanism covering vendor tools, exit-2 blocks even against an allow rule, and it is
  now mandatory on every spawned worker. Gaps: `agy` has no deny-list flag, so
  `GeminiWorkerAdapter` fails closed by throwing `PermissionGrantUnsupportedException` — correct per
  decision 0004, and **untested**; the end-to-end refusal has never been run live; and the agy hook
  command is fixed and measured **on Windows only** — the Unix form is read from `sh`'s grammar, not
  from a run, because no Unix host has measured it.
  *This line said "the hook is not yet shipped" long after #554 shipped it, and the tree elsewhere
  said it was confirmed working. Both were wrong in opposite directions, and #710 measured the
  truth: it shipped, and on Windows it never started, because agy runs the command through `cmd /c`
  and the path was quoted. One mechanism, three recorded states — which is the failure this journey
  file exists to prevent, occurring in it.*
  *This status was `Fails` for a day after #331 merged with the engine test passing — the drift the
  reconcile gate now runs in CI to catch (#489). It has now been wrong in the other direction too:
  green on the mechanism, silent on whether the mechanism bounded anything.*
- **Serves** — #331, #529, decisions 0004, 0022, **0029**

## J7 — Lose the connection and get back to work

**Status:** Fails — automated + human

When the phone loses the daemon — network drop, daemon restart, changed port — it tells you the truth
about what happened and offers a recovery that actually works.

- **Spans** — phone ↔ daemon · *seam: connection state ↔ recovery action*
- **Passes when** — a disconnected phone shows a truthful, human-readable state (not a raw exception)
  and the offered recovery action genuinely restores the connection or leads to re-pairing — no
  dead-end button that can't succeed.
- **Path** *(illustrative)* — the connection drops · the phone shows a clear "disconnected, here's
  why" state · the offered action (reconnect / re-pair) actually restores service.
- **Verify** — the state / action logic is automated; the real-device network-drop walk is a human
  check.
- **Today** — a disconnected phone shows a raw Dart exception (`errno 111`); the only offered action,
  Reconnect, can't succeed; real recovery is hidden as "Forget pairing" — which itself doesn't fully
  revoke (#346, #349).
- **Serves** — #346, #347, #349

## J8 — Open it for the first time and know what to do

**Status:** Passes — automated

The first time you launch the product — no work, no pairings, nothing — each surface tells you what it
is and gives you a real first action, not a blank wall.

- **Spans** — desktop first-run + phone first-run (pre-pairing) · *seam: empty state ↔ a real entry
  point*
- **Passes when** — on a truly empty first launch, each surface presents a clear primary next step that
  leads to a real outcome (open a folder / start work on desktop; pair to a machine on phone) — not an
  empty list or a "Nothing is waiting on you" dead-end.
- **Path** *(illustrative)* — fresh install · open desktop — it invites you to open a folder / start
  your first work · open phone — it invites you to pair · you reach a real first action without a
  manual.
- **Today** — passing (automated). The desktop empty state offers Start-from-template / Create-workflow
  and the phone's empty rooms surface offers "New room" — both driven green (`J8_DesktopFirstRunTests`,
  `j8_first_run_phone_test`). The phone's pre-pairing first run routes to pairing (`main.dart`), attested
  by inspection rather than a test; the driven phone leg covers the empty-rooms dead-end (#337).
- **Serves** — #337, #338, #339

## J9 — See what you're spending across every vendor

**Status:** Fails — automated

You can see usage across all the vendors AER drives — in one place — so multi-worker work and
worker-to-worker exchanges don't spend blindly.

- **Spans** — every adapter · *home on the dedicated activity surface (#360) / Settings (#338)*
- **Passes when** — a single view shows usage across every vendor AER orchestrates, best-effort per
  what each vendor's CLI exposes — the real cost lever for the invoke-per-turn runtime (decision 0008).
- **Path** *(illustrative)* — open the usage view · see per-vendor consumption across your workers · it
  reads the same whichever runtime path is in play.
- **Verify** — aggregation and display automated; per-vendor figures are only as rich as each CLI
  exposes (best-effort, and labelled as such). The per-vendor state renders on the phone too — an
  exhausted vendor's *"resumes after 14:00"* (decision 0026) matters most to someone who is away
  (#806).
- **Today** — no cross-vendor usage view exists; usage is invisible across the workers AER runs.
- **Serves** — #360, #338, decision 0008

## J10 — Ask a second model at a live gate, and still not have decided

**Status:** Fails — automated + live

Something needs your judgement and you are not sure. You put the question to a worker that is not in
the room; it joins, answers, and contradicts the first — and the decision is still sitting there,
yours to make.

- **Spans** — desktop · phone · *the gate as a long-lived object with its own conversation
  (decision 0019)*
- **Passes when** — at a live gate, a worker not previously in the room is asked, joins by being
  asked, answers, and contradicts the first — **and the gate is still open**. Nothing but the
  operator's answer closes it: not a consulted worker agreeing, not all of them agreeing.
- **Path** *(illustrative)* — a gate is raised · choose "ask someone" · pick a worker not in the room ·
  see what it will be sent, itemised, and edit it · it joins and answers · the gate is still pending.
- **Verify** — the consulted worker's evidence bundle is disclosed before sending and every item is
  removable; the responder is **chosen**, never inferred from conversation content (Rule 1).
- **Today** — nothing exists. There is no gate object that survives a consultation, and no way to
  address a worker that is not already a participant.
- **Serves** — #424, #385, #367, decision 0019

## J11 — Two subscriptions working in one room, with no key anywhere

**Status:** Fails — live

Both vendors act in the same room, on the plans you already pay for, and at no point were you asked
for an API key.

- **Spans** — both adapters · *the subscription-first commitment (decision 0012, CLAUDE.md Adapter
  Isolation)*
- **Passes when** — a room where both vendors act, on plan auth, with **no key configured anywhere** —
  and a person can confirm that by inspection, not by trust.
- **Path** *(illustrative)* — sign in to each vendor's CLI outside AER · open a room · add one worker
  of each · both answer in the same conversation.
- **Verify** — permanently a **human** gate. The adapters deliberately own no key-handling code and
  shell out to whatever is authenticated on the host, so there is no headless way to provision this
  and there should not be one — see CLAUDE.md's live-vendor smoke section.
- **Today** — the machinery exists (M12 proved a live mixed-vendor run) but nothing surfaces vendor
  readiness or presents two workers as one room's participants.
- **Serves** — #478, #391, decision 0012

## J12 — What one vendor learned, another one knows

**Status:** Fails — automated + live

A fact established by one worker is available to a different vendor later in the same room, without
you restating it.

- **Spans** — both adapters · the room's memory document · *decision 0016*
- **Passes when** — a fact established by one vendor is used by a **different** vendor later in the
  same room — and the memory it came from is visible, attributed and editable, not an invisible store.
- **Path** *(illustrative)* — one worker establishes a project fact · it proposes remembering it · you
  accept · a different vendor uses it many turns later without being told again.
- **Verify** — additions are **proposed and accepted, never inferred** — the product must not decide
  on its own what is worth remembering. The project's own vendor files (a repo's `CLAUDE.md`) stay
  honoured; room memory is additional, never a replacement.
- **Today** — the room-memory engine (0016/0044) is built and test-enforced (0050 isolation), and proposals are proposed-and-accepted; missing is any UI to see, attribute, or edit room memory on either surface, plus the #1019 continuity remainder. (corrected 2026-08-13, #1149)
- **Serves** — #442, #386, decision 0016

## J13 — Two workers from one vendor, at different models and efforts

**Status:** Fails — automated + live

A patient author and a cheap reviewer, on one subscription — because vendor, model and effort are
three separate choices.

- **Spans** — desktop · phone · *decisions 0017 and 0023*
- **Passes when** — two chips, **same vendor**, different model and effort, both answering in one
  room — and effort and model read in AER's own vocabulary (quick/standard/careful/exhaustive;
  deep/balanced/fast), never a vendor's flag value.
- **Path** *(illustrative)* — add a worker at a deep model and careful effort · add a second of the
  same vendor at a fast model and quick effort · both answer · the chips distinguish them.
- **Verify** — a vendor's own effort string must not appear in any surface; the mapping happens in the
  adapter (Architecture Rule 2). Where a vendor cannot express a level distinctly, the collapse is
  **disclosed** rather than silently faked.
- **Today** — vendor, model, and effort exist as three engine axes (0017 — `RoleDispatch.ToBinding` model/effort overrides, tier defaults in WorkerTiers.json); missing is 0023's AER-vocabulary mapping and any model/effort UI — vendor strings pass through verbatim where they surface at all. (corrected 2026-08-13, #1149)
- **Serves** — #391, #479, decisions 0017, 0023

## J14 — Hand a document to another vendor and see exactly what changed

**Status:** Fails — automated + live

One worker writes something, another edits it, and you can read the difference.

- **Spans** — both adapters · the files surface · *decision 0021*
- **Passes when** — one document authored by one vendor and edited by another, **with a diff between
  their versions**, each version attributed to who produced it.
- **Path** *(illustrative)* — a worker writes a plan · attach it and hand it to the other vendor · it
  returns an edited version · compare the two.
- **Verify** — the attachment is explicit and visible **before** sending, so "which version did it
  actually see" stays answerable. No execution directory or execution number appears anywhere. The
  diff stays legible on a phone screen — side-by-side is a desktop luxury, not the promise (#806).
- **Today** — the engine stores artifacts per execution, but they are not objects a person can pick
  up, version, attribute or hand over.
- **Serves** — #377, #455, decision 0021

## J15 — Quit the app mid-run, answer on your phone, come back to it finished

**Status:** Fails — human

You close the laptop while something is running. A permission reaches your phone, you answer it there,
and when you reopen the desktop the work has continued past it.

- **Spans** — desktop → daemon → paired phone → desktop · *the daemon owning the run is why remote
  cannot be bolted on later*
- **Passes when** — the desktop app is **quit** mid-run; a **permission** raised while it is closed
  reaches the phone; answering there advances the run; reopening the desktop finds it continued.
- **Path** *(illustrative)* — start work · quit the app · the worker asks permission · the phone
  notifies · answer it in context · reopen the desktop and find the run past that point.
- **Verify** — cross-device and cross-process, so a **human** gate. The notification informs and opens;
  it never carries the verdict (decision 0018).
- **Today** — nothing. This is distinct from **J1**, whose bar is a *decision* gate on a still-running
  desktop app; the permission kind is the one decision 0015 marks as genuinely new, and it is gated on
  #445's mechanism.
- **Serves** — #445, #337, #434, decisions 0015, 0022

## J16 — Grant a permission once, stop being asked, and find it later to revoke

**Status:** Fails — automated + live

The scope ladder doing its job: you answer once, it holds, and three weeks later you can still find
what you agreed to.

- **Spans** — desktop · Settings · *decision 0022, over decision 0004's scopes*
- **Passes when** — granting **"allow in this room"** means the same request is not asked again, and
  the grant can be **found and revoked in settings**.
- **Path** *(illustrative)* — a permission is raised · choose a room-scoped rung from the ladder shown
  at the moment of asking · the same request proceeds silently afterwards · open Settings, find the
  standing grant listed under that room, revoke it · it is asked again.
- **Verify** — this is the **grant** path and is distinct from **J6**, which is deny-enforcement. Where
  a rung is advisory rather than enforceable on the chosen vendor, that is stated at the moment of
  granting — `agy` matches command rules literally, so a family-shaped grant is not expressible there
  (`docs/vendor-capabilities.md`). The standing-grants list is reachable from the phone as well —
  three weeks later you are as likely to be holding it as sitting at the desk (#806).
- **Today** — permissions are advisory and unenforced (#331), there is no ladder, and there is no
  Settings surface to list or revoke anything (#338).
- **Serves** — #445, #481, #338, decisions 0004, 0022

## J17 — Author a shape on a phone, start it on the desktop, watch it run

**Status:** Fails — automated

The payoff for choosing a list over a canvas: the authoring surface survives the small screen.

- **Spans** — phone → desktop · *decisions 0014 and 0025*
- **Passes when** — a four-step template is authored **on a phone**, started on the desktop, and runs
  to completion with its shape legible as it goes. **At least one step names a blocker other than the
  step above it** (fan-out or fan-in), demonstrating that this is a list operation, not a canvas
  gesture — 0014 (#503, item 5) treats this as first-class, not a deferred cost.
- **Path** *(illustrative)* — on the phone, add four steps: step 1, two steps both blocked by step 1
  (fan-out), a fourth blocked by both (fan-in) · reorder by dragging, write each step's instruction,
  toggle "ask me first" on one · on the desktop, start a room from it · watch the two parallel steps
  advance together.
- **Verify** — each step's **instruction is its body**, its named blocker's output flows in implicitly,
  and there is no template language to learn. A step with no instruction is rejected **at edit time**,
  not at run time. Two steps whose blocker is already satisfied run **concurrently**, not one after the
  other in list order.
- **Today** — authoring is a desktop canvas behind Advanced with a second vocabulary (#327), and the
  step model has no instruction field at all.
- **Serves** — #339, #327, #340, decisions 0014, 0025

## J18 — Ask everyone one question and read the answers side by side

**Status:** Fails — automated + live

One question, every worker, answers laid out together — including when they disagree, which is the
case that pays for the whole idea.

- **Spans** — desktop · phone · *decision 0024*
- **Passes when** — one question produces **two answers side by side, disagreeing** — presented for
  comparison rather than as sequential turns that read like a conversation between workers.
- **Path** *(illustrative)* — type `/ask-all` and a question · every worker in the room answers · the
  answers are shown against each other.
- **Verify** — it is a **command, not a mode**: you drop into it for one message and out again. On a
  phone it is reached from the Actions sheet, since a slash palette does not survive a touch keyboard.
  Broadcasting declines to choose a recipient, so it never becomes routing-by-inference (Rule 1).
- **Today** — there is no command palette, no namespacing, and no multi-worker room to broadcast into.
- **Serves** — #386, #424, decision 0024

## J19 — Run the room for a day from your pocket

**Status:** Fails — automated + human

Work you started runs as several workflows while you're away. Each moment that needs or informs you — a
workflow finished, failed, or waiting on your call — reaches your phone without you asking; acting from
the phone keeps the room moving; and at no point after initiation is the desktop required.

- **Spans** — daemon → paired phone → daemon · *the operating loop: wake events out, decisions back
  (#778's fast-fire orchestrator turns, #799's wake-bridge, decision 0030's AER-as-notifier)*
- **Passes when** — a multi-workflow room, initiated from either surface, delivers workflow-terminal and
  needs-you events to a paired phone via AER's own notifier; a decision answered from the phone
  advances the room; and the cycle repeats at least twice with the desktop untouched after
  initiation. The notification informs and opens, never carries the verdict (decision 0018).
- **Path** *(illustrative)* — dispatch two workflows · pocket the phone · a workflow finishes and the phone
  says so · a review verdict needs you; the phone opens it; you answer · the room dispatches the
  follow-up · the second workflow's moment arrives the same way · the desk was never touched.
- **Verify** — the phone half is a human walk (real device, real notification); the daemon-side
  event→notification pipeline and the decision round-trip are automated. "Nothing needed you" must
  be evidenced by AER's own gate state, never by the absence of a notification — J3's 0030 rule,
  verbatim.
- **Today** — nothing delivers. Push exists on no surface, workflow events are relayed by a human
  orchestrator hand-tailing journals, and the decision inbox is scoped to a single open room. #806
  records the measured session behind this journey — a full build day driven from a pocket through
  a harness never designed for it, every workaround in it a requirement here. `docs/plan.md` §M26
  names this journey as the milestone's demo bar.
- **Serves** — #799, #806, #337, decisions 0018, 0030

---

The starting set is deliberately small and **grows** as milestones add promises. Each journey earns a
test (#313); its status is enforced against that test (#314); a milestone is done when its journeys
pass. Failure and safety promises (J6, J7) are first-class, not edge-conditions inside the happy path.
