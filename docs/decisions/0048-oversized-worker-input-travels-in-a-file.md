# 0048 — Oversized worker input travels in a file the worker reads, not a bigger command line

Status: accepted
Date: 2026-08-02

Builds on the command-line ceiling guard (#598 Windows / #612 POSIX — a dispatch past `ARG_MAX` is
refused up front with a typed `CommandLineTooLongException`), the review lane's file-passing pattern
([0047](0047-workflow-templates-are-data-over-roles.md) / #789 — a worker reads the diff as a file
under its read-files grant), and [0004](0004-permission-scopes.md) (the read-files grant that makes
that safe). Motivated by #932 and #778 (a resident orchestrator composing rich worker prompts).

## Context

A worker's prompt reaches the vendor CLI as a single command-line argument (`ClaudeWorkerAdapter`
builds `-p <prompt>`; the Gemini adapter is the same shape). The assembled command line is guarded by
`CoreDispatcher`'s ceiling: an over-long dispatch is refused up front with a typed
`CommandLineTooLongException` (#598/#612). The guard is correct — it fails loudly, never silently
truncates — but it can only *refuse*, never *deliver*: the aer-core spawn ABI (`AerTask`, `ffi.rs`)
carries only `program` + `args`, and both spawners explicitly `.stdin(Stdio::null())`.

#932 proposed closing that gap by growing the spawn ABI — add an stdin / input-bytes channel so the
prompt could be delivered off argv. Before committing to an M4 ABI bump, two things were measured.

**What overflows is content, and content already has a home.** The live overflow that motivated
#932 — a code-review worker whose prompt inlined a ~670-line diff, assembling to 46,068 chars past
the 32,000 ceiling — is a *content* overflow (a diff), and content is exactly what the review lane
already delivers as a **file the worker reads** under its read-files grant (#789). The instruction
proper is small; the bulk is always captured content (a diff, a spec, a prior phase's output), which
is file-passable. A "prompt itself, stripped of content, too big for argv" case is hypothetical — and
where it isn't (a long conversation to continue), the vendors' own `--resume` / `--continue` is the
native path, not prompt-stuffing.

**The off-argv channel would be vendor-asymmetric anyway.** Measured, live, control-armed (#934):
`claude -p` reads the prompt from stdin (confirmed end-to-end with `--output-format stream-json
--verbose`, AER's real format); `agy`'s `-p` / `--print` is a string flag whose *value* is the prompt
and reads nothing from stdin — no stdin-as-prompt, no stdin-as-context, no `--input-format`, no
prompt-file flag. So the channel would lift the ceiling for claude only; a large gemini prompt would
stay argv-bound regardless.

## Decision

**Oversized worker input is delivered as a file the worker reads under its read-files grant — not by
growing the spawn ABI. The command-line ceiling stays a guard, not a channel to widen.**

1. **File-passing is the standard for large content, both vendors.** The pattern the review lane
   already uses (#789) is the general answer: hand a worker large content as a file it reads, keep the
   prompt a small instruction that points at it. It is vendor-symmetric (both CLIs read granted files),
   needs no ABI change, and is already proven. *Automated engine-side 2026-08-05 (#748): past
   `CoreDispatcher.OversizePromptThreshold` the dispatcher delivers the prompt through its
   already-captured `prompt.txt` and a short wrapper on argv — orchestrators no longer hand-roll this,
   and the ceiling still guards every non-opted-in path.*

2. **The spawn ABI stays argv-only.** The #932 stdin / input-bytes channel is **rejected for now**. It
   is a claude-only convenience (agy cannot read a piped prompt), largely redundant with file-passing,
   and its cost is the repo's heaviest change class — an M4 `AerTask` ABI bump, a strict P/Invoke match,
   and a concurrency-sensitive stdin writer (the full-pipe deadlock case needs its own test). Not worth
   it against a hypothetical it mostly would not serve.

3. **The loud refusal teaches the fix.** `CommandLineTooLongException`'s message stops saying only
   "shorten the prompt" and points the caller at file-passing — hand the large content to the worker as
   a file it reads. The guard already fails loudly; now it also names the available path.

4. **Revisit trigger, recorded not assumed.** Build the stdin channel only if #778's orchestrator
   surfaces a *concrete* need for deterministic in-context delivery to claude workers — where a
   file-read round-trip or a partial read is a measured reliability problem — not on the general
   possibility. The claude stdin + stream-json path is already measured viable (#934), so a revisit
   starts from a known-good precondition rather than re-measuring.

## Rests on

| fact | how we know | if false |
|---|---|---|
| The refusal message today tells the operator to *shorten the prompt*, not to pass content as a file | **measured** — the three `CoreDispatcher` throw sites (`GuardCommandLineLength` / `GuardPosixArgumentLength` / `GuardPosixTotalLength`) all end "a worker's prompt is passed inline as one argument, so that is usually the one to shorten" | the message improvement is unnecessary |
| A worker reads large content handed to it as a file under its read-files grant | **proven** — the review lane (#789) passes the diff this way, and the #934 review itself was dispatched by pointing the reviewer at files rather than inlining a 670-line diff that would breach the ceiling | file-passing does not actually cover the content case, and the ABI channel is needed |
| The live overflow that motivated #932 was content (a diff), not instruction | **measured** — #932's own body records the 46,068-char case as a ~670-line inlined diff | the "prompt itself" overflow is real and file-passing cannot reach it |
| `claude -p` reads the prompt from stdin (incl. stream-json); `agy -p` reads nothing off argv | **measured, control-armed** — `verify.py::lifecycle.claude-print-reads-prompt-from-stdin`, `::agy-print-requires-prompt-argument` (#934) | the ABI channel would be vendor-symmetric and more clearly worth building |
| The aer-core spawn ABI is argv-only; both spawners null stdin | **measured** — `AerTask` carries `program` + `args`; the spawners `.stdin(Stdio::null())` (external/aer-core `ffi.rs`) | a stdin channel already exists and no ABI bump is needed to try it |

## Consequences

**Easier.** No M4 ABI bump, no P/Invoke change, no concurrency-sensitive stdin writer to build and
test. One delivery pattern (file-passing) serves both vendors, and the loud refusal now teaches it
instead of sending the operator to trim a prompt that content-passing would have fixed.

**Harder / the cost.** claude workers do not get deterministic in-context large-input delivery; a
large-content task depends on the worker reading the file it is pointed at (a tool-call round-trip,
and the worker's cooperation). If #778 turns out to need determinism there, the ABI work returns —
deferred, not designed out.

**Obliges us to** make `CommandLineTooLongException`'s message point at file-passing (with a test that
the guidance is present), keep this record as the durable home of the revisit trigger, and re-open the
ABI question only against a concrete #778 need rather than the general possibility.

**Relates to** [0047](0047-workflow-templates-are-data-over-roles.md) / #789 (the file-passing pattern
it generalizes), [0004](0004-permission-scopes.md) (the read-files grant that makes it safe), the
#598/#612 ceiling guards (which stay), and #778 (the orchestrator whose future need is the only thing
that reopens this).
