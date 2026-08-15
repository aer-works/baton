# Decision records

Numbered, immutable-ish records of decisions that shape the product. One decision per file.

They exist because intent was scattered across issue comments, chat transcripts, spec prose and
three competing planning documents — and scattered intent is what produced a six-destination product
where four surfaces show the same objects and none reconciles with the others.

## How they relate to everything else

| Artefact | Answers | Lives |
|---|---|---|
| **Decision record** | *why* we chose this, **and it is still in force** | here, numbered, cited by the rest |
| **Journey** | *what the product promises* | `spec/`, see #312 |
| **Behavioural spec** | *what the engine does* | `spec/` |
| **Milestone history** | *what we decided **then***, kept for provenance | [`docs/milestone-history.md`](../milestone-history.md) |
| **Plan** | *what we are doing **now*** | [`docs/plan.md`](../plan.md), gated |
| **Issue** | *what to change* | GitHub, cites a journey |

The spec cites decisions. Issues cite journeys. **#283 is the index that links both** — it is not
another document competing with them.

**Decision record vs milestone history** — the distinction that used to be missing, and the reason
that file was renamed (it was `decisions-of-record.md`, which read as a rival to this folder):

> A **decision record** is a rule you must follow today. **Milestone history** is why a past
> milestone did what it did — provenance, cited from code comments and runbooks when someone asks
> "why is this field here?". If a historical decision is still binding, it belongs *here*, as a
> numbered record. History is never the authority for current work.

Neither is the same as the plan: the plan says what is being built, and defers *status* to the
sources that keep it.

## Format

Front matter, then the record:

```
# NNNN — Title
Status: proposed | accepted | superseded by NNNN
Date: YYYY-MM-DD
```

Then: **Context** (what forced the decision, with evidence), **Decision** (what we chose, stated
plainly), **Consequences** (what this makes easy, what it makes hard, what it obliges us to do),
and — **required since 2026-07-25 (#527)** — **Rests on**.

### `Rests on` — the load-bearing facts, and what would falsify them

A list of the specific external facts the decision would not survive losing. Each row names the
fact, how it is known, and what happens to this decision if it turns out false.

```
## Rests on

| fact | how we know | if false |
|---|---|---|
| A `PreToolUse` hook exiting 2 blocks a tool even with an allow rule | **measured** — `pixi run vendor-verify -- --only gate.hook-exit-2-beats-allow` | the gate has no vendor-independent enforcement point; §3 is void |
| A second concurrent login against one subscription is permitted | **assumed** — needs the account owner; not measurable from an agent session | per-worker config roots collapse to one; §5's isolation model needs replacing |
```

Note what the example does *not* contain: a row like *"`--allowedTools` bounds what a worker can
do — assumed"*. That claim is **measured false** ([#529](https://github.com/aer-works/baton/issues/529)),
and filing a known-false fact as merely unverified is the most dangerous row this table can carry —
it reads as pending work rather than as a broken dependency. **If a fact is false, the decision is
already broken; say so in the decision, don't park it here.**

**Why this is now mandatory.** The vendor audit (#527) falsified four vendor claims this project had
built on, and the decisions that broke — 0015 and 0018 — had asserted a *mechanism* without recording
what it rested on. When the mechanism turned out to be wrong there was no way to see what else fell
with it, so the blast radius had to be recovered by re-reading everything. A decision that names its
dependencies makes that mechanical instead.

Two rules for the column:

- **Distinguish measured from assumed, always.** An assumed row is not a defect — it is a
  verification task with a known owner. An assumed row *recorded as measured* is how 0015 broke.
- **Prefer a fact that a command can re-check.** Where a `pixi run vendor-verify` check exists, cite
  it by name; that turns "is this still true?" into something a future session runs rather than
  re-derives. See [`../documentation-lessons.md`](../documentation-lessons.md).

## Rules

- **Never edit a decision to change its meaning.** Supersede it with a new record and set the old
  one's status. The reasoning that was wrong is as useful as the reasoning that was right — three
  findings in the evaluation that produced these records were confidently wrong before they were
  checked, and knowing that is what stops them being re-derived.
- **Cite evidence, not preference.** "Chat and codebase sessions produce byte-identical bindings"
  beats "these feel redundant."
- A decision that no issue or spec section cites is either not a decision or not yet applied.

## Index

Generated from the records themselves — number, title (the record's own heading), status. Do not
edit the table; edit the record and run `pixi run gen-register` (`completeness.py` STEP 12 fails
the build when it is stale). The hand-written summary column this table used to carry was a second
copy of every record, retired by #952: the record is the only place a decision is stated.

<!-- generated: decisions-index (pixi run gen-register; edits here are overwritten) -->

| # | Title | Status |
|---|---|---|
| [0001](0001-two-nouns-workflow-and-session.md) | Two nouns: workflow and room | accepted |
| [0002](0002-one-vocabulary.md) | One vocabulary, no translation map | accepted |
| [0003](0003-templates-collapse-to-three-shapes.md) | Templates collapse to three shapes | accepted |
| [0004](0004-permission-scopes.md) | Permissions scope by project, room and step | accepted |
| [0005](0005-seam-milestones.md) | Capability milestones alternate with seam milestones | accepted |
| [0006](0006-visual-direction-quiet.md) | Visual direction is "Quiet" | accepted |
| [0007](0007-background-work-inline-and-dedicated.md) | Background work surfaces both inline and on a dedicated surface | accepted |
| [0008](0008-runtime-streaming-over-append-log.md) | Runtime: live streaming over a durable append log | accepted |
| [0009](0009-session-lifecycle-and-retention.md) | Session lifecycle & retention: a tree you count the top of | accepted |
| [0010](0010-skills-and-advisor.md) | Worker capabilities are skills; the advisor is the first one | accepted |
| [0011](0011-token-based-context-management.md) | Context management is token-based, per worker, not turn-based | accepted |
| [0012](0012-what-aer-flow-is.md) | What Baton is | accepted |
| [0013](0013-room-is-the-user-facing-noun.md) | Room is the user-facing noun; session is the vendor's | accepted |
| [0014](0014-shapes-are-a-list-not-a-canvas.md) | A shape is an ordered list that renders as a graph | accepted |
| [0015](0015-three-kinds-of-needs-you.md) | A pause asks for one of three things: permission, a decision, or approval | accepted; **mechanism amended by [0029](0029-the-gate-is-three-mechanisms.md)** |
| [0016](0016-memory-is-room-owned.md) | Memory belongs to the room, not the worker | accepted |
| [0017](0017-vendor-model-effort-are-three-choices.md) | Vendor, model and effort are three separate choices | accepted |
| [0018](0018-attention-is-the-primary-signal.md) | Attention is the primary signal: state orders the list, notifications never decide | accepted; **notification source supplied by [0030](0030-aer-is-its-own-notifier.md)** |
| [0019](0019-consulting-is-not-deciding.md) | Consulting is not deciding: you can ask anyone, and the gate stays open | accepted |
| [0020](0020-one-state-machine.md) | One state machine: every surface renders the room's state, none derives its own | accepted; **amended 2026-08-14 (#1219) — tenth state `stopped`, derived with the room's §15 lock** |
| [0021](0021-artifacts-are-files.md) | Artifacts are files: vendor-neutral, versioned, attributed, and never silently overwritten | accepted |
| [0022](0022-permission-ladder-and-denial-is-an-answer.md) | The permission ladder is offered at the moment of asking, and a denial is a real answer | accepted; **cross-room rung held by [0052](0052-the-ladder-ships-without-the-cross-room-rung.md)** |
| [0023](0023-effort-and-models-are-named-by-behaviour.md) | Effort is named by behaviour and models are offered by purpose, never by a vendor's own string | accepted |
| [0024](0024-commands-are-namespaced.md) | Commands are namespaced by owner, and `/ask-all` is the broadcast | accepted |
| [0025](0025-a-step-is-an-instruction-with-a-gate-toggle.md) | A step's instruction is its body, and "ask me first" is a toggle on the step | accepted |
| [0026](0026-running-out-of-plan-is-a-state-not-a-failure.md) | Running out of plan is a state with a reset time, not a generic failure | accepted |
| [0027](0027-context-is-per-worker.md) | The mechanics behind 0011's per-worker unit and announced-choice trigger | accepted |
| [0028](0028-no-permissive-control-is-the-default.md) | Visual rank is a decision: no permissive control is ever the default | accepted |
| [0029](0029-the-gate-is-three-mechanisms.md) | The gate is three mechanisms with three populations, not one (amends 0015) | accepted |
| [0030](0030-aer-is-its-own-notifier.md) | AER is its own notifier: no vendor event announces a pause (amends 0018) | accepted |
| [0031](0031-skills-are-account-wide.md) | Skills are account-wide, not project-scoped | accepted |
| [0032](0032-room-orchestrator-is-mandatory.md) | A room always has exactly one orchestrator | accepted |
| [0033](0033-skills-attach-directly-no-persona.md) | Skills attach directly to a worker; there is no Persona object | accepted |
| [0034](0034-project-permission-ceiling-lives-in-aers-own-config.md) | A project's permission ceiling lives in AER's own config, not the repo | accepted |
| [0035](0035-aer-yield-is-a-structured-mcp-tool-not-a-sentinel.md) | `aer yield` is a structured MCP tool call, not a text sentinel | accepted |
| [0036](0036-shape-is-rendering-not-a-second-state-machine.md) | A shape's state is Flow's existing state, rendered differently; not a second state machine | accepted |
| [0037](0037-permission-answers-never-share-the-turn-lock.md) | A permission answer must never share the per-session turn lock | accepted |
| [0038](0038-a-reviewer-verdict-never-calls-aer-decide.md) | A reviewer's verdict is evidence for a human decision, never the decision itself | accepted |
| [0039](0039-dialogue-turns-use-vendor-session-continuation-not-full-history-resend.md) | A dialogue turn resumes the vendor's own session; it does not resend the transcript | accepted |
| [0040](0040-needs-you-groups-by-kind-and-actions-alone-defer.md) | Within "needs you," gates group by kind, and only an action can say "later" | accepted |
| [0041](0041-phone-authoring-lands-with-shapes-not-after.md) | Phone template authoring ships with the shapes milestone, not deferred past it | accepted |
| [0042](0042-retry-backoff-is-a-derived-obligation-with-steady-default.md) | Retry backoff is a derived obligation, and an unspecified backoff means steady, not immediate | accepted |
| [0043](0043-structured-verdict-is-shape-checked-evidence.md) | A review verdict is a schema'd contract output, shape-checked by the engine and never read by it | accepted |
| [0044](0044-memory-belongs-to-the-room-and-changes-only-by-decision.md) | Memory belongs to the room, and changes only by decision | accepted |
| [0045](0045-the-product-is-baton-the-journal-is-the-ledger.md) | The product is Baton; the journal is the ledger; the CLI token stays `aer` | accepted |
| [0046](0046-a-room-is-a-container.md) | A room is a container; work nests, not places | accepted |
| [0047](0047-workflow-templates-are-data-over-roles.md) | Workflow templates are data composed over the role catalog | accepted |
| [0048](0048-oversized-worker-input-travels-in-a-file.md) | Oversized worker input travels in a file the worker reads, not a bigger command line | accepted |
| [0049](0049-the-wake-loop-is-in-contract-and-the-orchestrator-decides.md) | The wake loop is in-contract, and the orchestrator is a resident presence that decides | accepted |
| [0050](0050-vendor-memory-is-isolated-scratch.md) | Vendor memory is isolated scratch; room memory is the only durable layer | accepted |
| [0051](0051-markdown-rendering-is-a-defined-subset-parsed-per-platform.md) | Markdown rendering is a defined CommonMark subset, parsed per platform, with no remote content | accepted |
| [0052](0052-the-ladder-ships-without-the-cross-room-rung.md) | The ladder ships without the cross-room rung; its home is project scope, not account scope | accepted |
| [0053](0053-room-event-appends-take-their-own-lock.md) | Room-event appends take their own lock, not the flow engine's | accepted |
| [0054](0054-participants-turns-and-addressing.md) | Participants, turns, and addressing: the multi-worker room's nouns | accepted |
| [0055](0055-an-authority-grant-is-not-a-standing-permission.md) | An authority grant is not a standing permission | accepted |
| [0056](0056-a-room-carries-its-own-worker-bindings.md) | A room carries its own worker bindings | accepted |

<!-- /generated: decisions-index -->
