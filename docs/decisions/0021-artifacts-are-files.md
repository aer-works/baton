# 0021 — Artifacts are files: vendor-neutral, versioned, attributed, and never silently overwritten

Status: accepted
Date: 2026-07-24

## Context

The product's premise is more than one vendor in one conversation
([0012](0012-what-aer-flow-is.md)). That premise fails at the first handover unless the *things
workers produce* can move between them.

A message in a vendor's transcript cannot move. It is trapped in that vendor's format, addressable
only through that vendor's session. A file can. The corpus is direct about this in
[`03-interaction-depth.md`](../design/03-interaction-depth.md):

> An artifact is a file on disk, not a message in a vendor's transcript. That is what makes it
> portable: claude writes it, antigravity edits it, antigravity reads it, and **none of them needs
> the others' conversation format**.

The engine already stores artifacts per execution, so the storage half exists. What did not exist is
the *model*: artifacts as objects a person picks up, versions, attributes, hands over, and decides
what to do with. [`04-workers-commands-control.md`](../design/04-workers-commands-control.md) frames
the question the owner actually asked — should AER's own documents be abstracted away? — and answers
it with a distinction:

> **The mechanism should be; the documents should not.** Nobody should ever see an execution
> directory — but a plan you can read, version and hand to another vendor **is the work itself**.

Two settled calls hang off that, and both were absent from the repo: what the file list contains, and
what happens when a working document is saved into the project. The second is the sharp one — the
corpus's stress test lists it among the *"things I put on a screen without thinking them through"*:
*"drawn as a one-way door with no thought about collisions, overwrite, or what happens when the
project's copy has since changed. **It is a merge problem wearing a button.**"*

## Decision

**An artifact is a file. Files are the product's currency, and the room presents them as one list
with one distinction that matters.**

**1. Vendor-neutral, versioned, attributed, explicitly attached.** Anything one worker made, any
other worker can be handed. Every version records who produced it and when, so *"what did the second
vendor actually change"* is a diff rather than a re-read — the corpus notes that handing a file to a
second vendor *"is worthless if you cannot see what came back different."* Attachment is **explicit
and visible before sending**, because *"which version of the plan did antigravity actually see"* is
precisely the question that becomes unanswerable if attachment is implicit.

**2. Documents stay, plumbing goes.** One file list. The only distinction drawn is **"in your
project" or not** — a real, consequential difference, because one is in your git history and the
other is not. Execution directories, execution numbers, and wherever AER stashed something are
**never surfaced anywhere**. Both kinds get the same affordances: versions, attribution, diffs,
send-to-a-worker. That uniformity is what makes cross-vendor work feel like one product rather than a
pipeline — handing over a source file and handing over a plan are the same gesture.

**3. Saving into the project is diff-and-choose, never overwrite by default.** From
[`06-answers.md`](../design/06-answers.md):

- Target does not exist → write it, and the room says where.
- Target exists and differs → **show the diff**, and offer three explicit choices: replace, save
  alongside under a new name, or cancel.
- The project's copy changed *after* the working document was derived from it → **say so
  specifically**, because that is the case where replacing quietly destroys someone's work.
- **The destructive option is never the default button.**

The corpus's reasoning is the part worth keeping: *"this is a merge problem, and the honest design is
to refuse to guess: show both, let the person choose."* Saving into the project is the one-way door
where something enters your repository, and it should be a decision rather than a side effect.

## Consequences

**Easier.** Cross-vendor work becomes reviewable rather than hopeful — a review trail instead of
copy-paste, which the corpus lists as differentiating claim **04**, *files that move between vendors,
with receipts*. It also gives [0016](0016-memory-is-room-owned.md) somewhere to live at no extra
conceptual cost: room memory is *"a working document, so it needs no new concept — it appears in the
files list, has versions and attribution, can be opened and edited, and can be saved into the project
if you want it to become a real file."* One model serves both.

**Harder.** Versioning and attribution are now obligations on every artifact write, including ones
produced mid-turn by a worker that does not know it is being recorded. Diffing must work between
arbitrary versions and across vendors, on both surfaces — and on a phone an artifact needs its own
screen ([`03-interaction-depth.md`](../design/03-interaction-depth.md): *"reviewing on a phone is
realistic, editing is not"*). The save-into-project flow is genuinely a merge UI, which is more work
than a button, and the divergence check requires knowing what the project's copy looked like **at
derivation time** — a provenance link that has to be recorded when the working document is created,
not reconstructed later.

**Obliges us to** keep execution directories out of every surface; give project files and working
documents identical affordances; record per-version authorship on write rather than inferring it;
attach explicitly and show the attachment before sending; and never make replace the default action
when saving into a project — including when the target is unchanged, since "unchanged" is a claim
that can be wrong.

**Relates to** [0016](0016-memory-is-room-owned.md), which is an instance of this record rather than a
separate mechanism. [0019](0019-consulting-is-not-deciding.md) depends on it: the evidence bundle a
consulted worker receives is *"the raising turn and its attachments verbatim"*, and an attachment
being a portable, versioned file is what makes the second opinion form on the same evidence rather
than on a paraphrase. [0012](0012-what-aer-flow-is.md) is what it serves.

Related: `#377` (the visual diff viewer — this record is the model beneath it), `#424` (AER's state as
a queryable context source), `#442` (what memory a worker gets).
