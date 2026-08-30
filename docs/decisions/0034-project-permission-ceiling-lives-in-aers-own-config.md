# 0034 — A project's permission ceiling lives in AER's own config, not the repo

Status: accepted
Date: 2026-07-26

## Context

[0004](0004-permission-scopes.md) established the three-scope permission model — project ∩ session ∩
step, always narrowing — and named the project ceiling as *"where trust actually lives... stable,
outlives any session."* It also explicitly left one obligation unresolved: *"decide where a project's
ceiling is stored and how it is presented on first use of a folder — a trust prompt is the obvious
shape, and there is currently no Settings surface to host any of it."*

A full design-readiness pass before closing #574/#575 surfaced this as one of the last genuinely
undecided items blocking a clean handoff into implementation planning.

## Decision

**The ceiling lives in AER's own app-level config, keyed by the project's path — never a file
committed inside the project's own directory.**

- **First presented as a trust prompt** the first time AER is pointed at a folder it has not seen
  before — matching 0004's own instinct for the obvious shape, and consistent with
  [0009](0009-session-lifecycle-and-retention.md)'s framing of a room's directory as something the
  person chooses each time, not something AER assumes.
- **Reachable afterward from a global Settings surface** — a "Projects" list naming every folder AER
  has a stored ceiling for, so a person can find and revise a ceiling they set once and forgot about,
  the same way 0022 already requires a standing permission to be "found and revoked in settings."
- **Not a repo dotfile.** AER already commits to never placing anything into a project's own directory
  unprompted (Credential Isolation, `CLAUDE.md` Architecture Rules) — a permission ceiling stored
  inside the repo would violate that same instinct, and would silently become a *shared, committed*
  policy the moment it was checked in, applying to teammates who never set it and may not agree with
  it. The ceiling is personal to whoever is running AER, not a team convention.

## Rests on

| fact | how we know | if false |
|---|---|---|
| AER never writes a credential into a directory a vendor owns | **measured** — Architecture Rule 4, enforced by `VendorCredentialIsolationTests` | storing the ceiling in a vendor-owned file would be permissible, and this record's central placement argument dissolves |
| `CLAUDE_CONFIG_DIR` redirects session storage but **not** the subscription login | **measured** — `durability.config-dir-redirect-breaks-auth` | per-worker config roots behave differently than assumed, and the "never a file the vendor owns" boundary needs re-deriving |
| `--allowedTools` is pre-approval and routing, not a security ceiling | **measured** — `gate.allowedtools-is-preapproval-not-ceiling` (#529) | a vendor flag could express the ceiling directly, and AER-side storage is redundant rather than necessary |

## Consequences

**Easier.** The project's own directory stays untouched by AER's own bookkeeping — nothing to
`.gitignore`, nothing that could be accidentally committed and silently bind a permission policy onto
someone else's checkout of the same repo.

**Harder.** Two machines running AER against clones of the same repo path do not share a ceiling —
each has its own, keyed by that machine's own AER config root. If the same person works from two
machines, or a team wants a shared, repo-travelling ceiling, that is a different, undesigned feature
(closer to Claude Code's own team-shareable `.claude/settings.json` convention) — not what this record
covers, and not assumed here.

**Obliges us to.** Build the "Projects" Settings surface 0004 already noted doesn't exist yet, and key
storage by an absolute, normalized project path so the same folder reopened from a different room
still finds its ceiling.

Related: [0004](0004-permission-scopes.md) (the scope model this resolves one open obligation of),
[0022](0022-permission-ladder-and-denial-is-an-answer.md) (the "found and revoked in settings"
requirement this record's Settings surface satisfies).
