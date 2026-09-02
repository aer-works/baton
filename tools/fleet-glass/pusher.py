"""Fleet Glass pusher: derive the fleet snapshot via `baton mcp` (stdio MCP, #1458: folded from the
standalone Baton.Mcp.Host binary into a Baton.Cli verb) and scan ~/.baton/rooms
for terminal-room deliverables, then POST both outbound to the Cloudflare mailbox Worker (worker.js)
every ~25s. Moved into the repo, with the deliverables inbox added, by aer-works/baton#1413.

Outbound-only; the machine running this accepts no inbound connections.

THE SNAPSHOT HALF -- change-gated (#1457) and coalescing-floored (#1538)
-------------------------------------------------------------------------
The wrapped {rooms, underhood, timelines, stale_hidden_count} body is hashed (stable, sort_keys)
before every POST; a hash that matches the last SUCCESSFUL push's (persisted in push_state_file, key
SNAPSHOT_HASH_KEY) skips the POST. Cloudflare's KV free tier caps at 1,000 writes/day and worker.js's
/push handler is an unconditional env.FLEET.put per POST -- pushing an unchanged snapshot every
interval_seconds (default 25s) burns 3,456 writes/day against that cap for nothing. A missing/
unreadable persisted hash always re-pushes (fail toward one extra write, never toward silence, same
posture as the deliverables state file below); a FAILED POST never persists the hash, so the next
cycle retries. See `snapshot_hash` / `should_push_snapshot`.

COALESCING FLOOR (#1538): when the change-gate says CHANGED, push only if >= min_push_interval_s
(default 90s) since the last actual push; otherwise log `coalesced (Ns since last push)` and let
the next cycle retry. Continuous-change days are capped at <=960 writes/day worst case.

SINGLE-INSTANCE GUARD (#1538): on startup, atomically claim pusher.lock (O_EXCL-style create).
If the lock exists and its PID is alive with 'pusher' in its command line, terminate-and-replace it
(deploys always win). If the PID is dead or not a pusher, log and reclaim. Release on clean exit.

`timelines` and `stale_hidden_count` (#1505) are both frozen, append-only-derived facts computed
once per cycle from on-disk state (flow.jsonl event counts, the stale-room filter) -- neither reads
`now()` beyond what `drop_stale_rooms`'s own cutoff already did, so neither field makes the hash
churn on wall-clock time alone; the change-gate above still only re-pushes on a real content change.
See "THE TIMELINE HALF" below for the KV-write arithmetic this adds.

Config comes from pusher.config.json next to this script (gitignored, machine-local -- ship
pusher.config.example.json and copy it):
    {
      "dll": "<path to Baton.Cli.dll (baton mcp)>",
      "push_url": "https://.../push/<PUSH_TOKEN>",
      "deliver_url": "https://.../deliver/<PUSH_TOKEN>",   # optional; derived from push_url if absent
      "heartbeat_url": "https://.../heartbeat/<PUSH_TOKEN>", # optional; derived from push_url if absent
      "interval_seconds": 25,
      "min_push_interval_s": 90,                          # optional; coalescing floor for snapshot pushes
      "lock_file": "pusher.lock",                          # optional; defaults next to this script
      "roots": [],
      "max_age_days": 3,
      "rooms_root": "~/.baton/rooms",                         # optional; defaults there
      "secret_patterns_file": "secretpatterns.local.txt",    # optional; defaults next to this script
      "push_state_file": "push-state.local.json",            # optional; defaults next to this script
      "underhood_dirs": []
    }
push_url (and deliver_url/heartbeat_url, if set) embed the push token -- the config file is a local
secret; never print or commit it.

THE TIMELINE HALF (#1505, extended by #1613 item 4)
-------------------------------------------
Pre-#42 (the daemon has not yet been given the projection job, spec/baton.md §7), this pusher gets
per-room timelines the same way it gets the fleet snapshot: one `room_detail` call per room each
cycle its timeline can still change -- every cycle for a non-terminal room, exactly once per process
lifetime for a terminal one (see `resolve_room_timeline`'s own docstring for the caching policy) --
through the SAME dotnet-mcp process `derive_snapshot_and_timelines` already spawns for
`fleet_status` -- never a second `dotnet` spawn per room. `extract_timeline` keeps only a fixed,
named set of content-free fields off each entry (see its own docstring for exactly which -- not
restated here, so this paragraph cannot go stale the way it once did when that set grew); `room_detail`'s
`stdout` field and any `note`/`detail`/`error` text are dropped unconditionally, so stdout can never
ride the mailbox through this path -- see the module's secret gate above for why that boundary exists
at all. Capped at the last TIMELINE_CAP (30) entries per
room: a lane's timeline is step-level transitions (dispatch, execution start/exit, retries, decisions)
written a handful of times per step, not a line per stdout write -- a lane produces tens of these
over its life, not thousands, so this rides the mailbox safely under the same 1,000-write/day KV
budget the change-gate above protects, and 30 is generous headroom over what a normal lane emits
before terminating. Keyed by room PATH, never room NAME (#1505 review note: fleet_status dedupes
rooms by path, so two same-named rooms under different roots are distinct entries; a name-keyed join
would hand one room's timeline to the other -- exactly the wrong-and-confident failure mode #41's
removal below exists to stop, reintroduced by a careless join).

THE HEARTBEAT HALF (#1486), extended by #1613 item 2
-------------------------------------------
The change-gate above makes pushed_at legitimately stale on a quiet fleet, and nothing distinguishes
that from a dead pusher. Independent of the gated snapshot, this loop also POSTs a timestamp ping to
worker.js's /heartbeat route at a coarse fixed cadence -- hourly, tracked in push_state_file under
HEARTBEAT_STATE_KEY. Arithmetic: 24 writes/day at hourly cadence, against the same 1,000/day KV
free-tier cap the change-gate protects; combined with the change-gated snapshot writes (worst case
one per interval_seconds when the fleet is constantly changing) this adds a small, fixed floor that
never scales with polling frequency. Same save-only-after-success discipline as
push_snapshot_and_record: POST first, record the timestamp only afterwards, so a failed heartbeat
retries next cycle instead of silently going stale. Heartbeat failures are logged and never raise
into the snapshot path -- see main()'s heartbeat try/except, which runs in its own block after the
snapshot has already been sent.

Pre-#1613 this body was a literal "{}"; it now carries `{"derived_at": ...}` -- an ISO timestamp
naming when THIS process's snapshot derivation last completed, not a deliverable, so it still does
not pass through the secret gate below (nothing in it that gate exists to catch). The Worker still
stamps its OWN receipt time server-side for heartbeat_at (see worker.js's /heartbeat handler);
derived_at travels inside the body precisely because — unlike heartbeat_at — it names a fact only
the pusher itself knows. The same endpoint is now ALSO hit on a second, independent, more frequent
cadence (`should_send_derived_ping`, "derived_at" section below) whenever a snapshot push hasn't
already delivered a fresher derived_at recently -- see that section for why this does not blow the
write budget above.

THE DELIVERABLES HALF (#1413 half 2)
-------------------------------------
Each run walks every TERMINAL room under rooms_root (a room with a terminal.json) and, for each,
uploads ONLY that room's declared output artifact(s) -- terminal.json's own "outputs" list, which is
exactly the room's `--output` file(s) -- plus a small verdict summary (state/error/try). NEVER
prompt.txt, NEVER .stdout.log, NEVER the rest of the artifacts directory; `declared_outputs` below is
the sole source of what gets read.

Before any deliverable content is uploaded it passes the SECRET GATE (`secret_hit_index`): scanned
against a denylist of regex patterns loaded from secret_patterns_file (gitignored -- the patterns
themselves are sensitive, since they reveal what to grep for; ship secretpatterns.example.txt with
generic placeholders instead). On a hit, the real content is replaced with a stub naming which
pattern index matched, and the hit is logged. If the patterns file is MISSING or UNREADABLE, this
fails CLOSED: every deliverable in that run is withheld, stub included, until an operator fixes it --
see `load_secret_patterns`'s docstring for why that state is deliberately never memorized as "done".

Dedupe is per (room_path, artifact, content-hash) -- `push_state_file` (gitignored) remembers the hash
last pushed for each (room_path, artifact) pair, and a run that finds an unchanged hash skips re-pushing
it (matching the snapshot half's path-keyed join, #1617; room name is kept for display only). A room
with zero declared outputs (typically a Failed room) still gets ONE deliverable, carrying
only the verdict summary, so a failure with nothing to show is still visible in the inbox.

WRITE BUDGET, KEY MIGRATION, & BATCH CAPPING (#1617, PR #1632):
Steady-state deliverables cost ~tens of writes/day against Cloudflare's 1,000/day free-tier KV cap
(each /deliver POST of K items costs K+1 KV writes: K for inbox:item:<id> and 1 for inbox:index).
When keys migrated from room_name to room_path, `gather_deliverables` automatically migrates legacy
`f"{room_name}::{artifact}"` entries on load under their respective room_path keys and drops the old
keys, stamping `__format_version__ = 2`. This avoids an all-at-once re-push storm of already-delivered
history (measured at 210 deliverables / 211 KV writes worst case on this machine without migration).
To protect against retry storms on network errors or payload cap violations (>5MB body cap), deliver
POSTs are capped at DEFAULT_DELIVER_BATCH_CAP (10 items = 11 KV writes per cycle). A backlog drains
across successive cycles at <=26 writes/min, and a failing batch retries only its own 10 items rather
than an unbounded full-fleet burst.

A PER-ITEM pattern hit IS memorized as pushed, unlike the missing-patterns-file case: its stub was
delivered, and not memorizing it would re-send that stub every cycle. The trade-off is that a
false-positive match does not self-heal when the offending pattern is later narrowed -- to re-offer
such an item, delete its (room, artifact) entry from push_state_file, or touch the artifact so its
hash changes.

Usage: python pusher.py [--once] [--selftest]
Writes pusher.log (rotating-ish: truncated at 1MB) next to this script.
"""

from __future__ import annotations

import atexit
import hashlib
import json
import os
import re
import signal
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).parent
LOG = HERE / "pusher.log"

DEFAULT_ROOMS_ROOT = Path.home() / ".baton" / "rooms"
DEFAULT_SECRET_PATTERNS_FILE = HERE / "secretpatterns.local.txt"
DEFAULT_PUSH_STATE_FILE = HERE / "push-state.local.json"
DEFAULT_LOCK_FILE = HERE / "pusher.lock"


def log(msg: str) -> None:
    try:
        if LOG.exists() and LOG.stat().st_size > 1_000_000:
            LOG.write_text("", encoding="utf-8")
        with LOG.open("a", encoding="utf-8") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} {msg}\n")
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Single-instance guard (#1538)
# ---------------------------------------------------------------------------------------------

def _try_create_lock(lock_path: Path, pid: int) -> bool:
    try:
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
        fd = os.open(str(lock_path), flags)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(f"{pid}\n")
        except Exception:
            pass
        return True
    except (FileExistsError, OSError):
        return False


def read_lock_pid(lock_path: Path) -> int | None:
    try:
        raw = lock_path.read_text(encoding="utf-8").strip()
        return int(raw)
    except (OSError, ValueError):
        return None


def is_pid_alive(pid: int | None) -> bool:
    if pid is None or pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes
        kernel32 = ctypes.windll.kernel32
        SYNCHRONIZE = 0x00100000
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, False, pid)
        if not handle:
            if kernel32.GetLastError() == 5:  # ERROR_ACCESS_DENIED
                return True
            return False
        try:
            # 258 is WAIT_TIMEOUT (still active); 0 is WAIT_OBJECT_0 (signaled / exited)
            res = kernel32.WaitForSingleObject(handle, 0)
            return res == 258
        finally:
            kernel32.CloseHandle(handle)
    else:
        try:
            os.kill(pid, 0)
            return True
        except (OSError, ProcessLookupError):
            return False


def get_process_cmdline(pid: int) -> str:
    if pid <= 0:
        return ""
    if sys.platform == "win32":
        try:
            out = subprocess.run(
                ["wmic", "process", "where", f"ProcessId={pid}", "get", "CommandLine"],
                capture_output=True, text=True, timeout=5, check=False,
            )
            lines = [ln.strip() for ln in out.stdout.splitlines() if ln.strip() and ln.strip().lower() != "commandline"]
            if lines:
                return lines[0]
        except Exception:
            pass
        try:
            out = subprocess.run(
                ["powershell", "-NoProfile", "-Command", f"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine"],
                capture_output=True, text=True, timeout=5, check=False,
            )
            if out.returncode == 0 and out.stdout.strip():
                return out.stdout.strip()
        except Exception:
            pass
        return ""
    else:
        try:
            return Path(f"/proc/{pid}/cmdline").read_text().replace("\x00", " ")
        except Exception:
            return ""


def terminate_process(pid: int) -> None:
    if pid <= 0 or pid == os.getpid():
        return
    try:
        os.kill(pid, signal.SIGTERM)
    except (OSError, ProcessLookupError):
        pass
    for _ in range(20):
        if not is_pid_alive(pid):
            return
        time.sleep(0.05)
    if sys.platform == "win32" and is_pid_alive(pid):
        try:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, timeout=5, check=False)
        except Exception:
            pass


def acquire_lock(lock_path: Path, pid: int | None = None) -> bool:
    """Atomically claim lock_path with `pid`. If lock exists, check whether the holder is alive:
    if alive and its command line contains 'pusher', terminate-and-replace it (deploys always win);
    if dead or not a pusher, log and reclaim."""
    if pid is None:
        pid = os.getpid()

    if _try_create_lock(lock_path, pid):
        return True

    old_pid = read_lock_pid(lock_path)
    if old_pid is not None and old_pid != pid:
        if is_pid_alive(old_pid):
            cmdline = get_process_cmdline(old_pid)
            if "pusher" in cmdline.lower() and "claude" not in cmdline.lower():
                terminate_process(old_pid)
                log(f"replaced stale instance pid={old_pid}")
            else:
                log(f"reclaimed stale lock (pid={old_pid} not a pusher)")
        else:
            log(f"reclaimed stale lock (pid={old_pid} dead)")
    else:
        log("reclaimed unreadable stale lock")

    try:
        if lock_path.exists():
            lock_path.unlink()
    except OSError:
        pass

    if _try_create_lock(lock_path, pid):
        return True
    try:
        lock_path.write_text(f"{pid}\n", encoding="utf-8")
        return True
    except OSError as ex:
        log(f"failed to write lock file: {ex}")
        return False


def release_lock(lock_path: Path, pid: int | None = None) -> None:
    if pid is None:
        pid = os.getpid()
    try:
        if lock_path.is_file():
            cur_pid = read_lock_pid(lock_path)
            if cur_pid == pid:
                lock_path.unlink()
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Fleet snapshot (unchanged pipeline: derive via `baton mcp`, drop stale rooms, gather underhood)
# ---------------------------------------------------------------------------------------------

def rpc(proc: subprocess.Popen, req_id: int, method: str, params=None):
    msg = {"jsonrpc": "2.0", "id": req_id, "method": method}
    if params is not None:
        msg["params"] = params
    proc.stdin.write(json.dumps(msg) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("host closed stdout")
        line = line.strip()
        if not line:
            continue
        resp = json.loads(line)
        if resp.get("id") == req_id:
            return resp


TIMELINE_CAP = 30  # last N timeline entries kept per room -- see module docstring's "THE TIMELINE
                    # HALF" for why a lane's step-level event count stays well under this.


def is_terminal_room(room_path: str) -> bool:
    """A room is terminal once terminal.json exists -- the same fast-path fleet_status itself
    uses (spec/baton.md §6). Non-terminal rooms are re-fetched every cycle (their timeline keeps
    growing); a terminal room's flow.jsonl is already frozen, so #1613 fetches it through
    room_detail exactly ONCE (see `derive_snapshot_and_timelines`'s cache parameter) rather than
    either skipping it forever (the pre-#1613 behavior -- a finished room showed no timeline at
    all) or re-fetching frozen bytes every cycle for nothing."""
    try:
        return (Path(room_path) / "terminal.json").is_file()
    except (OSError, TypeError):
        return False


def extract_timeline(room_detail_result: dict) -> list[dict]:
    """Content-free timeline projection from one room_detail response: KEEP ONLY `type`,
    `timestamp`, `stepId`, and `exitCode` off each timeline entry. Does not enumerate fields to DROP
    (stdout, note, error, detail) -- it enumerates the four fields it KEEPS, so a future room_detail
    field never leaks through by accident of this function failing to name it. `stdout` is never
    read at all, whether or not room_detail's response carries one.

    `stepId`/`exitCode` (#1613 item 4) are admitted under the content ruling in spec/baton.md §6, not
    restated here.

    The synthetic "unreadable" entry (RoomDetailTool.ReadTimelineAsync, e.g. a held-open ledger) is
    kept as a type-only marker -- its `detail` (an exception message) is dropped like any other
    entry's, so the timeline still shows "something is wrong here" without smuggling free text.

    No event TYPE is excluded, deliberately (#1537): this function never inspects `type`'s value,
    only its shape (a string) -- so the vocabulary here is exactly whatever the engine journals,
    never a second, narrower list to keep in sync with FlowEvent/CoreEvent/RoomEvent. The selftest's
    "admits every event type unfiltered" check is what keeps that true; a future type-keyed filter
    would fail it.
    """
    timeline = room_detail_result.get("timeline")
    if not isinstance(timeline, dict):
        return []
    entries = timeline.get("entries")
    if not isinstance(entries, list):
        return []
    out = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        event_type = entry.get("type")
        if not isinstance(event_type, str):
            continue
        kept = {"type": event_type}
        timestamp = entry.get("timestamp")
        if isinstance(timestamp, str):
            kept["timestamp"] = timestamp
        step_id = entry.get("stepId")
        if isinstance(step_id, str):
            kept["stepId"] = step_id
        exit_code = entry.get("exitCode")
        if isinstance(exit_code, int) and not isinstance(exit_code, bool):
            kept["exitCode"] = exit_code
        out.append(kept)
    return out[-TIMELINE_CAP:]


# ---------------------------------------------------------------------------------------------
# Live telemetry for Running rooms (#1613 item 1, extended by this review's live-token finding and
# items 3/4's incremental reader): a tool-call count, claude-only live token counts, and a
# last-stream-activity instant, read directly off the currently-running execution's own
# already-captured .stdout.log -- no new `dotnet mcp` round trip, no engine change. Why pusher-side
# rather than engine-side (the ExecutionUsageProjector seam), and the token fields' exact gating and
# additive-vs-level semantics: spec/baton.md §6's `rooms[].live` schema entry, not restated here.
# ---------------------------------------------------------------------------------------------

def _running_execution_id(room: dict) -> str | None:
    steps = room.get("steps")
    if not isinstance(steps, list):
        return None
    for step in steps:
        if isinstance(step, dict) and step.get("state") == "Running" and isinstance(step.get("execution"), str):
            return step["execution"]
    return None


def _find_stdout_paths(room_path: str, execution_id: str) -> tuple[Path | None, Path | None]:
    """(stdout_path, rollover_path) for the Running execution's own captured stream. The same
    two-location fallback ArtifactManager/ExecutionUsageProjector use on the engine side (the live
    output directory, then artifacts/pruned for a retention-swept execution) -- mirrored here rather
    than shelling out, since the path shape itself (`artifacts/execution_<id>/.stdout.log`) is a
    stable, already-public on-disk contract (ArtifactManager.AllocateOutputDirectory /
    .ResolvePrunedOutputDirectory). `rollover_path` is the sibling `.stdout.log.1`
    ExecutionStreamLogger's single 8 MiB rollover produces in the SAME directory (#1613 review
    finding 3) -- None when no rollover has happened yet for this execution."""
    for relative in (f"artifacts/execution_{execution_id}", f"artifacts/pruned/execution_{execution_id}"):
        base = Path(room_path) / relative
        candidate = base / ".stdout.log"
        if candidate.is_file():
            rollover = base / ".stdout.log.1"
            return candidate, (rollover if rollover.is_file() else None)
    return None, None


def _read_new_lines(path: Path, offset: int) -> tuple[list[str], int]:
    """Complete lines appended to `path` since byte `offset` (#1613 review finding 4 -- read only
    the delta, never the whole file, every cycle), and the new offset positioned right after the
    last complete line consumed. A trailing partial line -- the vendor CLI mid-flush, no newline
    yet -- is left UNCONSUMED so it is read whole next cycle instead of split across two parses."""
    try:
        with path.open("rb") as f:
            f.seek(offset)
            chunk = f.read()
    except OSError:
        return [], offset
    if not chunk:
        return [], offset
    text = chunk.decode("utf-8", errors="replace")
    last_newline = text.rfind("\n")
    if last_newline == -1:
        return [], offset
    complete = text[:last_newline]
    consumed = len(complete.encode("utf-8")) + 1  # + the newline itself
    return complete.split("\n"), offset + consumed


def extract_live_counts(lines: list[str], seen_message_ids: set | None = None) -> dict:
    """A tool-call COUNT, plus live token/turn fields for BOTH vendors (#1682 -- agy's `step_update`
    usage was found live during that issue's own evidence gathering; the prior "agy has no usage to
    read" claim recorded here was wrong and is corrected in this same change), tolerant of a torn
    last line (the file is still being written) and of both vendors' stream envelopes:
      - claude: `type`-keyed; a completed `assistant` message's `message.content` array carries a
        `{"type": "tool_use", ...}` block per tool call -- shape measured against real #1559
        capture fixtures (tests/Baton.Cli.Tests/RunCommandEchoTests.cs). The SAME `assistant`
        message's `message.usage` object carries an output count plus, when the CLI reports the
        cache split, the three input-side counts a context figure needs -- the exact key names and
        where they were measured are spec/baton.md §6's `rooms[].live` entry, not restated here; see
        below for how each is used.
      - agy: `event`-keyed; a `step_update` heartbeat with `state` in `"DONE"`/`"ERROR"` (its terminal
        lifecycle states) and `step_type: "tool"` marks one completed real tool step -- #1686 review
        F3: mirrors the engine's own `ClaudeUsageParser.CountToolSteps` unit (spec/baton.md §3),
        shape measured live against agy 1.1.11 (AgyWorkerAdapter.TryParseProgressEvent's own #1088
        doc comment). A `step_update` with
        `state: "DONE"` and `step_type: "agent_response"` carries its own `usage` object
        (`input_tokens`/`output_tokens`) -- measured live against a real #1682 evidence capture
        (`dispatch-implement-38c24d11`).
    A line that fails to parse as JSON is skipped, not an error -- the vendor CLI may have flushed
    a partial line at the exact moment this read caught the file mid-write.

    Returns `{"toolCalls": int}` always, plus:
      - `"billedTokens"`: present only if at least one usage-bearing line in THIS batch reported
        one -- the SUM over the batch (additive: the caller accumulates this across every batch it
        has ever read for the execution, spec/baton.md §6), same `input + output [+ cache_creation]`
        formula the engine's own `TokenBudgetMonitor` arrests on (#1682) -- NOT `thinking_tokens`,
        which is a breakdown already counted inside `output_tokens` (measured against real #1682
        evidence: Σinput + Σoutput reproduces the vendor's own Σ`total_tokens` exactly). Whole-tree
        on claude, including subagent `assistant` events (they carry `parent_tool_use_id` but are
        not filtered out).
      - `"turns"`: present alongside `billedTokens` -- the COUNT of usage-bearing lines in this batch
        (additive, same convention).
      - `"context"`: `{"contextTokens": int, "cacheReadTokens": int}` from the LATEST claude
        `assistant` line in this batch that reports all three of `input_tokens`/
        `cache_read_input_tokens`/`cache_creation_input_tokens` together -- a LEVEL (the caller
        replaces, never sums, its own running value), claude-only (agy's step_update usage carries
        no cache-creation figure to build a comparable trio from, docs/vendor-capabilities.md).
        Absent when no line in the batch reports the full trio: never a partial or fabricated
        figure, and never built from `input_tokens` alone (summing that across turns would
        re-count each turn's whole repeated context -- the trap this field exists to avoid).

    `seen_message_ids` (#1686 review F6): claude can split one API response's usage across several
    consecutive `assistant` events sharing the SAME `message.id` and an IDENTICAL `message.usage`
    object -- measured against real `.stdout.log` captures (spec/baton.md §3: up to ~60% of
    usage-bearing lines on a real room are such repeats). Passing the SAME set across every batch this
    process has ever read for an execution (the caller-owned `live_cache` state,
    `live_telemetry_for_room` below) dedupes a repeat rather than summing it again; a line with no
    `message.id` (agy; claude's own terminal line is never read here) always accumulates. `None`
    (the default) dedupes only within this one call, for a caller with no cross-batch state to thread
    (a one-shot read, or a test).
    """
    tool_calls = 0
    billed_tokens = 0
    turns = 0
    usage_seen = False
    context = None
    if seen_message_ids is None:
        seen_message_ids = set()
    for raw_line in lines:
        line = raw_line.strip()
        if not line:
            continue
        try:
            evt = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(evt, dict):
            continue

        if evt.get("type") == "assistant":
            message = evt.get("message")
            content = message.get("content") if isinstance(message, dict) else None
            if isinstance(content, list):
                tool_calls += sum(1 for b in content if isinstance(b, dict) and b.get("type") == "tool_use")
            usage = message.get("usage") if isinstance(message, dict) else None
            message_id = message.get("id") if isinstance(message, dict) else None
            # #1686 review F6: a repeated message.id means this usage object was already summed off an
            # earlier chunk of the SAME API response -- skip it rather than double-counting.
            already_counted = isinstance(message_id, str) and message_id and message_id in seen_message_ids
            # #1686 review F13: register the id only once a usage object is actually in hand -- an
            # `assistant` line carrying an id but no usage must not poison the seen-set for the line
            # that later carries that same id's real usage (the engine only reaches its own set for a
            # line that already parsed as usage; this keeps both sides on the same registration point).
            if isinstance(usage, dict) and isinstance(message_id, str) and message_id:
                seen_message_ids.add(message_id)
            if isinstance(usage, dict) and not already_counted:
                out = usage.get("output_tokens")
                in_tok = usage.get("input_tokens")
                cache_creation = usage.get("cache_creation_input_tokens")
                cache_read = usage.get("cache_read_input_tokens")
                numeric = lambda v: isinstance(v, int) and not isinstance(v, bool)
                if numeric(out) or numeric(in_tok) or numeric(cache_creation):
                    billed_tokens += (out if numeric(out) else 0) + (in_tok if numeric(in_tok) else 0) \
                        + (cache_creation if numeric(cache_creation) else 0)
                    turns += 1
                    usage_seen = True
                if numeric(in_tok) and numeric(cache_read) and numeric(cache_creation):
                    context = {
                        "contextTokens": in_tok + cache_read + cache_creation,
                        "cacheReadTokens": cache_read,
                    }
        elif evt.get("event") == "step_update":
            step = evt.get("step_update")
            if isinstance(step, dict) and step.get("step_type") == "tool" \
                    and step.get("state") in ("DONE", "ERROR"):
                tool_calls += 1
            elif isinstance(step, dict) and step.get("state") == "DONE":
                if step.get("step_type") == "agent_response":
                    usage = step.get("usage")
                    if isinstance(usage, dict):
                        out = usage.get("output_tokens")
                        in_tok = usage.get("input_tokens")
                        numeric = lambda v: isinstance(v, int) and not isinstance(v, bool)
                        if numeric(out) or numeric(in_tok):
                            billed_tokens += (out if numeric(out) else 0) + (in_tok if numeric(in_tok) else 0)
                            turns += 1
                            usage_seen = True

    result = {"toolCalls": tool_calls}
    if usage_seen:
        result["billedTokens"] = billed_tokens
        result["turns"] = turns
    if context is not None:
        result["context"] = context
    return result


def _apply_live_delta(state: dict, delta: dict) -> None:
    """Merge one parsed batch (a rollover file or newly-appended live-file bytes) into a
    per-execution running state: `toolCalls`/`billedTokens`/`turns` ACCUMULATE (#1613 review findings
    3/4, extended to the #1682 fields the same way -- every batch this process has ever read for the
    execution), `context` is the latest LEVEL seen -- only overwritten when the batch actually reports
    one, so an empty or tool-only batch never blanks out a level that was already known."""
    counts = state["counts"]
    counts["toolCalls"] = counts.get("toolCalls", 0) + delta.get("toolCalls", 0)
    if "billedTokens" in delta:
        counts["billedTokens"] = counts.get("billedTokens", 0) + delta["billedTokens"]
        counts["turns"] = counts.get("turns", 0) + delta["turns"]
    if "context" in delta:
        state["context"] = delta["context"]


LAST_ACTIVITY_BUCKET_SECONDS = 90  # #1613 review finding 1: floor lastActivityAt's mtime to this
                                    # bucket BEFORE it enters the payload, so a continuously-
                                    # streaming lane's every-chunk mtime advance does not itself
                                    # change snapshot_hash every cycle (the #1457 change-gate) -- see
                                    # the module docstring's write-budget arithmetic. Quantizing
                                    # rather than excluding (unlike derived_at) is deliberate: a lane
                                    # that streams text without ever calling a tool would otherwise
                                    # change no field in `live` at all, so glass would keep rendering
                                    # a stale "active Nm ago" for a lane that is actually streaming.


def _quantized_activity_iso(mtime: float, bucket_seconds: float = LAST_ACTIVITY_BUCKET_SECONDS) -> str:
    bucketed = (mtime // bucket_seconds) * bucket_seconds
    return datetime.fromtimestamp(bucketed, tz=timezone.utc).isoformat()


def live_telemetry_for_room(room: dict, live_cache: dict | None = None) -> dict | None:
    """None when there is no Running step, or its execution has no captured stdout yet (dispatch
    just started) -- absent, never a fabricated zero, matching ExecutionUsageView's own
    never-null/never-fabricated convention on the engine side. `lastActivityAt`'s honesty property
    and the token fields' gating are spec/baton.md §6's `rooms[].live` schema entry, not restated
    here.

    `live_cache` is the caller-owned `(byte_offset, running_counts)` dict #1613 review findings 3/4
    need to avoid re-reading and re-parsing the whole `.stdout.log` every cycle -- the same
    caller-owned-dict pattern `terminal_timeline_cache` already uses. Keyed by `room_path::
    execution_id`, so a retry's fresh execution starts its own counters rather than inheriting a
    finished one's. Defaults to a fresh, single-call dict when omitted (tests, and any caller that
    genuinely wants a one-shot whole-file read -- offset 0 reading to EOF is equivalent)."""
    if live_cache is None:
        live_cache = {}
    execution_id = _running_execution_id(room)
    room_path = room.get("path")
    if execution_id is None or not isinstance(room_path, str) or not room_path:
        return None

    stdout_path, rollover_path = _find_stdout_paths(room_path, execution_id)
    if stdout_path is None:
        return None

    try:
        mtime = stdout_path.stat().st_mtime
        current_size = stdout_path.stat().st_size
    except OSError:
        return None

    key = f"{room_path}::{execution_id}"
    state = live_cache.setdefault(key, {
        "stdout_offset": 0, "rollover_offset": 0, "counts": {"toolCalls": 0}, "context": None,
        # #1686 review F6: persists across every batch read for this execution -- a message.id read in
        # an earlier cycle's batch must still dedupe a repeat that shows up in a LATER cycle's batch.
        "seen_message_ids": set(),
    })

    # #1613 review finding 3: `.stdout.log` rolls over to `.stdout.log.1` at 8 MiB and resets to
    # empty (ExecutionStreamLogger.cs) -- a size DECREASE since the offset we last read is the
    # rollover signal. The rename preserves content byte-for-byte, so hand the read position across
    # to the rollover file's own (sticky, independently-tracked) offset rather than re-reading
    # anything already counted -- this also self-heals a SECOND rollover later in the same
    # execution's life, since `.stdout.log.1` gets overwritten each time and the same
    # decrease-detection applies to its own offset too.
    if current_size < state["stdout_offset"]:
        state["rollover_offset"] = max(state["rollover_offset"], state["stdout_offset"])
        state["stdout_offset"] = 0

    if rollover_path is not None:
        try:
            rollover_size = rollover_path.stat().st_size
        except OSError:
            rollover_size = 0
        if rollover_size < state["rollover_offset"]:
            state["rollover_offset"] = 0
        rollover_lines, state["rollover_offset"] = _read_new_lines(rollover_path, state["rollover_offset"])
        if rollover_lines:
            _apply_live_delta(state, extract_live_counts(rollover_lines, state["seen_message_ids"]))

    new_lines, state["stdout_offset"] = _read_new_lines(stdout_path, state["stdout_offset"])
    if new_lines:
        _apply_live_delta(state, extract_live_counts(new_lines, state["seen_message_ids"]))

    result = dict(state["counts"])
    if state["context"] is not None:
        result.update(state["context"])
    result["lastActivityAt"] = _quantized_activity_iso(mtime)
    return result


def attach_live_telemetry(room_list: list, live_cache: dict) -> None:
    """Mutates each Running room in the (already stale-filtered) list in place, adding a `live`
    field. Gated on the pusher's own displayed `state`, not the raw engine state: a room
    fleet_status already downgraded to Stalled (#1513, a CONFIRMED-dead process) never gets a live
    section a dead process cannot honestly back. Called AFTER drop_stale_rooms in main()'s loop, on
    purpose -- `lastActivityAt` is a real file mtime, not a manufactured "now" stamp, so unlike
    `exhaustedUntil` it never needs `newest_timestamp`'s skip set: running it post-filter simply
    means it plays no part in the staleness decision at all, sidestepping the question by
    construction rather than by exemption. `live_cache` is main()'s own persisted dict (#1613 review
    findings 3/4) -- REQUIRED here (unlike `live_telemetry_for_room`'s optional default) because a
    fresh dict every call would defeat the whole point of incremental reading."""
    if not isinstance(room_list, list):
        return
    for room in room_list:
        if not isinstance(room, dict) or room.get("state") != "Running":
            continue
        live = live_telemetry_for_room(room, live_cache)
        if live is not None:
            room["live"] = live


def prune_live_telemetry_cache(live_cache: dict, room_list: list) -> dict:
    """New dict carrying forward only the cache entries for executions still actually Running in
    `room_list` -- a finished or retried execution's counters must not linger forever in a
    long-lived pusher process. Mirrors `terminal_timeline_cache`'s own per-cycle prune in main()."""
    live_keys = set()
    for room in room_list or []:
        if not isinstance(room, dict) or room.get("state") != "Running":
            continue
        execution_id = _running_execution_id(room)
        room_path = room.get("path")
        if execution_id is not None and isinstance(room_path, str) and room_path:
            live_keys.add(f"{room_path}::{execution_id}")
    return {k: v for k, v in live_cache.items() if k in live_keys}


def resolve_room_timeline(room_path: str, is_terminal: bool, cache: dict, fetch_fn) -> list[dict]:
    """#1613 item 4's caching POLICY, pulled out of `derive_snapshot_and_timelines` so it is
    testable without a live `dotnet` subprocess: a non-terminal room always calls `fetch_fn`
    (its timeline keeps growing); a terminal room calls it AT MOST ONCE -- a cache hit returns the
    cached entries without calling `fetch_fn` again, and a fetch that comes back non-empty is
    written into `cache` (mutated in place) so every later call for the same room_path short-
    circuits. A terminal room whose fetch returns [] (error, or a genuinely empty timeline) is
    NOT cached, so it retries next cycle rather than assuming empty is a stable answer."""
    if not is_terminal:
        return fetch_fn(room_path)

    cached = cache.get(room_path)
    if cached is not None:
        return cached

    entries = fetch_fn(room_path)
    if entries:
        cache[room_path] = entries
    return entries


def derive_snapshot_and_timelines(dll: str, roots: list, terminal_timeline_cache: dict | None = None) -> tuple[str, dict]:
    """Returns (the rooms JSON exactly as fleet_status produced it, {room_path: [timeline entries]}
    for every room with one) -- ONE dotnet-mcp process for both, reused across every room_detail
    call in this cycle (module docstring's "THE TIMELINE HALF"): spawning a fresh `dotnet` per room
    would multiply the exact per-cycle subprocess cost the daemon-owns-the-projection design (#1502
    menu #42) exists to kill.

    Non-terminal rooms are re-fetched every cycle (their timeline keeps growing). Terminal rooms
    (#1613 item 4 -- pre-#1613 they were skipped forever, which is why a finished lane showed no
    timeline at all) are fetched through room_detail exactly ONCE per process lifetime and served
    from `terminal_timeline_cache` on every cycle after: a terminal room's flow.jsonl is frozen, so
    re-fetching identical bytes every ~25s cycle would be pure waste, and would also make the
    pushed snapshot's hash churn on nothing (the #1457 change-gate). `terminal_timeline_cache` is
    caller-owned (main()'s own dict, persisted across loop iterations, mutated in place here) --
    there is no on-disk cache, so a pusher restart self-heals by refetching once more.
    """
    terminal_timeline_cache = {} if terminal_timeline_cache is None else terminal_timeline_cache
    # #1458: dll now points at Baton.Cli.dll -- "mcp" is the verb that used to be the whole binary
    # (Baton.Mcp.Host.dll's own Main). Argv shape mirrors ClaudeWorkerAdapter's own
    # EnsureMemoryProposalMcpConfig, the canonical explanation of why the verb comes first.
    proc = subprocess.Popen(
        ["dotnet", dll, "mcp", "--fleet-status-tool", "--room-detail-tool"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8",
    )
    try:
        rpc(proc, 1, "initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "fleet-pusher", "version": "0.2.0"},
        })
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()
        resp = rpc(proc, 2, "tools/call", {
            "name": "fleet_status",
            "arguments": {"roots": roots} if roots else {},
        })
        result = resp.get("result")
        if result is None:
            raise RuntimeError(f"tools/call error: {resp.get('error')}")
        text = result["content"][0]["text"]
        rooms = json.loads(text)  # validate before pushing; raises on garbage
        room_list = rooms if isinstance(rooms, list) else (rooms.get("rooms") or [])

        timelines = {}
        next_id = 3

        def fetch_timeline(room_path: str) -> list[dict]:
            nonlocal next_id
            detail_resp = rpc(proc, next_id, "tools/call", {
                "name": "room_detail",
                "arguments": {"room": room_path},
            })
            next_id += 1
            detail_result = detail_resp.get("result")
            if detail_result is None:
                log(f"room_detail error for {room_path}: {detail_resp.get('error')}")
                return []
            detail = json.loads(detail_result["content"][0]["text"])
            return extract_timeline(detail)

        for room in room_list:
            if not isinstance(room, dict):
                continue
            room_path = room.get("path")
            if not isinstance(room_path, str) or not room_path:
                continue

            try:
                entries = resolve_room_timeline(
                    room_path, is_terminal_room(room_path), terminal_timeline_cache, fetch_timeline)
                if entries:
                    timelines[room_path] = entries
            except Exception as ex:  # noqa: BLE001 — one room's timeline must not sink the cycle
                log(f"room_detail failed for {room_path}: {type(ex).__name__}: {ex}")
    finally:
        proc.terminate()
    return text, timelines


_NEWEST_TIMESTAMP_SKIP_KEYS = frozenset({"exhaustedUntil"})


def newest_timestamp(node, _skip_keys: frozenset = _NEWEST_TIMESTAMP_SKIP_KEYS) -> str:
    """Max ISO-8601-looking string anywhere in the room object -- shape-agnostic on purpose,
    so a fleet_status field rename degrades to 'room has no timestamp' (kept), never a crash.

    `exhaustedUntil` (#1551) is excluded by key, the one deliberate exception to "shape-agnostic":
    it's a vendor-quota park's reset instant, a FUTURE timestamp by construction while parked.
    Folding it into this scan would make an abandoned parked room's "newest timestamp" always
    outrun drop_stale_rooms' cutoff below -- a room nobody is watching would never age out."""
    best = ""
    if isinstance(node, dict):
        for k, v in node.items():
            if k in _skip_keys:
                continue
            best = max(best, newest_timestamp(v, _skip_keys))
    elif isinstance(node, list):
        for v in node:
            best = max(best, newest_timestamp(v, _skip_keys))
    elif isinstance(node, str) and len(node) >= 19 and node[4] == "-" and node[10] == "T":
        best = node
    return best


def drop_stale_rooms(body: str, max_age_days: float) -> tuple[str, int]:
    """Filter rooms whose newest timestamp is older than the cutoff -- zombie RUNNING rooms
    included (a room that died without terminal.json shows Running forever; age is the only
    honest signal). Rooms with no parseable timestamp are KEPT: unreadable is a finding the
    glass should show, not silently drop.

    Returns (filtered body, dropped count). #1505 landmine #43: a dropped room used to be logged
    ONLY to pusher.log -- a room that vanished and a room that never existed looked identical on the
    glass. The count is now the caller's to carry into the pushed snapshot (as
    `stale_hidden_count`), so the page can show "N older than {max_age_days}d hidden" instead of
    silence."""
    data = json.loads(body)
    # fleet_status emits a bare room list; tolerate a {rooms: [...]} wrapper too.
    bare = isinstance(data, list)
    rooms = data if bare else data.get("rooms")
    if not isinstance(rooms, list):
        return body, 0
    cutoff = datetime.now(timezone.utc).timestamp() - max_age_days * 86400
    kept = []
    for room in rooms:
        ts = newest_timestamp(room)
        if ts:
            try:
                when = datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp()
                if when < cutoff:
                    continue
            except ValueError:
                pass
        kept.append(room)
    dropped = len(rooms) - len(kept)
    if dropped:
        log(f"filtered {dropped} stale room(s) older than {max_age_days}d")
    if bare:
        return json.dumps(kept), dropped
    data["rooms"] = kept
    return json.dumps(data), dropped


# ---------------------------------------------------------------------------------------------
# Hot-set capping (#1656) -- measurement and full contract: spec/baton.md §6, "Paging and the
# terminal hot-set cap". Terminal rooms are frozen (terminal.json never changes once written) and
# glass.html itself already only ever RENDERS the newest slice of them, so this moves the same cap
# upstream: only the newest HOT_TERMINAL_CAP terminal rooms ride the plain fleet_status response;
# the rest are still derived and pushed (as `terminal_archive`, a field worker.js's /push handler
# stores under its own KV key, never inside "snapshot") but only served back a page at a time.
# ---------------------------------------------------------------------------------------------

HOT_TERMINAL_CAP = 40  # matches what glass.html already slices the Succeeded bucket to
                        # client-side pre-#1656 (groupLanesHtml(visibleDone.slice(0,40), ...)) --
                        # picked to keep the same "what an operator actually looks at" size, not a
                        # new number.

HOT_NONTERMINAL_WARN = 60  # F3 (2026-09-02 review): the cap above bounds only the terminal bucket
                            # -- Running/Stalled/Indeterminate rooms ride the plain fleet_status
                            # response in FULL, uncapped (spec/baton.md §6). This is a signal, not a
                            # cap: one log line when concurrently-active rooms cross the threshold,
                            # so an incident storm shows up in pusher.log rather than only as a
                            # bigger push the day it happens.


def nonterminal_warn_line(non_terminal_count: int) -> str | None:
    """One log line when `non_terminal_count` exceeds HOT_NONTERMINAL_WARN, else None -- a signal,
    not a cap. Full contract: spec/baton.md §6, "Paging and the terminal hot-set cap"."""
    if non_terminal_count > HOT_NONTERMINAL_WARN:
        return (f"non-terminal room count {non_terminal_count} exceeds HOT_NONTERMINAL_WARN "
                f"({HOT_NONTERMINAL_WARN}) -- unbounded, no cap")
    return None

_TERMINAL_STATES = frozenset({"Succeeded", "Failed"})  # the two buckets glass.html's own Terminal
                                                        # section covers (render()'s `termContent`)
                                                        # -- Running/Stalled/Indeterminate/unreadable
                                                        # rooms are never terminal by this measure.


def split_hot_and_archive(room_list: list) -> tuple[list, list, int]:
    """Splits `room_list` (fleet_status's own per-room objects) into `(hot_rooms, terminal_archive,
    terminal_total)`. `hot_rooms` (non-terminal rooms plus the newest HOT_TERMINAL_CAP terminal
    ones) is what rides the plain (no `page`) fleet_status response; `terminal_archive` is the FULL
    terminal population (not just the tail beyond the cap -- a `page=0` fetch then returns the same
    newest rooms `hot_rooms` already carried); `terminal_total` is the total terminal count. Full
    contract, including the "newest" measure and why a malformed room degrades to non-terminal
    rather than being dropped: spec/baton.md §6, "Paging and the terminal hot-set cap"."""
    non_terminal = [r for r in room_list if not (isinstance(r, dict) and r.get("state") in _TERMINAL_STATES)]
    terminal = [r for r in room_list if isinstance(r, dict) and r.get("state") in _TERMINAL_STATES]
    terminal.sort(key=newest_timestamp, reverse=True)
    hot_rooms = non_terminal + terminal[:HOT_TERMINAL_CAP]
    return hot_rooms, terminal, len(terminal)


def _git(cwd: str, *args: str) -> str:
    try:
        out = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True, timeout=15, check=False,
        )
        return out.stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def gather_underhood(cfg: dict) -> list:
    """Worktree telemetry for active lanes: branch, diff shape, newest commit.

    CONTENT-FREE BY DESIGN: branch names, file counts, and +/- totals only -- no diff hunks, so
    nothing here can leak a secret VALUE. Fleet-level only, never attached to a specific room row --
    #1505 removed the `underhood_logs` name-matching heuristic that used to do that (`name.endswith(
    e["name"].lstrip("w")) or e["name"].lstrip("w") in name`, a substring match on a w-stripped
    directory name): two similarly-named lanes could silently attach the WRONG lane's log tail to a
    worktree entry. Wrong-and-confident is worse than absent (spec/baton.md's epic #1502 ratified
    decisions) -- this stays a fleet-level section with no per-room attribution until a real
    room<->worktree key exists to replace the guess, not before."""
    import glob as globmod

    entries = []
    for pattern in cfg.get("underhood_dirs", []):
        for d in sorted(globmod.glob(pattern)):
            if not (Path(d) / ".git").exists():
                continue
            branch = _git(d, "rev-parse", "--abbrev-ref", "HEAD")
            shortstat = _git(d, "diff", "--shortstat", "HEAD")
            dirty = len([ln for ln in _git(d, "status", "--porcelain").splitlines() if ln])
            last = _git(d, "log", "-1", "--format=%s\x1f%cI")
            subject, _, committed = last.partition("\x1f")
            entries.append({
                "name": Path(d).name,
                "branch": branch,
                "uncommitted": shortstat or ("clean" if dirty == 0 else f"{dirty} file(s) touched"),
                "last_commit": subject[:120],
                "last_commit_at": committed,
            })
    return entries


def post_json(url: str, body: str) -> None:
    req = urllib.request.Request(
        url, data=body.encode("utf-8"), method="POST",
        # Cloudflare's edge 403s the default Python-urllib user-agent.
        headers={"content-type": "application/json", "user-agent": "fleet-pusher/0.2"},
    )
    with urllib.request.urlopen(req, timeout=20) as resp:
        if resp.status != 200:
            raise RuntimeError(f"push status {resp.status}")


SNAPSHOT_HASH_KEY = "__snapshot_hash__"


def build_wrapped(room_list, underhood, timelines, stale_hidden_count,
                   terminal_total: int = 0, terminal_archive: list | None = None,
                   conductor: dict | None = None) -> dict:
    """The exact snapshot body main() pushes. One home so the leak selftest exercises the real push
    path's construction, not a hand-rebuilt copy that could drift from it (PR #1508 review).

    `terminal_total`/`terminal_archive` (#1656) default to 0/None so every pre-existing call site
    (this module's own hash/selftest fixtures) keeps working unchanged -- callers that care about
    the hot-set split pass `room_list` as already-capped `hot_rooms` (see `split_hot_and_archive`)
    and the FULL terminal population separately here. worker.js's /push handler strips
    `terminal_archive` back out into its own KV key before it ever reaches "snapshot"."""
    wrapped = {"rooms": room_list,
               "underhood": underhood,
               "timelines": timelines,
               "stale_hidden_count": stale_hidden_count,
               "terminal_total": terminal_total,
               "terminal_archive": terminal_archive or []}
    if conductor is not None:
        wrapped["conductor"] = conductor
    return wrapped


def snapshot_hash(wrapped: dict) -> str:
    """Stable hash of the wrapped {rooms, underhood} body -- sort_keys so the hash does not depend
    on dict insertion order upstream, independent of the (unsorted) exact string actually POSTed."""
    return sha256_hex(json.dumps(wrapped, sort_keys=True).encode("utf-8"))


def should_push_snapshot(state: dict, current_hash: str) -> bool:
    """True unless `current_hash` matches the last SUCCESSFUL push's hash persisted under
    SNAPSHOT_HASH_KEY. A missing/unreadable persisted value (state.get returns None) always
    pushes -- fail toward one extra write, never toward silence."""
    return state.get(SNAPSHOT_HASH_KEY) != current_hash


LAST_PUSH_TS_KEY = "__last_push_ts__"
DEFAULT_MIN_PUSH_INTERVAL_S = 90


def should_coalesce_push(state: dict, now_ts: float, min_interval_s: float = DEFAULT_MIN_PUSH_INTERVAL_S) -> bool:
    """True if less than min_interval_s has elapsed since the last actual snapshot push."""
    last = state.get(LAST_PUSH_TS_KEY)
    if not isinstance(last, (int, float)):
        return False
    return (now_ts - last) < min_interval_s


def push_snapshot_and_record(post, body: str, state: dict, state_path, current_hash: str, now_ts: float | None = None) -> None:
    """POST first, record the hash and push timestamp ONLY afterwards. This ordering is the
    change-gate's single most safety-critical property (a hash persisted for a FAILED push would
    gate every retry and go silent until the next content change), so it lives in one testable
    function instead of inline in main()'s loop -- the selftest proves a raising `post` leaves the
    state file untouched."""
    post(body)
    state[SNAPSHOT_HASH_KEY] = current_hash
    if now_ts is None:
        now_ts = time.time()
    state[LAST_PUSH_TS_KEY] = now_ts
    save_push_state(state_path, state)


def should_log_skip(streak: int, log_every: int) -> bool:
    """First skip in a streak logs immediately (so 'now skipping' is visible right away); after
    that, only every `log_every`th cycle -- keeps pusher.log from being mostly skip lines across a
    quiet fleet while still proving the loop is alive, given the 1MB truncation behavior."""
    return streak == 1 or streak % log_every == 0


def derive_deliver_url(cfg: dict) -> str | None:
    if cfg.get("deliver_url"):
        return cfg["deliver_url"]
    push_url = cfg.get("push_url", "")
    if "/push/" in push_url:
        return push_url.replace("/push/", "/deliver/", 1)
    return None


HEARTBEAT_STATE_KEY = "__last_heartbeat_ts__"
HEARTBEAT_INTERVAL_SECONDS = 3600  # hourly: 24 writes/day + the change-gated snapshot writes (worst
                                    # case one per interval_seconds) against the 1,000/day KV
                                    # free-tier cap the change-gate (#1457) protects -- see the
                                    # module docstring's "THE HEARTBEAT HALF" section.


def derive_heartbeat_url(cfg: dict) -> str | None:
    if cfg.get("heartbeat_url"):
        return cfg["heartbeat_url"]
    push_url = cfg.get("push_url", "")
    if "/push/" in push_url:
        return push_url.replace("/push/", "/heartbeat/", 1)
    return None


def should_send_heartbeat(state: dict, now_ts: float, interval: float = HEARTBEAT_INTERVAL_SECONDS) -> bool:
    """True once at least `interval` seconds have elapsed since the last recorded heartbeat.
    A missing/unreadable persisted timestamp always sends -- same fail-toward-one-extra-write
    posture as should_push_snapshot."""
    last = state.get(HEARTBEAT_STATE_KEY)
    if not isinstance(last, (int, float)):
        return True
    return (now_ts - last) >= interval


def send_heartbeat_and_record(post, state: dict, state_path, now_ts: float, extra_state: dict | None = None) -> None:
    """POST first, record only afterwards -- same ordering discipline as push_snapshot_and_record
    (a raising `post` must leave `state` untouched, so a failed heartbeat retries next cycle
    instead of going silent). `extra_state` (#1613 item 2) lets one physical POST also stamp a
    second, independently-gated cadence's own state key (see `should_send_derived_ping` below)
    without a second network round trip -- merged in only after `post` succeeds, same
    all-or-nothing ordering as HEARTBEAT_STATE_KEY itself."""
    post()
    state[HEARTBEAT_STATE_KEY] = now_ts
    if extra_state:
        state.update(extra_state)
    save_push_state(state_path, state)


# ---------------------------------------------------------------------------------------------
# derived_at (#1613 item 2): what it is and why the glass banner keys on it instead of pushed_at
# is spec/baton.md §6's `derived_at` schema entry, not restated here. `pending_push_age_s`
# (this review's finding 2) rides the SAME `/heartbeat` ping body: derived_at alone cannot tell "the
# fleet is quiet" apart from "derivation keeps succeeding but every PUSH keeps failing" (a 413 from
# the 1 MB cap, a 5xx, a network blip) -- see `pending_push_age_s`'s own docstring below.
#
# Budget: derived_at must reach the server far more often than heartbeat_at's own hourly cadence to
# be a useful "stuck" signal, but a naive fixed-interval ping alongside the change-gated snapshot
# writes would blow the 1,000-writes/day KV free-tier cap this module's docstring already budgets
# to the edge (~984/day between the snapshot and heartbeat alone). The two writes are made
# mutually exclusive per cycle instead of additive: an actual snapshot PUSH already carries a fresh
# derived_at in its own body (excluded from `snapshot_hash` so it never forces a push on its own --
# see `main()`'s push branch), so `should_send_derived_ping` below only fires the dedicated ping
# when NEITHER a push nor a prior ping has landed one recently. A day spent constantly pushing
# (worst case ~960 writes) never also pays the ping's cost (it wouldn't fire); a quiet day (near
# zero snapshot writes) pays the ping's cost instead (worst case ~288/day at this interval) --
# never both at once, so the combined worst case stays close to the snapshot-alone worst case.
# ---------------------------------------------------------------------------------------------

DERIVED_PING_STATE_KEY = "__last_derived_ping_ts__"
DERIVED_PING_INTERVAL_SECONDS = 300  # 5 minutes -- well under the glass's RUNNING_SUSPICION_MS
                                      # (10 minutes) "stuck" threshold, so a genuinely wedged
                                      # derivation is caught on roughly the same timescale the
                                      # banner already used pre-#1613, not degraded to it.


def should_send_derived_ping(state: dict, now_ts: float, interval: float = DERIVED_PING_INTERVAL_SECONDS) -> bool:
    """True once `interval` seconds have elapsed since derived_at last reached the server by
    EITHER channel: an actual snapshot push (LAST_PUSH_TS_KEY) or a prior dedicated ping
    (DERIVED_PING_STATE_KEY) -- whichever is more recent. A missing/unreadable timestamp on both
    counts as "never landed" -- fail toward sending one extra ping, never toward silence, same
    posture as should_send_heartbeat/should_push_snapshot."""
    landed_via_push = state.get(LAST_PUSH_TS_KEY)
    landed_via_ping = state.get(DERIVED_PING_STATE_KEY)
    candidates = [t for t in (landed_via_push, landed_via_ping) if isinstance(t, (int, float))]
    if not candidates:
        return True
    return (now_ts - max(candidates)) >= interval


def pending_push_age_s(state: dict, current_hash: str, now_ts: float) -> float | None:
    """Seconds since the last SUCCESSFUL push, but ONLY when there is content actually waiting to go
    out (`should_push_snapshot` says the persisted hash no longer matches `current_hash`) -- this
    review's finding 2. A quiet, healthy fleet reports `None` here even though its own last push may
    genuinely have been hours ago; that is the whole point -- it is what lets glass tell "nothing to
    push" apart from "wants to push and can't", which `derived_at` alone cannot do (derivation
    succeeds every cycle regardless of whether the following POST does). A missing/unreadable
    LAST_PUSH_TS_KEY while content IS waiting also reports `None`: there is no successful-push
    baseline yet to measure age from (this process's first cycle), and reporting an arbitrary number
    here would be a fabricated figure, not an absent one -- same never-fabricate convention as every
    other optional field in this module. Because `push_snapshot_and_record` only updates
    LAST_PUSH_TS_KEY AFTER a successful POST, a run of failing pushes leaves it frozen, so this value
    grows cycle over cycle for as long as the failures continue -- exactly the "growing pending age"
    signal the heartbeat ping is meant to carry."""
    if not should_push_snapshot(state, current_hash):
        return None
    last = state.get(LAST_PUSH_TS_KEY)
    if not isinstance(last, (int, float)):
        return None
    return now_ts - last


# ---------------------------------------------------------------------------------------------
# Deliverables: terminal-room scan, secret gate, dedupe (#1413 half 2)
# ---------------------------------------------------------------------------------------------

def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_secret_patterns(path: Path) -> list[re.Pattern] | None:
    """Compiled denylist from a plain-text file (one Python regex per line; '#' starts a comment,
    blank lines ignored). Returns None -- the fail-closed sentinel -- if the file is missing or
    cannot be read/parsed.

    None is NOT the same as an empty list: an empty, present, readable file (a deliberate "nothing
    to withhold on" choice) returns []. Only an absent or broken file returns None, and every caller
    of this function must treat None as "withhold everything", per the owner's fail-closed ruling.
    """
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError:
        return None
    patterns = []
    try:
        for line in raw.splitlines():
            stripped = line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            patterns.append(re.compile(stripped))
    except re.error:
        return None
    return patterns


def secret_hit_index(text: str, patterns: list[re.Pattern]) -> int | None:
    """Index of the first pattern (in file order) that matches anywhere in text, else None."""
    for i, pattern in enumerate(patterns):
        if pattern.search(text):
            return i
    return None


def extract_title(text: str, fallback: str) -> str:
    """The file's first markdown heading (`# Title`), else the fallback (its filename)."""
    for line in text.splitlines():
        m = re.match(r"^#\s+(.+?)\s*$", line)
        if m:
            return m.group(1)
    return fallback


STATE_FORMAT_VERSION_KEY = "__format_version__"
CURRENT_STATE_FORMAT_VERSION = 2
DEFAULT_DELIVER_BATCH_CAP = 10


def load_push_state(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_push_state(path: Path, state: dict) -> None:
    if STATE_FORMAT_VERSION_KEY not in state:
        state[STATE_FORMAT_VERSION_KEY] = CURRENT_STATE_FORMAT_VERSION
    path.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")


def migrate_push_state(
    state: dict,
    terminal_rooms: list[tuple[str, str, Path]],
    state_path: Path | None = None,
) -> bool:
    """Migrate legacy push-state keys from f"{room_name}::{artifact}" to f"{room_path}::{artifact}".

    For each terminal room:
    - If a legacy key is present and the corresponding path key is absent, adopt the legacy value
      under the path key and drop the legacy key, then persist.
    - A legacy key whose name matches two current rooms (the exact collision #1617 is about)
      must NOT be adopted for either — log it and let both re-push once.
    - Also records the state-file format version (__format_version__ = 2).
    """
    name_to_rooms: dict[str, list[tuple[str, str, Path]]] = {}
    for room_path, room_name, room_dir in terminal_rooms:
        name_to_rooms.setdefault(room_name, []).append((room_path, room_name, room_dir))

    changed = False
    for k in list(state.keys()):
        if k.startswith("__") or "::" not in k:
            continue
        prefix, artifact = k.split("::", 1)
        if prefix in name_to_rooms:
            rooms_for_name = name_to_rooms[prefix]
            if len(rooms_for_name) == 1:
                room_path, _, _ = rooms_for_name[0]
                if prefix != room_path:
                    path_key = f"{room_path}::{artifact}"
                    if path_key not in state:
                        state[path_key] = state[k]
                    del state[k]
                    changed = True
            else:
                paths_str = ", ".join(r[0] for r in rooms_for_name)
                log(f"migration: legacy key '{k}' matches {len(rooms_for_name)} rooms ({paths_str}); not adopting, will re-push")
                del state[k]
                changed = True

    if state.get(STATE_FORMAT_VERSION_KEY) != CURRENT_STATE_FORMAT_VERSION:
        state[STATE_FORMAT_VERSION_KEY] = CURRENT_STATE_FORMAT_VERSION
        changed = True

    if changed and state_path is not None:
        save_push_state(state_path, state)

    return changed


def find_terminal_rooms(rooms_root: Path) -> list[tuple[str, str, Path]]:
    """(room_path, room_name, room_dir) for every room directory that carries a terminal.json.

    A room with no terminal.json is still running (or was never dispatched) -- outside this
    function's job, which is only to find TERMINAL rooms; the fleet snapshot half already covers
    in-flight state.
    """
    if not rooms_root.is_dir():
        return []
    found = []
    for child in sorted(rooms_root.iterdir()):
        if child.is_dir() and (child / "terminal.json").is_file():
            found.append((str(child), child.name, child))
    return found


def load_terminal(room_dir: Path) -> dict | None:
    try:
        return json.loads((room_dir / "terminal.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def verdict_summary(terminal: dict) -> dict:
    return {
        "state": terminal.get("state"),
        "error": terminal.get("error"),
        "try": terminal.get("try"),
    }


def declared_outputs(terminal: dict) -> list[Path]:
    """terminal.json's own "outputs" list -- the room's declared `--output` artifact(s), and the
    ONLY thing this pusher ever reads out of a room's artifacts directory. Never prompt.txt, never
    .stdout.log: those simply never appear in this list, because it is not a directory walk."""
    return [Path(p) for p in terminal.get("outputs", []) if isinstance(p, str) and p]


def _apply_secret_gate(content_bytes: bytes, local_path: str, patterns: list[re.Pattern] | None):
    """Returns (content_to_upload, withheld, stub_reason, pattern_index) for one artifact's bytes.

    Fails CLOSED: `patterns is None` (the load_secret_patterns sentinel) withholds unconditionally,
    stub reason names the missing file rather than a matched pattern. NEVER logs or uploads a
    pattern's own text -- only its index -- so the denylist itself never leaks through the log or
    the mailbox.
    """
    if patterns is None:
        return (
            f"withheld — secret-pattern file missing, read locally: {local_path}",
            True, "patterns file missing", None,
        )
    text = content_bytes.decode("utf-8", errors="replace")
    hit = secret_hit_index(text, patterns)
    if hit is not None:
        return (
            f"withheld — secret-pattern match, read locally: {local_path}",
            True, f"matched pattern #{hit}", hit,
        )
    return text, False, None, None


def build_item(room_path: str, room_dir: Path, artifact_path: Path, verdict: dict,
                patterns: list[re.Pattern] | None, room_name: str | None = None) -> dict:
    """One deliverable for a declared output artifact. `artifact_path` is absolute (terminal.json
    stores absolute paths); the item's "artifact" field is that path relative to the room dir, so
    dedupe keys and inbox rows never carry the operator's home directory.

    Keyed by room PATH (#1617, matching the snapshot half's timeline join; room_name is kept for
    display only)."""
    if room_name is None:
        room_name = room_dir.name if room_dir is not None else Path(room_path).name
    try:
        raw = artifact_path.read_bytes()
    except OSError as ex:
        raw = f"(unreadable: {ex})".encode("utf-8")
    content_hash = sha256_hex(raw)
    try:
        rel = artifact_path.relative_to(room_dir).as_posix()
    except ValueError:
        rel = artifact_path.name
    content, withheld, stub_reason, pattern_index = _apply_secret_gate(raw, str(artifact_path), patterns)
    title = extract_title(raw.decode("utf-8", errors="replace"), artifact_path.name) if not withheld else artifact_path.name
    item = {
        "id": f"{room_path}::{rel}::{content_hash[:16]}",
        "room": room_path,
        "room_name": room_name,
        "artifact": rel,
        "title": title,
        "content_hash": content_hash,
        "withheld": withheld,
        "verdict": verdict,
        "content": content,
    }
    try:
        st = artifact_path.stat()
        item["created_at"] = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
    except (OSError, ValueError):
        pass
    if stub_reason:
        item["stub_reason"] = stub_reason
    if pattern_index is not None:
        log(f"secret-gate: {room_name}/{rel} matched pattern #{pattern_index}: withheld")
    return item


def build_verdict_only_item(room_path: str, verdict: dict, room_dir: Path | None = None,
                            room_name: str | None = None) -> dict:
    """A room with zero declared outputs (typically Failed) still gets one inbox entry, so a
    failure with nothing to show is still visible rather than silently absent.

    Keyed by room PATH (#1617, matching the snapshot half's timeline join; room_name is kept for
    display only)."""
    if room_name is None:
        room_name = room_dir.name if room_dir is not None else Path(room_path).name
    text = json.dumps(verdict, indent=2, sort_keys=True)
    content_hash = sha256_hex(text.encode("utf-8"))
    item = {
        "id": f"{room_path}::__verdict__::{content_hash[:16]}",
        "room": room_path,
        "room_name": room_name,
        "artifact": None,
        "title": f"{room_name} — {verdict.get('state') or 'unknown'}",
        "content_hash": content_hash,
        "withheld": False,
        "verdict": verdict,
        "content": text,
    }
    if room_dir is not None:
        try:
            st = (Path(room_dir) / "terminal.json").stat()
            item["created_at"] = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
        except (OSError, ValueError):
            pass
    return item


def gather_conductor_deliverables(
    rooms_root: Path,
    state: dict,
    patterns: list[re.Pattern] | None,
    limit: int | None = DEFAULT_DELIVER_BATCH_CAP,
) -> list[dict]:
    """Scans manifest.jsonl from the standing conductor room (or any conductor room under rooms_root)
    and gathers deliverable items with kind='conductor' and id derived from source_path (#1669)."""
    items = []
    conductor_dirs = []
    conductor_default = rooms_root / "conductor"
    if conductor_default.is_dir():
        conductor_dirs.append(conductor_default)

    if rooms_root.is_dir():
        try:
            for child in rooms_root.iterdir():
                if child.is_dir() and child != conductor_default:
                    if (child / "artifacts" / "conductor" / "manifest.jsonl").is_file():
                        conductor_dirs.append(child)
        except OSError:
            pass

    for conductor_dir in conductor_dirs:
        manifest_path = conductor_dir / "artifacts" / "conductor" / "manifest.jsonl"
        if not manifest_path.is_file():
            continue

        conductor_room_path = str(conductor_dir)
        conductor_artifacts_dir = conductor_dir / "artifacts" / "conductor"

        try:
            lines = manifest_path.read_text(encoding="utf-8-sig").splitlines()
        except Exception as ex:  # noqa: BLE001
            log(f"conductor manifest read error for {conductor_dir}: {type(ex).__name__}: {ex}")
            continue

        for line_num, line in enumerate(lines, start=1):
            if limit is not None and len(items) >= limit:
                break
            if not line.strip():
                continue
            try:
                entry = json.loads(line)
            except json.JSONDecodeError as ex:
                log(f"conductor manifest JSONDecodeError in {conductor_dir} line {line_num}: {ex}")
                continue
            except Exception:
                continue
            if not isinstance(entry, dict):
                continue

            source_path = entry.get("source_path")
            if not isinstance(source_path, str) or not source_path:
                continue

            # F1 (2026-09-02 review): artifact_file is read from the manifest line, never
            # re-derived from the basename — DeliverCommand.cs keys the on-disk filename off a hash
            # of source_path precisely so two sources sharing a basename land on two distinct files;
            # re-deriving here would silently collapse them back onto one.
            artifact_file_name = entry.get("artifact_file")
            if not isinstance(artifact_file_name, str) or not artifact_file_name:
                continue

            basename = Path(source_path).name
            artifact_file = conductor_artifacts_dir / artifact_file_name
            if not artifact_file.is_file():
                continue

            try:
                raw = artifact_file.read_bytes()
            except Exception:
                continue

            content_hash = sha256_hex(raw)
            key = f"{conductor_room_path}::artifacts/conductor/{artifact_file_name}"
            if state.get(key) == content_hash:
                continue

            content, withheld, stub_reason, pattern_index = _apply_secret_gate(
                raw, str(artifact_file), patterns)

            title = entry.get("title") or basename
            delivered_at = entry.get("delivered_at")
            if not delivered_at:
                try:
                    st = artifact_file.stat()
                    delivered_at = datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat()
                except (OSError, ValueError):
                    delivered_at = datetime.now(timezone.utc).isoformat()

            item = {
                "id": f"{conductor_room_path}::conductor::{source_path}",
                "kind": "conductor",
                "room": conductor_room_path,
                "room_name": "conductor",
                "artifact": f"artifacts/conductor/{artifact_file_name}",
                "source_path": source_path,
                "title": title,
                "content_hash": content_hash,
                "withheld": withheld,
                "verdict": {"state": "Succeeded"},
                "content": content,
                "created_at": delivered_at,
            }
            if stub_reason:
                item["stub_reason"] = stub_reason
            items.append(item)

    return items


def gather_deliverables(
    rooms_root: Path,
    state: dict,
    patterns: list[re.Pattern] | None,
    state_path: Path | None = None,
    limit: int | None = DEFAULT_DELIVER_BATCH_CAP,
) -> list[dict]:
    """Every not-yet-pushed deliverable across all terminal rooms and conductor rooms under rooms_root, capped at `limit`.

    Migrates legacy room_name-keyed state entries to room_path keys before lookup (#1617 / PR #1632).
    "not yet pushed" is decided per (room_path, artifact) against `state[key] == content_hash` -- an
    unchanged hash is skipped. Deliberately NOT memorized into `state` here (the caller does that,
    only after a successful network push): when `patterns is None`, every item this run is withheld
    for that reason alone, and it must be re-offered on the NEXT run too, in case an operator has
    fixed the patterns file by then -- see `load_secret_patterns`.
    """
    if patterns is None:
        log("secret-gate: secret_patterns_file missing/unreadable — WITHHOLDING EVERYTHING this run (fail closed)")

    items = []
    conductor_items = gather_conductor_deliverables(rooms_root, state, patterns, limit=limit)
    items.extend(conductor_items)

    terminal_rooms = find_terminal_rooms(rooms_root)
    migrate_push_state(state, terminal_rooms, state_path=state_path)

    for room_path, room_name, room_dir in terminal_rooms:
        if limit is not None and len(items) >= limit:
            break
        terminal = load_terminal(room_dir)
        if terminal is None:
            continue
        verdict = verdict_summary(terminal)
        outputs = declared_outputs(terminal)
        if not outputs:
            item = build_verdict_only_item(room_path, verdict, room_dir, room_name=room_name)
            key = f"{room_path}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                items.append(item)
            continue
        for artifact_path in outputs:
            if limit is not None and len(items) >= limit:
                break
            item = build_item(room_path, room_dir, artifact_path, verdict, patterns, room_name=room_name)
            key = f"{room_path}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                items.append(item)
    return items


def mark_pushed(state: dict, items: list[dict]) -> dict:
    """New state dict with each item's (room_path, artifact) -> content_hash recorded. Pure, so callers
    control exactly when a successful push is allowed to count as "seen"."""
    updated = dict(state)
    for item in items:
        updated[f"{item['room']}::{item['artifact']}"] = item["content_hash"]
    return updated


# ---------------------------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------------------------

def main() -> None:
    cfg = json.loads((HERE / "pusher.config.json").read_text(encoding="utf-8"))
    once = "--once" in sys.argv
    interval = cfg.get("interval_seconds", 25)
    min_push_interval_s = cfg.get("min_push_interval_s", DEFAULT_MIN_PUSH_INTERVAL_S)
    lock_path = Path(cfg["lock_file"]).expanduser() if cfg.get("lock_file") else DEFAULT_LOCK_FILE
    rooms_root = Path(cfg["rooms_root"]).expanduser() if cfg.get("rooms_root") else DEFAULT_ROOMS_ROOT
    patterns_path = Path(cfg["secret_patterns_file"]).expanduser() if cfg.get("secret_patterns_file") else DEFAULT_SECRET_PATTERNS_FILE
    state_path = Path(cfg["push_state_file"]).expanduser() if cfg.get("push_state_file") else DEFAULT_PUSH_STATE_FILE
    deliver_url = derive_deliver_url(cfg)
    heartbeat_url = derive_heartbeat_url(cfg)
    skip_log_every = max(1, round(600 / interval)) if interval > 0 else 1
    skip_streak = 0
    # #1613 item 4: terminal-room timelines are fetched through room_detail exactly ONCE per
    # process lifetime and served from here on every later cycle -- see
    # derive_snapshot_and_timelines's own doc. In-memory only: a restart self-heals by refetching.
    terminal_timeline_cache: dict = {}
    # #1613 review findings 3/4: per-execution (byte_offset, running_counts) for Running rooms'
    # live telemetry -- see live_telemetry_for_room's own doc. In-memory only, same self-heals-on-
    # restart posture as terminal_timeline_cache above.
    live_telemetry_cache: dict = {}
    # #1613 item 2: the wall-clock instant this process's OWN most recent `derive_snapshot_and_
    # timelines` call last completed successfully -- None until the first cycle succeeds. Carried
    # into the heartbeat/derived-ping section below regardless of whether THIS cycle's content
    # changed enough to push.
    last_derived_at: str | None = None
    # #1613 review finding 2: seconds since the last SUCCESSFUL push while content is still waiting
    # to go out -- None whenever nothing is pending (see pending_push_age_s). Carried forward
    # unchanged on a cycle whose derivation itself fails, same as last_derived_at above.
    pending_push_age: float | None = None

    acquire_lock(lock_path)
    atexit.register(release_lock, lock_path)

    try:
        while True:
            try:
                body, timelines = derive_snapshot_and_timelines(
                    cfg["dll"], cfg.get("roots", []), terminal_timeline_cache)
                last_derived_at = datetime.now(timezone.utc).isoformat()
                body, stale_hidden_count = drop_stale_rooms(body, cfg.get("max_age_days", 3))
                rooms = json.loads(body)
                raw_room_list = rooms if isinstance(rooms, list) else rooms.get("rooms")
                conductor_info = None
                filtered_room_list = []
                for r in (raw_room_list or []):
                    if isinstance(r, dict) and (r.get("role") == "conductor" or r.get("name") == "conductor"):
                        c_path = r.get("path") or str(rooms_root / "conductor")
                        conductor_info = {
                            "path": c_path,
                            "artifacts_path": str(Path(c_path) / "artifacts" / "conductor"),
                        }
                    else:
                        filtered_room_list.append(r)
                room_list = filtered_room_list
                if conductor_info is None and (rooms_root / "conductor").is_dir():
                    c_path = str(rooms_root / "conductor")
                    conductor_info = {
                        "path": c_path,
                        "artifacts_path": str(Path(c_path) / "artifacts" / "conductor"),
                    }
                # #1613 item 1: live telemetry for Running rooms, computed AFTER stale-filtering
                # (never touches drop_stale_rooms' own newest_timestamp scan above) so it plays no
                # part in the staleness decision at all.
                live_telemetry_cache = prune_live_telemetry_cache(live_telemetry_cache, room_list)
                attach_live_telemetry(room_list, live_telemetry_cache)
                # Timelines were fetched pre-stale-filter, keyed by path; only carry forward the ones
                # for rooms that survived drop_stale_rooms above, so a hidden room's timeline is hidden
                # with it rather than riding along as orphaned payload.
                surviving_paths = {r.get("path") for r in (room_list or []) if isinstance(r, dict)}
                terminal_timeline_cache = {
                    p: t for p, t in terminal_timeline_cache.items() if p in surviving_paths}
                # #1656: split BEFORE building the wrapped body, and filter `timelines` to
                # `hot_paths` (not the wider `surviving_paths`) -- spec/baton.md §6, "Paging and the
                # terminal hot-set cap".
                hot_rooms, terminal_archive, terminal_total = split_hot_and_archive(room_list or [])
                non_terminal_count = len(room_list or []) - terminal_total
                warn_line = nonterminal_warn_line(non_terminal_count)
                if warn_line:
                    log(warn_line)
                hot_paths = {r.get("path") for r in hot_rooms if isinstance(r, dict)}
                wrapped = build_wrapped(
                    hot_rooms,
                    gather_underhood(cfg),
                    {p: t for p, t in timelines.items() if p in hot_paths},
                    stale_hidden_count,
                    terminal_total=terminal_total,
                    terminal_archive=terminal_archive,
                    conductor=conductor_info)
                current_hash = snapshot_hash(wrapped)
                snap_state = load_push_state(state_path)
                if should_push_snapshot(snap_state, current_hash):
                    now_ts = time.time()
                    if should_coalesce_push(snap_state, now_ts, min_push_interval_s):
                        last_ts = snap_state[LAST_PUSH_TS_KEY]
                        elapsed = int(now_ts - last_ts)
                        log(f"coalesced ({elapsed}s since last push)")
                    else:
                        # derived_at rides the ACTUAL posted body but is excluded from current_hash
                        # (computed above from `wrapped` alone) -- it must never make the change-gate
                        # think an otherwise-unchanged snapshot changed.
                        post_body = json.dumps({**wrapped, "derived_at": last_derived_at})
                        try:
                            push_snapshot_and_record(
                                lambda b: post_json(cfg["push_url"], b),
                                post_body, snap_state, state_path, current_hash, now_ts=now_ts)
                        except Exception as ex:  # noqa: BLE001 — a failing push must not skip the
                            # pending-push-age computation below (finding 2's whole point), and the
                            # loop must survive regardless -- caught here, not the outer except, so
                            # execution falls through to that computation either way.
                            log(f"ERROR (push) {type(ex).__name__}: {ex}")
                        else:
                            if skip_streak:
                                log(f"skipped {skip_streak} unchanged cycle(s) since last push")
                                skip_streak = 0
                            log(f"pushed {len(body)} bytes")
                else:
                    skip_streak += 1
                    if should_log_skip(skip_streak, skip_log_every):
                        log(f"unchanged, skipped ({skip_streak} in a row)")
                # #1613 review finding 2: recomputed from `snap_state` AFTER the push attempt above,
                # whichever way it went -- a successful push just updated LAST_PUSH_TS_KEY in place
                # (push_snapshot_and_record mutates snap_state), so should_push_snapshot now agrees
                # the hash matches and this comes back None; a coalesced or failed push leaves
                # snap_state's hash stale, so this reports how long content has been waiting.
                pending_push_age = pending_push_age_s(snap_state, current_hash, time.time())
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (snapshot) {type(ex).__name__}: {ex}")

            # Own try/except, runs AFTER the snapshot has already been sent above -- a slow or failing
            # heartbeat POST must never block or delay the snapshot path (#1486). Also carries the
            # derived-freshness ping (#1613 item 2) on the same lightweight endpoint whenever a push
            # hasn't already delivered a fresh derived_at recently -- see should_send_derived_ping --
            # and, since this review's finding 2, the current pending_push_age (omitted when None,
            # i.e. nothing is waiting to go out) so glass can alarm on a failing push independent of
            # derived_at, which stays fresh even while every push fails.
            try:
                if heartbeat_url is None:
                    pass  # no heartbeat_url configured and none derivable from push_url — skip quietly
                else:
                    hb_state = load_push_state(state_path)
                    now_ts = time.time()
                    heartbeat_due = should_send_heartbeat(hb_state, now_ts)
                    derived_ping_due = should_send_derived_ping(hb_state, now_ts)
                    if heartbeat_due or derived_ping_due:
                        payload_dict = {"derived_at": last_derived_at}
                        if pending_push_age is not None:
                            payload_dict["pending_push_age_s"] = pending_push_age
                        payload = json.dumps(payload_dict)
                        extra_state = {DERIVED_PING_STATE_KEY: now_ts} if derived_ping_due else None
                        send_heartbeat_and_record(
                            lambda: post_json(heartbeat_url, payload),
                            hb_state, state_path, now_ts, extra_state=extra_state)
                        log("heartbeat sent" if heartbeat_due else "derived-freshness ping sent")
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (heartbeat) {type(ex).__name__}: {ex}")

            try:
                if deliver_url is None:
                    log("deliver: no deliver_url (set one, or a push_url containing /push/) — skipped")
                else:
                    state = load_push_state(state_path)
                    patterns = load_secret_patterns(patterns_path)
                    items = gather_deliverables(
                        rooms_root, state, patterns,
                        state_path=state_path,
                        limit=cfg.get("deliver_batch_cap", DEFAULT_DELIVER_BATCH_CAP),
                    )
                    if items:
                        post_json(deliver_url, json.dumps({"items": items}))
                        if patterns is not None:
                            save_push_state(state_path, mark_pushed(state, items))
                        log(f"delivered {len(items)} item(s) "
                            f"({sum(1 for i in items if i['withheld'])} withheld)")
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (deliver) {type(ex).__name__}: {ex}")

            if once:
                break
            time.sleep(interval)
    finally:
        release_lock(lock_path)


# ---------------------------------------------------------------------------------------------
# Selftest -- pins the secret-gate's fail-closed behavior and the dedupe/selection rules against
# synthetic fixtures. No network, no real ~/.baton: pixi run fleet-glass-pusher-selftest.
# ---------------------------------------------------------------------------------------------

def _make_room(root: Path, name: str, outputs_rel: list, state="Succeeded", error=None) -> Path:
    room_dir = root / name
    artifacts_dir = room_dir / "artifacts" / "execution_x"
    artifacts_dir.mkdir(parents=True)
    outputs_abs = []
    for rel, text in outputs_rel:
        p = artifacts_dir / rel
        p.write_text(text, encoding="utf-8")
        outputs_abs.append(str(p))
    (artifacts_dir / "prompt.txt").write_text("the worker's prompt, never uploaded", encoding="utf-8")
    (artifacts_dir / ".stdout.log").write_text("raw stdout, never uploaded", encoding="utf-8")
    (room_dir / "terminal.json").write_text(json.dumps({
        "state": state, "steps": [], "outputs": outputs_abs, "error": error, "try": None,
    }), encoding="utf-8")
    return room_dir



def _selftest() -> int:
    import tempfile
    failures = []

    def check(name, cond):
        if not cond:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        rooms_root = tmp / "rooms"
        rooms_root.mkdir()
        _make_room(rooms_root, "room-a", [("report.md", "# Report A\n\nbody text\n")])
        _make_room(rooms_root, "room-b", [], state="Failed", error="boom")

        # -- fail-closed: patterns file missing entirely --
        missing_patterns = load_secret_patterns(tmp / "does-not-exist.txt")
        check("missing patterns file returns the None sentinel", missing_patterns is None)

        items = gather_deliverables(rooms_root, {}, missing_patterns)
        by_room = {i["room_name"]: i for i in items if i["artifact"]}
        check("deliverable item carries room path in 'room'",
              by_room["room-a"]["room"] == str(rooms_root / "room-a"))
        check("deliverable item carries room name in 'room_name'",
              by_room["room-a"]["room_name"] == "room-a")
        check("fail-closed: room-a's real report is withheld when patterns are missing",
              by_room["room-a"]["withheld"] is True
              and "patterns file missing" in by_room["room-a"]["stub_reason"]
              and "Report A" not in by_room["room-a"]["content"])
        check("fail-closed: prompt.txt/.stdout.log never enter the item stream",
              all("prompt" not in (i.get("artifact") or "") and "stdout" not in (i.get("artifact") or "")
                  for i in items))
        check("a room with zero declared outputs still yields one verdict-only item",
              any(i["room_name"] == "room-b" and i["artifact"] is None and i["verdict"]["error"] == "boom"
                  for i in items))

        # -- patterns present, no hit: real content passes through --
        clean_patterns_file = tmp / "clean.txt"
        clean_patterns_file.write_text("# comment only, no real patterns\n", encoding="utf-8")
        clean_patterns = load_secret_patterns(clean_patterns_file)
        check("an empty-but-present patterns file parses to [] (not the fail-closed sentinel)",
              clean_patterns == [])
        items2 = gather_deliverables(rooms_root, {}, clean_patterns)
        report = next(i for i in items2 if i["room_name"] == "room-a" and i["artifact"])
        check("clean content is uploaded verbatim when nothing matches",
              report["withheld"] is False and "Report A" in report["content"])
        check("title comes from the first markdown heading", report["title"] == "Report A")
        check("deliverable carries ISO-8601 created_at from artifact mtime",
              isinstance(report.get("created_at"), str) and "T" in report["created_at"])
        verdict_only = next(i for i in items2 if i["room_name"] == "room-b")
        check("verdict-only deliverable carries created_at from terminal.json mtime",
              isinstance(verdict_only.get("created_at"), str) and "T" in verdict_only["created_at"])
        unreadable_item = build_item("room-x", tmp / "nonexistent", tmp / "nonexistent" / "missing.md", {}, [])
        check("unreadable artifact omits created_at (never crashes)", "created_at" not in unreadable_item)
        unreadable_verdict = build_verdict_only_item("room-x", {}, tmp / "nonexistent")
        check("missing terminal.json omits created_at", "created_at" not in unreadable_verdict)

        # -- patterns present, a hit: withheld with the matched index, never the pattern text --
        hit_patterns_file = tmp / "hit.txt"
        hit_patterns_file.write_text("sk-[A-Za-z0-9]{10,}\nAKIA[0-9A-Z]{16}\n", encoding="utf-8")
        _make_room(rooms_root, "room-c", [("secret.md", "token: sk-abcdefghijklmnop\n")])
        hit_patterns = load_secret_patterns(hit_patterns_file)
        items3 = gather_deliverables(rooms_root, {}, hit_patterns)
        secret_item = next(i for i in items3 if i["room_name"] == "room-c")
        check("a pattern hit withholds the content", secret_item["withheld"] is True)
        check("the stub names the matched pattern's INDEX, not its text",
              secret_item["stub_reason"] == "matched pattern #0" and "sk-" not in secret_item["content"])

        # -- dedupe: an unchanged (room, artifact, hash) is not re-offered --
        state_after = mark_pushed({}, items2)
        items4 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe skips an already-pushed, unchanged artifact",
              not any(i["room_name"] == "room-a" and i["artifact"] == "artifacts/execution_x/report.md"
                      for i in items4))

        # -- polarity: changed content is offered again despite matching state key --
        (rooms_root / "room-a" / "artifacts" / "execution_x" / "report.md").write_text(
            "# Report A v2\n\nchanged\n", encoding="utf-8")
        items5 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe re-offers an artifact whose content changed",
              any(i["room_name"] == "room-a" and i["title"] == "Report A v2" for i in items5))

        # -- fail-closed is never memorized: gather_deliverables only reads state, it never writes
        # it -- main() is what decides whether to persist, and it skips that when patterns is None
        # (see main()'s "if patterns is not None: save_push_state(...)"). Proven here by calling
        # gather_deliverables twice against the SAME untouched state and requiring identical output,
        # which is exactly what "the caller never got a chance to mark this done" looks like.
        still_missing = load_secret_patterns(tmp / "still-missing.txt")
        first = gather_deliverables(rooms_root, {}, still_missing)
        second = gather_deliverables(rooms_root, {}, still_missing)
        check("a fail-closed run offers the same items every time (nothing here marks it done)",
              [i["id"] for i in first] == [i["id"] for i in second] and len(first) > 0)

        # -- #1617: deliverables join keyed by room path, not room name --
        shared_root1 = tmp / "cluster1" / "rooms"
        shared_root2 = tmp / "cluster2" / "rooms"
        shared_root1.mkdir(parents=True)
        shared_root2.mkdir(parents=True)
        room1_dir = _make_room(shared_root1, "same-name", [("report.md", "# Same Content\n")])
        room2_dir = _make_room(shared_root2, "same-name", [("report.md", "# Same Content\n")])

        items_r1 = gather_deliverables(shared_root1, {}, clean_patterns)
        items_r2 = gather_deliverables(shared_root2, {}, clean_patterns)
        check("deliverable item 'room' is the full room path string",
              items_r1[0]["room"] == str(room1_dir) and items_r2[0]["room"] == str(room2_dir))
        check("deliverable item 'room_name' carries the directory name for display",
              items_r1[0]["room_name"] == "same-name" and items_r2[0]["room_name"] == "same-name")
        check("deliverable item ids are distinct between same-named rooms in different paths",
              items_r1[0]["id"] != items_r2[0]["id"]
              and str(room1_dir) in items_r1[0]["id"]
              and str(room2_dir) in items_r2[0]["id"])

        # Dedupe keying: mark room1 pushed into state.
        state_with_r1 = mark_pushed({}, items_r1)
        check("state keys dedupe by room path, not room name",
              f"{room1_dir}::artifacts/execution_x/report.md" in state_with_r1
              and "same-name::artifacts/execution_x/report.md" not in state_with_r1)

        # Scanning room2 with state_with_r1 must NOT skip room2's deliverable (same name, same content, different path)
        items_r2_after_r1 = gather_deliverables(shared_root2, state_with_r1, clean_patterns)
        check("same-named room in different path is NOT skipped by dedupe when another room with same name and content was pushed",
              len(items_r2_after_r1) == 1 and items_r2_after_r1[0]["room"] == str(room2_dir))

        # (Control) scanning room1 again with state_with_r1 IS skipped by dedupe
        items_r1_again = gather_deliverables(shared_root1, state_with_r1, clean_patterns)
        check("(control) identical room path with unchanged content IS skipped by dedupe",
              len(items_r1_again) == 0)

        # -- Migration on load & format versioning (#1617 / PR #1632) --
        mig_rooms_root = tmp / "mig_rooms"
        mig_rooms_root.mkdir()
        mig_room_dir = _make_room(mig_rooms_root, "room-legacy", [("report.md", "# Legacy Content\n")])
        mig_hash = sha256_hex((mig_room_dir / "artifacts" / "execution_x" / "report.md").read_bytes())
        mig_state_file = tmp / "mig-push-state.json"

        # (a) an old-format state file with one legacy key migrates and the item is NOT re-pushed
        old_state = {"room-legacy::artifacts/execution_x/report.md": mig_hash}
        mig_state_file.write_text(json.dumps(old_state), encoding="utf-8")
        loaded_state = load_push_state(mig_state_file)

        mig_items = gather_deliverables(mig_rooms_root, loaded_state, clean_patterns, state_path=mig_state_file)
        check("(a) old-format state migrates: item is NOT re-pushed", len(mig_items) == 0)
        check("(a) old legacy key is removed from state", "room-legacy::artifacts/execution_x/report.md" not in loaded_state)
        check("(a) path key is adopted in state", f"{mig_room_dir}::artifacts/execution_x/report.md" in loaded_state)
        check("(a) state format version is recorded", loaded_state.get(STATE_FORMAT_VERSION_KEY) == CURRENT_STATE_FORMAT_VERSION)
        persisted_state = load_push_state(mig_state_file)
        check("(a) migrated state is persisted to disk",
              f"{mig_room_dir}::artifacts/execution_x/report.md" in persisted_state
              and "room-legacy::artifacts/execution_x/report.md" not in persisted_state
              and persisted_state.get(STATE_FORMAT_VERSION_KEY) == CURRENT_STATE_FORMAT_VERSION)

        # (b) a legacy key ambiguous between two same-named rooms is not adopted
        ambig_root1 = tmp / "ambig1" / "rooms"
        ambig_root2 = tmp / "ambig2" / "rooms"
        ambig_root1.mkdir(parents=True)
        ambig_root2.mkdir(parents=True)
        ambig_r1 = _make_room(ambig_root1, "ambig-room", [("report.md", "# Clash\n")])
        ambig_r2 = _make_room(ambig_root2, "ambig-room", [("report.md", "# Clash\n")])
        ambig_hash = sha256_hex((ambig_r1 / "artifacts" / "execution_x" / "report.md").read_bytes())
        ambig_state = {"ambig-room::artifacts/execution_x/report.md": ambig_hash}
        terminal_ambig = [(str(ambig_r1), "ambig-room", ambig_r1), (str(ambig_r2), "ambig-room", ambig_r2)]
        migrate_push_state(ambig_state, terminal_ambig)
        check("(b) ambiguous legacy key is not adopted for room 1",
              f"{ambig_r1}::artifacts/execution_x/report.md" not in ambig_state)
        check("(b) ambiguous legacy key is not adopted for room 2",
              f"{ambig_r2}::artifacts/execution_x/report.md" not in ambig_state)

        ambig_items_1 = gather_deliverables(ambig_root1, ambig_state, clean_patterns)
        ambig_items_2 = gather_deliverables(ambig_root2, ambig_state, clean_patterns)
        check("(b) both colliding rooms re-push once",
              len(ambig_items_1) == 1 and len(ambig_items_2) == 1)

        # (c) item id for unchanged deliverable after migration equals id new code computes
        expected_new_id = f"{mig_room_dir}::artifacts/execution_x/report.md::{mig_hash[:16]}"
        verdict = verdict_summary(load_terminal(mig_room_dir))
        computed_item = build_item(str(mig_room_dir), mig_room_dir, mig_room_dir / "artifacts" / "execution_x" / "report.md",
                                   verdict, clean_patterns, room_name="room-legacy")
        check("(c) item id equals the id new code computes (inbox:index dedupe in worker.js replaces rather than duplicates)",
              computed_item["id"] == expected_new_id)

        # -- Deliverables batch capping (#1617 / PR #1632) --
        cap_root = tmp / "cap_rooms"
        cap_root.mkdir()
        for i in range(15):
            _make_room(cap_root, f"room-batch-{i:02d}", [("report.md", f"# Batch {i}\n")])
        capped_items = gather_deliverables(cap_root, {}, clean_patterns, limit=10)
        check("gather_deliverables caps items at limit (default 10) to prevent retry storm",
              len(capped_items) == 10)

    # -- deliver_url derivation --
    check("deliver_url derives from push_url by swapping the path segment",
          derive_deliver_url({"push_url": "https://h/push/TOK"}) == "https://h/deliver/TOK")
    check("deliver_url respects an explicit override",
          derive_deliver_url({"push_url": "https://h/push/TOK", "deliver_url": "https://other/x"}) == "https://other/x")
    check("deliver_url is None when it cannot be derived or configured",
          derive_deliver_url({"push_url": "https://h/nope/TOK"}) is None)

    # -- #1486: heartbeat --
    check("heartbeat_url derives from push_url by swapping the path segment",
          derive_heartbeat_url({"push_url": "https://h/push/TOK"}) == "https://h/heartbeat/TOK")
    check("heartbeat_url respects an explicit override",
          derive_heartbeat_url({"push_url": "https://h/push/TOK", "heartbeat_url": "https://other/x"}) == "https://other/x")
    check("heartbeat_url is None when it cannot be derived or configured",
          derive_heartbeat_url({"push_url": "https://h/nope/TOK"}) is None)

    check("a missing persisted heartbeat timestamp always sends (fail toward one extra write)",
          should_send_heartbeat({}, 10_000.0) is True)
    check("cadence: no beat before the hour is up",
          should_send_heartbeat({HEARTBEAT_STATE_KEY: 10_000.0}, 10_000.0 + HEARTBEAT_INTERVAL_SECONDS - 1) is False)
    check("cadence: a beat is due once the interval has fully elapsed",
          should_send_heartbeat({HEARTBEAT_STATE_KEY: 10_000.0}, 10_000.0 + HEARTBEAT_INTERVAL_SECONDS) is True)

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"

        def _hb_boom():
            raise RuntimeError("heartbeat post failed")

        hb_state = {SNAPSHOT_HASH_KEY: "unrelated-untouched-hash"}
        try:
            send_heartbeat_and_record(_hb_boom, hb_state, sp, 10_000.0)
        except RuntimeError:
            pass
        check("a FAILED heartbeat post persists nothing (no state file written)", not sp.exists())
        check("a FAILED heartbeat post leaves the in-memory state dict untouched",
              HEARTBEAT_STATE_KEY not in hb_state and hb_state[SNAPSHOT_HASH_KEY] == "unrelated-untouched-hash")

        send_heartbeat_and_record(lambda: None, hb_state, sp, 10_000.0)
        check("a successful heartbeat records the timestamp for the next cycle's cadence gate",
              load_push_state(sp).get(HEARTBEAT_STATE_KEY) == 10_000.0)
        check("a successful heartbeat leaves the unrelated snapshot-hash key alone (snapshot path unaffected)",
              load_push_state(sp).get(SNAPSHOT_HASH_KEY) == "unrelated-untouched-hash")

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        extra_state = {DERIVED_PING_STATE_KEY: 5_000.0}
        send_heartbeat_and_record(lambda: None, {}, sp, 5_000.0, extra_state=extra_state)
        check("send_heartbeat_and_record's extra_state (#1613 item 2) lands alongside HEARTBEAT_STATE_KEY",
              load_push_state(sp).get(DERIVED_PING_STATE_KEY) == 5_000.0
              and load_push_state(sp).get(HEARTBEAT_STATE_KEY) == 5_000.0)

        sp2 = Path(tmp) / "push-state-no-extra.json"

        def _boom2():
            raise RuntimeError("boom")

        try:
            send_heartbeat_and_record(_boom2, {}, sp2, 5_000.0, extra_state=extra_state)
        except RuntimeError:
            pass
        check("a FAILED post never lands extra_state either (same all-or-nothing ordering)",
              not sp2.exists())

    # -- #1613 item 2: derived_at ping cadence, decoupled from the hourly heartbeat --
    check("a missing persisted derived_at landing timestamp always pings (fail toward one extra write)",
          should_send_derived_ping({}, 10_000.0) is True)
    check("no ping needed within the interval since the last PUSH landed a fresh derived_at",
          should_send_derived_ping({LAST_PUSH_TS_KEY: 10_000.0}, 10_000.0 + DERIVED_PING_INTERVAL_SECONDS - 1) is False)
    check("a ping is due once the interval has fully elapsed since the last push",
          should_send_derived_ping({LAST_PUSH_TS_KEY: 10_000.0}, 10_000.0 + DERIVED_PING_INTERVAL_SECONDS) is True)
    check("a prior PING (not just a push) also resets the interval",
          should_send_derived_ping({DERIVED_PING_STATE_KEY: 10_000.0}, 10_000.0 + 60) is False)
    check("whichever landed MORE RECENTLY wins -- a fresher ping beats a stale push",
          should_send_derived_ping(
              {LAST_PUSH_TS_KEY: 0.0, DERIVED_PING_STATE_KEY: 10_000.0}, 10_000.0 + 60) is False)
    check("(control) a stale push AND a stale ping both outside the interval -- due",
          should_send_derived_ping(
              {LAST_PUSH_TS_KEY: 0.0, DERIVED_PING_STATE_KEY: 0.0}, DERIVED_PING_INTERVAL_SECONDS) is True)

    # -- #1613 item 2: derived_at rides the ACTUAL posted body but is excluded from the hash that
    # gates the change-gate -- a hash computed from `wrapped` (never touching derived_at) must be
    # identical to one computed from the same `wrapped` regardless of what derived_at value would
    # later be spliced into the posted JSON alongside it.
    wrapped_no_derived = {"rooms": [{"name": "room-a", "state": "Running"}], "underhood": []}
    hash_before = snapshot_hash(wrapped_no_derived)
    posted_body_1 = json.dumps({**wrapped_no_derived, "derived_at": "2026-09-01T00:00:00Z"})
    posted_body_2 = json.dumps({**wrapped_no_derived, "derived_at": "2026-09-01T00:05:00Z"})
    # This review's finding 7: the two checks that used to sit here were both non-discriminating --
    # `hash_before == snapshot_hash(wrapped_no_derived)` is `snapshot_hash(x) == snapshot_hash(x)`
    # (holds no matter what main() hashes), and `"derived_at" not in wrapped_no_derived` restates a
    # literal two lines above. Both would still pass if main() hashed `post_body` instead of
    # `wrapped`. The discriminating claim: hashing the POSTED body (which DOES carry derived_at)
    # gives a DIFFERENT hash than hashing `wrapped` alone -- proving the exclusion at :1156/:1168 is
    # load-bearing, not incidental, and this arm would actually fail if a future edit hashed the
    # wrong thing.
    check("hashing the POSTED body (derived_at included) differs from hashing `wrapped` alone -- "
          "main() must hash `wrapped`, never `post_body`, or the change-gate would re-trigger on "
          "derived_at alone",
          snapshot_hash(json.loads(posted_body_1)) != hash_before)
    check("(control) the two posted bodies DO differ -- proving derived_at actually rides the "
          "wire, it just doesn't gate the push",
          posted_body_1 != posted_body_2)

    # -- #1457: snapshot change-gate (KV daily quota) --
    wrapped_a = {"rooms": [{"name": "room-a", "state": "Running"}], "underhood": []}
    wrapped_a_reordered = {"underhood": [], "rooms": [{"state": "Running", "name": "room-a"}]}
    wrapped_b = {"rooms": [{"name": "room-a", "state": "Succeeded"}], "underhood": []}
    hash_a = snapshot_hash(wrapped_a)
    check("snapshot_hash is stable across dict key/field order (sort_keys)",
          hash_a == snapshot_hash(wrapped_a_reordered))
    check("snapshot_hash changes when the wrapped body's content changes",
          hash_a != snapshot_hash(wrapped_b))

    check("a missing persisted hash always pushes (fail toward one extra write)",
          should_push_snapshot({}, hash_a) is True)
    check("an unreadable/missing state.get sentinel (None) never matches a real hash",
          should_push_snapshot({SNAPSHOT_HASH_KEY: None}, hash_a) is True)
    check("a matching persisted hash skips the push",
          should_push_snapshot({SNAPSHOT_HASH_KEY: hash_a}, hash_a) is False)
    check("a stale persisted hash (content changed since) triggers a push",
          should_push_snapshot({SNAPSHOT_HASH_KEY: hash_a}, snapshot_hash(wrapped_b)) is True)

    check("should_log_skip fires on the first skip of a streak",
          should_log_skip(1, 24) is True)
    check("should_log_skip is quiet between the coarse cadence points",
          all(not should_log_skip(n, 24) for n in range(2, 24)))
    check("should_log_skip fires again at the coarse cadence boundary",
          should_log_skip(24, 24) is True and should_log_skip(48, 24) is True)

    # Post-before-save ordering (#1457 review finding A): a raising post must leave the state file
    # untouched; a succeeding one must persist the hash. Real temp file, stubbed post.
    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"

        def _boom(_body):
            raise RuntimeError("post failed")

        try:
            push_snapshot_and_record(_boom, "{}", {}, sp, hash_a)
        except RuntimeError:
            pass
        check("a FAILED post persists nothing (state file untouched, retries next cycle)",
              not sp.exists())
        push_snapshot_and_record(lambda _body: None, "{}", {}, sp, hash_a)
        check("a successful post persists the hash for the next cycle's gate",
              load_push_state(sp).get(SNAPSHOT_HASH_KEY) == hash_a)

    # -- #1505: timeline extraction strips content-bearing fields (the mailbox's stdout boundary) --
    stdout_leak = "SECRET_STDOUT_LINE_THAT_MUST_NEVER_RIDE_THE_MAILBOX"
    fake_room_detail = {
        "name": "room-x",
        "stdout": {"text": stdout_leak, "truncated": False, "totalBytes": 999, "source": "execution_1"},
        "timeline": {
            "entries": [
                {"type": "flow.ExecutionRequestAccepted", "timestamp": "2026-08-31T00:00:00Z"},
                {"type": "core.ExecutionStarted", "timestamp": "2026-08-31T00:00:01Z", "detail": stdout_leak},
            ],
            "truncated": False,
            "totalEntries": 2,
        },
        "note": stdout_leak,
    }
    extracted = extract_timeline(fake_room_detail)
    check("extract_timeline keeps real type+timestamp entries (positive control)",
          extracted == [
              {"type": "flow.ExecutionRequestAccepted", "timestamp": "2026-08-31T00:00:00Z"},
              {"type": "core.ExecutionStarted", "timestamp": "2026-08-31T00:00:01Z"},
          ])
    check("extract_timeline drops an entry's `detail` field even when populated",
          all("detail" not in e for e in extracted))
    # The claim under test is "none of it touches the SNAPSHOT" -- prove it against the body main()
    # actually pushes by building it through the SAME build_wrapped() main() calls, so a future edit
    # to that construction can't silently invalidate this proof.
    wrapped_with_timeline = build_wrapped([], [], {"/rooms/room-x": extracted}, 0)
    serialized = json.dumps(wrapped_with_timeline)
    check("the stdout/detail/note leak string is absent from the fully serialized pushed body",
          stdout_leak not in serialized)

    # Negative control for the positive-control claim above: an extractor that always returns []
    # would also pass the leak check for the wrong reason -- prove real entries actually survive.
    check("(control) a non-empty timeline still carries its real entries into the serialized body",
          "flow.ExecutionRequestAccepted" in serialized)

    unreadable_detail = extract_timeline({
        "timeline": {"entries": [{"type": "unreadable", "detail": "ledger held by pid 1234, path C:\\secret\\room"}],
                     "truncated": False, "totalEntries": 1}
    })
    check("an 'unreadable' marker entry survives as a type-only marker",
          unreadable_detail == [{"type": "unreadable"}])

    check("extract_timeline caps at TIMELINE_CAP entries, keeping the newest tail",
          extract_timeline({"timeline": {"entries": [{"type": f"e{i}"} for i in range(TIMELINE_CAP + 5)],
                                          "truncated": False, "totalEntries": TIMELINE_CAP + 5}}) ==
          [{"type": f"e{i}"} for i in range(5, TIMELINE_CAP + 5)])

    check("extract_timeline degrades to [] for a room_detail response with no timeline at all",
          extract_timeline({"name": "room-y", "note": "no flow.jsonl yet"}) == [])

    # #1537: extract_timeline admits every event TYPE -- it has never filtered on `type`, only on
    # field shape (KEEP-ONLY type+timestamp, see the function's own docstring). This is the
    # discriminating control for that claim: it would fail the moment anyone added a type-keyed
    # allowlist, including one that (wrongly) tried to list "every type we know about today" --
    # the "someFutureType" entry has no home in FlowEvent.cs/CoreEvent.cs/RoomEvent.cs and must
    # still survive. The 29 real tags are current as of this change (10 flow + 2 core + 17 room);
    # they are a snapshot for this test's own realism, not a source of truth the engine must keep
    # in sync -- the engine is the source of truth, and this test doesn't police it.
    every_known_type = [
        "flow.executionRequestAccepted", "flow.executionRequestRejected", "flow.executionSucceeded",
        "flow.executionFailed", "flow.executionCancelled", "flow.cancellationRequested",
        "flow.workflowPaused", "flow.externalDecisionRecorded", "flow.workflowResumed",
        "flow.stepRetryScheduled",
        "core.executionStarted", "core.executionExited",
        "room.heldWorkDispatched", "room.heldWorkEscalated", "room.heldWorkResolved",
        "room.grantRecorded", "room.grantAmended", "room.grantRevoked", "room.escalationRaised",
        "room.turnHostDormancyEntered", "room.turnHostDormancyCleared",
        "room.runtimePermissionAsked", "room.runtimePermissionAnswered", "room.runtimePermissionRevoked",
        "room.workflowSwitched", "room.standingPermissionRevoked",
        "room.workerJoined", "room.workerRenamed", "room.orchestratorAssigned",
        "flow.someFutureType",
    ]
    assert len(every_known_type) <= TIMELINE_CAP, \
        "synthetic list outgrew TIMELINE_CAP -- shorten it; this is not a filter to widen"
    admitted = extract_timeline({
        "timeline": {"entries": [{"type": t, "timestamp": "2026-08-31T00:00:00Z"} for t in every_known_type],
                     "truncated": False, "totalEntries": len(every_known_type)}
    })
    check("extract_timeline admits every event type unfiltered, known or not -- no type-keyed allowlist",
          [e["type"] for e in admitted] == every_known_type)

    # -- #1613 item 4: stepId/exitCode are ids/counts, kept -- but only where the entry has them,
    # never fabricated, and the stdout leak check above still holds with them present --
    step_exit_entries = extract_timeline({
        "timeline": {
            "entries": [
                {"type": "flow.executionRequestAccepted", "timestamp": "2026-09-01T00:00:00Z", "stepId": "build"},
                {"type": "core.executionExited", "timestamp": "2026-09-01T00:00:05Z", "exitCode": 0},
                {"type": "core.executionExited", "timestamp": "2026-09-01T00:00:06Z", "exitCode": -1},
                {"type": "flow.executionSucceeded", "timestamp": "2026-09-01T00:00:07Z"},
            ],
            "truncated": False, "totalEntries": 4,
        }
    })
    check("extract_timeline keeps stepId where the entry carries one",
          step_exit_entries[0] == {"type": "flow.executionRequestAccepted", "timestamp": "2026-09-01T00:00:00Z", "stepId": "build"})
    check("extract_timeline keeps exitCode where the entry carries one, including zero and negative",
          step_exit_entries[1]["exitCode"] == 0 and step_exit_entries[2]["exitCode"] == -1)
    check("extract_timeline omits stepId/exitCode where the entry carries neither",
          "stepId" not in step_exit_entries[3] and "exitCode" not in step_exit_entries[3])
    check("extract_timeline never invents stepId/exitCode on an entry that lacks them",
          "exitCode" not in step_exit_entries[0] and "stepId" not in step_exit_entries[1])

    # -- #1613 item 4: terminal-timeline caching policy (fetch once, not per cycle) --
    fetch_calls = []

    def counting_fetch(room_path):
        fetch_calls.append(room_path)
        return [{"type": "flow.executionSucceeded"}]

    term_cache: dict = {}
    first = resolve_room_timeline("/rooms/term-a", True, term_cache, counting_fetch)
    check("first call for a terminal room fetches", fetch_calls == ["/rooms/term-a"])
    check("first call's result is cached", term_cache.get("/rooms/term-a") == first)
    second = resolve_room_timeline("/rooms/term-a", True, term_cache, counting_fetch)
    check("a SECOND cycle's call for the SAME terminal room does NOT fetch again (cache hit)",
          fetch_calls == ["/rooms/term-a"] and second == first)

    fetch_calls.clear()
    resolve_room_timeline("/rooms/live-a", False, term_cache, counting_fetch)
    resolve_room_timeline("/rooms/live-a", False, term_cache, counting_fetch)
    check("(control) a non-terminal room fetches on EVERY call, cache or not",
          fetch_calls == ["/rooms/live-a", "/rooms/live-a"])

    empty_calls = []

    def empty_fetch(room_path):
        empty_calls.append(room_path)
        return []

    empty_cache: dict = {}
    resolve_room_timeline("/rooms/term-empty", True, empty_cache, empty_fetch)
    resolve_room_timeline("/rooms/term-empty", True, empty_cache, empty_fetch)
    check("a terminal room whose fetch returns [] is never cached, so it retries every cycle",
          empty_calls == ["/rooms/term-empty", "/rooms/term-empty"]
          and "/rooms/term-empty" not in empty_cache)

    # -- #1613 item 1: live telemetry for Running rooms --
    check("extract_live_counts counts claude tool_use blocks across assistant events",
          extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "text", "text": "hi"}]}}),
              json.dumps({"type": "assistant", "message": {"content": [
                  {"type": "tool_use", "name": "Bash", "input": {"command": "ls"}},
                  {"type": "tool_use", "name": "Read", "input": {"path": "x"}},
              ]}}),
          ]) == {"toolCalls": 2})
    check("extract_live_counts counts agy DONE/tool step_update heartbeats",
          extract_live_counts([
              json.dumps({"event": "init"}),
              json.dumps({"event": "step_update", "step_update": {"state": "ACTIVE", "step_type": "tool"}}),
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}}),
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "agent_response"}}),
          ]) == {"toolCalls": 1})
    check("extract_live_counts ignores a torn/unparseable last line instead of raising",
          extract_live_counts(['{"type": "assistant", "message": {"content": [{"type": "tool_use"}]}}',
                                '{"type": "assistant", "message": {"conte']) == {"toolCalls": 1})
    check("extract_live_counts also counts a tool step at its ERROR terminal state, not DONE only "
          "(#1686 review F3 -- mirrors the engine's own ClaudeUsageParser/AgyUsageParser.CountToolSteps "
          "DONE-or-ERROR unit; previously a failed agy tool call incremented the engine's arrest count "
          "without incrementing the operator's lane-card count)",
          extract_live_counts([
              json.dumps({"event": "step_update", "step_update": {"state": "ERROR", "step_type": "tool"}}),
          ]) == {"toolCalls": 1})
    # -- #1682: billed tokens/turns for BOTH vendors, on the shape a real capture confirmed
    # 2026-09-01/02 (docs/vendor-capabilities.md) -- `message.usage` on every claude `assistant`
    # line and agy's DONE/agent_response `step_update.usage`, not just either vendor's terminal line.
    real_assistant_usage_line = json.dumps({
        "type": "assistant",
        "message": {
            "content": [{"type": "text", "text": "ok"}],
            "usage": {
                "input_tokens": 2, "cache_creation_input_tokens": 12066,
                "cache_read_input_tokens": 15092, "output_tokens": 4,
                "service_tier": "standard",
            },
        },
    })
    real_counts = extract_live_counts([real_assistant_usage_line])
    check("billedTokens is input + output + cache_creation off the real captured claude envelope shape "
          "(NOT thinking, and NOT cache_read, which is display-only)",
          real_counts.get("billedTokens") == 2 + 4 + 12066)
    check("turns is 1 for a single usage-bearing line", real_counts.get("turns") == 1)
    check("contextTokens sums the message's three input-side usage counts (fresh input plus both "
          "cache counters)",
          real_counts.get("context", {}).get("contextTokens") == 2 + 12066 + 15092)
    check("cacheReadTokens is cache_read_input_tokens alone",
          real_counts.get("context", {}).get("cacheReadTokens") == 15092)

    check("billedTokens/turns are ADDITIVE across multiple assistant messages in one batch "
          "(whole-tree, including subagent assistant lines, which are never filtered out)",
          extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {"input_tokens": 10, "output_tokens": 100}}}),
              json.dumps({"type": "assistant", "message": {"usage": {"input_tokens": 20, "output_tokens": 30}}}),
          ]).get("billedTokens") == 160
          and extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {"input_tokens": 10, "output_tokens": 100}}}),
              json.dumps({"type": "assistant", "message": {"usage": {"input_tokens": 20, "output_tokens": 30}}}),
          ]).get("turns") == 2)
    check("context is the LATEST message's level within a batch, never summed across messages",
          extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {
                  "output_tokens": 1, "input_tokens": 100, "cache_read_input_tokens": 0,
                  "cache_creation_input_tokens": 0}}}),
              json.dumps({"type": "assistant", "message": {"usage": {
                  "output_tokens": 1, "input_tokens": 5, "cache_read_input_tokens": 200,
                  "cache_creation_input_tokens": 0}}}),
          ]).get("context") == {"contextTokens": 205, "cacheReadTokens": 200})
    check("billedTokens/turns are ABSENT, never a substituted zero, when no line reports usage",
          "billedTokens" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use"}]}})])
          and "turns" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use"}]}})]))
    check("context is ABSENT when the cache fields aren't ALL present -- never a partial figure "
          "built from input_tokens alone (the trap the original ruling correctly named)",
          "context" not in extract_live_counts([
              json.dumps({"type": "assistant", "message": {"usage": {"output_tokens": 4, "input_tokens": 2}}})]))
    check("agy DONE/tool step_update heartbeats contribute no token fields (no usage on that step_type)",
          extract_live_counts([
              json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}})
          ]) == {"toolCalls": 1})
    # #1682: corrects the prior claim that agy carries "no usage field to read at all" -- a real
    # capture (dispatch-implement-38c24d11) shows DONE/agent_response step_updates DO carry one.
    real_agy_usage_line = json.dumps({
        "event": "step_update",
        "step_update": {
            "state": "DONE", "step_type": "agent_response",
            "usage": {"input_tokens": 14205, "output_tokens": 443, "thinking_tokens": 349,
                       "cache_read_tokens": 0, "total_tokens": 14648},
        },
    })
    real_agy_counts = extract_live_counts([real_agy_usage_line])
    check("billedTokens reads agy's DONE/agent_response step_update.usage (input + output, NOT thinking)",
          real_agy_counts.get("billedTokens") == 14205 + 443)
    check("turns is 1 for a single agy usage-bearing line", real_agy_counts.get("turns") == 1)
    check("agy step_update contributes no `context` -- claude-only (no cache_creation figure to build a trio from)",
          "context" not in real_agy_counts)
    check("a terminal `result` line's usage never leaks into live counts -- only type==assistant/step_update are read",
          extract_live_counts([
              json.dumps({"type": "result", "usage": {"output_tokens": 999, "input_tokens": 999}})
          ]) == {"toolCalls": 0})

    # #1686 review F6 -- extract_live_counts's own docstring above has the measured shape this
    # reproduces; dedupe by message.id closes it.
    dup_message_lines = [
        json.dumps({"type": "assistant", "message": {"id": "msg_1", "usage": {"input_tokens": 100, "output_tokens": 10}}}),
        json.dumps({"type": "assistant", "message": {"id": "msg_1", "usage": {"input_tokens": 100, "output_tokens": 10}}}),
        json.dumps({"type": "assistant", "message": {"id": "msg_2", "usage": {"input_tokens": 50, "output_tokens": 5}}}),
    ]
    dup_seen_ids: set = set()
    dup_counts = extract_live_counts(dup_message_lines, dup_seen_ids)
    check("billedTokens dedupes a repeated message.id instead of summing it twice",
          dup_counts.get("billedTokens") == (100 + 10) + (50 + 5))
    check("turns dedupes the same way", dup_counts.get("turns") == 2)

    # A repeat that arrives in a LATER batch (a later poll cycle) must still dedupe against the SAME
    # persistent seen_message_ids the caller threads through live_cache's per-execution state.
    later_batch_counts = extract_live_counts(
        [json.dumps({"type": "assistant", "message": {"id": "msg_1", "usage": {"input_tokens": 100, "output_tokens": 10}}})],
        dup_seen_ids)
    check("a repeated message.id in a LATER batch (persistent seen-set) still dedupes",
          "billedTokens" not in later_batch_counts)

    check("live_telemetry_for_room is None with no Running step",
          live_telemetry_for_room({"path": "/rooms/x", "steps": [{"id": "s1", "state": "Succeeded"}]}) is None)
    check("live_telemetry_for_room is None when the Running step has no captured stdout yet",
          live_telemetry_for_room({
              "path": str(Path(tempfile.mkdtemp()) / "nonexistent-room"),
              "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}],
          }) is None)

    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "live-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-live-1"
        exec_dir.mkdir(parents=True)
        (exec_dir / ".stdout.log").write_text(
            json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash"}]}}) + "\n",
            encoding="utf-8")
        live = live_telemetry_for_room({
            "path": str(room_dir),
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-live-1"}],
        })
        check("live_telemetry_for_room reads the Running step's own .stdout.log and counts tool calls",
              live is not None and live["toolCalls"] == 1)
        check("live_telemetry_for_room's lastActivityAt is a real ISO instant (the file's own mtime)",
              live is not None and isinstance(live.get("lastActivityAt"), str) and "T" in live["lastActivityAt"])

        pruned_dir = room_dir / "artifacts" / "pruned" / "execution_exec-pruned-1"
        pruned_dir.mkdir(parents=True)
        (pruned_dir / ".stdout.log").write_text(
            json.dumps({"event": "step_update", "step_update": {"state": "DONE", "step_type": "tool"}}) + "\n",
            encoding="utf-8")
        live_pruned = live_telemetry_for_room({
            "path": str(room_dir),
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-pruned-1"}],
        })
        check("live_telemetry_for_room falls back to artifacts/pruned, same as the engine side",
              live_pruned is not None and live_pruned["toolCalls"] == 1)

    running_room = {"path": "/rooms/r", "state": "Running",
                     "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}]}
    stalled_room = {"path": "/rooms/s", "state": "Stalled",
                     "steps": [{"id": "s1", "state": "Running", "execution": "exec-none"}]}
    room_list_for_live = [running_room, stalled_room]
    attach_live_telemetry(room_list_for_live, {})
    check("attach_live_telemetry never adds a `live` key it cannot honestly back (no stdout yet)",
          "live" not in running_room)
    check("attach_live_telemetry gates on the DISPLAYED state, never touching a Stalled room "
          "(#1513 confirmed-dead) even though its raw step still reads Running",
          "live" not in stalled_room)

    # -- this review, finding 4: incremental reading -- a second cycle over an UNCHANGED cache only
    # counts newly-appended bytes, never re-parses the whole file. --
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "incremental-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-inc-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"
        stdout_path.write_text(json.dumps(
            {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash"}]}}) + "\n",
            encoding="utf-8")
        inc_room = {"path": str(room_dir), "steps": [{"id": "s1", "state": "Running", "execution": "exec-inc-1"}]}
        inc_cache: dict = {}
        inc_live1 = live_telemetry_for_room(inc_room, inc_cache)
        check("first cycle counts the initial tool call", inc_live1["toolCalls"] == 1)
        state_after_1 = inc_cache[f"{room_dir}::exec-inc-1"]
        check("first cycle's offset advances to the end of the file it just read",
              state_after_1["stdout_offset"] == stdout_path.stat().st_size)

        with stdout_path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(
                {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Read"}]}}) + "\n")
        inc_live2 = live_telemetry_for_room(inc_room, inc_cache)
        check("second cycle ADDS only the newly appended tool call -- proving this reads the delta, "
              "not the whole file again (a whole-file re-read would also land on 2, so this is "
              "checked together with the offset assertion above/below, not alone)",
              inc_live2["toolCalls"] == 2)
        check("a cycle with nothing new appended leaves the offset (and count) unchanged",
              live_telemetry_for_room(inc_room, inc_cache)["toolCalls"] == 2)

    # -- this review, finding 3: `.stdout.log` rollover at 8 MiB (ExecutionStreamLogger.cs) must
    # never silently reset the count to zero -- a size DECREASE is the rollover signal. --
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "rollover-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-roll-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"

        def _tool_line(name):
            return json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use", "name": name}]}}) + "\n"

        stdout_path.write_text(_tool_line("Bash"), encoding="utf-8")
        roll_room = {"path": str(room_dir), "steps": [{"id": "s1", "state": "Running", "execution": "exec-roll-1"}]}
        roll_cache: dict = {}
        check("pre-rollover: first cycle counts the initial tool call",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 1)
        with stdout_path.open("a", encoding="utf-8") as f:
            f.write(_tool_line("Read"))
        check("pre-rollover: second cycle adds the newly appended call",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 2)

        # Simulate ExecutionStreamLogger's single rollover: a REAL rollover is a rename, so the
        # moved file carries exactly what the pusher had already (fully) caught up to -- a fresh,
        # much smaller `.stdout.log` starts alongside it.
        rollover_path = exec_dir / ".stdout.log.1"
        rollover_path.write_text(stdout_path.read_text(encoding="utf-8"), encoding="utf-8")
        stdout_path.write_text(_tool_line("Grep"), encoding="utf-8")
        live_after_rollover = live_telemetry_for_room(roll_room, roll_cache)
        check("finding 3: toolCalls stays MONOTONIC across a rollover -- the pre-rollover count is "
              "preserved (never reset to zero) and the post-rollover file's own new call is added",
              live_after_rollover["toolCalls"] == 3)
        check("a cycle AFTER the rollover, with nothing new, never re-counts the rollover file again",
              live_telemetry_for_room(roll_room, roll_cache)["toolCalls"] == 3)

    # -- this review, finding 1: lastActivityAt is quantized to a coarse bucket before it enters the
    # payload, bounding snapshot_hash churn for a continuously-streaming lane. --
    bucket_aligned_base = LAST_ACTIVITY_BUCKET_SECONDS * 10.0  # exactly on a bucket boundary
    check("two mtimes inside the same bucket produce an identical lastActivityAt",
          _quantized_activity_iso(bucket_aligned_base)
          == _quantized_activity_iso(bucket_aligned_base + LAST_ACTIVITY_BUCKET_SECONDS - 1))
    check("crossing a bucket boundary changes lastActivityAt",
          _quantized_activity_iso(bucket_aligned_base)
          != _quantized_activity_iso(bucket_aligned_base + LAST_ACTIVITY_BUCKET_SECONDS))

    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "streaming-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-stream-1"
        exec_dir.mkdir(parents=True)
        stdout_path = exec_dir / ".stdout.log"
        stdout_path.write_text("", encoding="utf-8")

        def _streaming_room():
            return {"name": "streaming-room", "path": str(room_dir), "state": "Running",
                    "steps": [{"id": "s1", "state": "Running", "execution": "exec-stream-1"}]}

        stream_cache: dict = {}
        # Bucket-aligned so "+10" is guaranteed to stay inside the same bucket below.
        base_mtime = (1_700_000_000.0 // LAST_ACTIVITY_BUCKET_SECONDS) * LAST_ACTIVITY_BUCKET_SECONDS
        os.utime(stdout_path, (base_mtime, base_mtime))
        rooms_1 = [_streaming_room()]
        attach_live_telemetry(rooms_1, stream_cache)
        hash_1 = snapshot_hash(build_wrapped(rooms_1, [], {}, 0))

        os.utime(stdout_path, (base_mtime + 10, base_mtime + 10))  # same bucket
        rooms_2 = [_streaming_room()]
        attach_live_telemetry(rooms_2, stream_cache)
        hash_2 = snapshot_hash(build_wrapped(rooms_2, [], {}, 0))
        check("finding 1: mtime advancing WITHIN one bucket leaves the pushed snapshot hash "
              "unchanged -- a continuously-streaming lane no longer forces a push every cycle",
              hash_1 == hash_2)

        os.utime(stdout_path, (base_mtime + LAST_ACTIVITY_BUCKET_SECONDS, base_mtime + LAST_ACTIVITY_BUCKET_SECONDS))
        rooms_3 = [_streaming_room()]
        attach_live_telemetry(rooms_3, stream_cache)
        hash_3 = snapshot_hash(build_wrapped(rooms_3, [], {}, 0))
        check("finding 1: crossing a bucket boundary DOES change the pushed snapshot hash",
              hash_1 != hash_3)

    # -- this review, finding 2: pending_push_age_s -- absent when nothing is waiting to push, and
    # growing from the last SUCCESSFUL push while content keeps failing to go out. --
    check("pending_push_age_s is None when the persisted hash already matches (nothing waiting)",
          pending_push_age_s({SNAPSHOT_HASH_KEY: "h", LAST_PUSH_TS_KEY: 0.0}, "h", 10_000.0) is None)
    check("pending_push_age_s is None with no successful-push baseline yet, even if content waits",
          pending_push_age_s({}, "h", 10_000.0) is None)
    check("pending_push_age_s is the elapsed time since the last SUCCESSFUL push, while content "
          "still differs from the persisted hash",
          pending_push_age_s({LAST_PUSH_TS_KEY: 9_000.0}, "h", 10_000.0) == 1_000.0)

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        push_fail_state = {LAST_PUSH_TS_KEY: 0.0, SNAPSHOT_HASH_KEY: "old-hash"}

        def _push_boom(_body):
            raise RuntimeError("push failed (e.g. a 413 from the 1 MB cap)")

        try:
            push_snapshot_and_record(_push_boom, "{}", push_fail_state, sp, "new-hash", now_ts=100.0)
        except RuntimeError:
            pass
        check("finding 2: a FAILED push leaves LAST_PUSH_TS_KEY frozen, so pending_push_age_s keeps "
              "GROWING cycle over cycle instead of resetting -- this is what lets the heartbeat ping "
              "carry a growing pending age while pushes keep failing",
              pending_push_age_s(push_fail_state, "new-hash", 100.0) == 100.0
              and pending_push_age_s(push_fail_state, "new-hash", 500.0) == 500.0)

    # -- #1613 item 1: live telemetry is attached AFTER stale-filtering, never before -- it plays no
    # part in the staleness decision at all, sidestepping the exhaustedUntil-shaped landmine by
    # construction (ordering) rather than by adding it to newest_timestamp's skip set.
    with tempfile.TemporaryDirectory() as tmp:
        room_dir = Path(tmp) / "old-but-live-room"
        exec_dir = room_dir / "artifacts" / "execution_exec-old-1"
        exec_dir.mkdir(parents=True)
        (exec_dir / ".stdout.log").write_text("{}\n", encoding="utf-8")
        old_step_iso = datetime.fromtimestamp(
            datetime.now(timezone.utc).timestamp() - 10 * 86400, tz=timezone.utc).isoformat()
        stale_room_body = json.dumps([{
            "name": "old-but-live-room", "path": str(room_dir), "state": "Running",
            "steps": [{"id": "s1", "state": "Running", "execution": "exec-old-1", "timestamp": old_step_iso}],
        }])
        filtered, dropped = drop_stale_rooms(stale_room_body, max_age_days=3)
        check("a room with only an old step timestamp still drops as stale BEFORE live telemetry "
              "is ever attached -- a fresh .stdout.log mtime never rescues it from the filter",
              dropped == 1 and json.loads(filtered) == [])

    # -- #1505: stale-room drop becomes a visible count, never a silent disappearance (landmine #43) --
    now_iso = datetime.now(timezone.utc).isoformat()
    old_iso = (datetime.now(timezone.utc).timestamp() - 10 * 86400)
    old_iso = datetime.fromtimestamp(old_iso, tz=timezone.utc).isoformat()
    stale_body = json.dumps([
        {"name": "fresh", "state": "Running", "steps": [{"id": "s1", "state": "Running", "timestamp": now_iso}]},
        {"name": "zombie", "state": "Running", "steps": [{"id": "s1", "state": "Running", "timestamp": old_iso}]},
        {"name": "no-timestamp", "state": "Failed"},
    ])
    filtered_body, dropped_count = drop_stale_rooms(stale_body, max_age_days=3)
    check("drop_stale_rooms reports a non-zero dropped count for an aged zombie room",
          dropped_count == 1)
    filtered_rooms = json.loads(filtered_body)
    check("the dropped count matches what's actually missing from the filtered list",
          len(filtered_rooms) == 2 and not any(r["name"] == "zombie" for r in filtered_rooms))
    check("a room with no parseable timestamp is kept, not silently dropped",
          any(r["name"] == "no-timestamp" for r in filtered_rooms))

    fresh_body, fresh_dropped = drop_stale_rooms(
        json.dumps([{"name": "fresh", "state": "Running",
                     "steps": [{"id": "s1", "state": "Running", "timestamp": now_iso}]}]),
        max_age_days=3)
    check("(control) nothing dropped when every room is recent", fresh_dropped == 0)

    # -- #1551: an abandoned parked room's real (old) step timestamp must still win over its
    # FUTURE exhaustedUntil reset instant, or a room nobody is watching never ages out --
    future_iso = (datetime.now(timezone.utc).timestamp() + 30 * 86400)
    future_iso = datetime.fromtimestamp(future_iso, tz=timezone.utc).isoformat()
    check("(control) exhaustedUntil alone reads as the room's newest timestamp when included",
          newest_timestamp({"steps": [{"exhaustedUntil": future_iso}]}, _skip_keys=frozenset()) == future_iso)
    parked_body = json.dumps([
        {"name": "abandoned-park", "state": "Running",
         "steps": [{"id": "s1", "state": "Failed", "timestamp": old_iso, "exhaustedUntil": future_iso}]},
    ])
    parked_filtered, parked_dropped = drop_stale_rooms(parked_body, max_age_days=3)
    check("an abandoned parked room drops as stale off its real (old) step timestamp, "
          "not its future exhaustedUntil reset instant",
          parked_dropped == 1 and json.loads(parked_filtered) == [])

    # -- #1538: single-instance guard --
    with tempfile.TemporaryDirectory() as tmp:
        tmp_dir = Path(tmp)
        lock_file = tmp_dir / "pusher.lock"

        # 1. Clean acquisition and release
        check("acquire_lock succeeds on fresh lock file",
              acquire_lock(lock_file, pid=11111) is True)
        check("lock file holds the claimed PID",
              read_lock_pid(lock_file) == 11111)
        # Release with wrong PID must not delete lock file
        release_lock(lock_file, pid=22222)
        check("release_lock ignores non-matching PID",
              lock_file.is_file() and read_lock_pid(lock_file) == 11111)
        # Release with matching PID deletes lock file
        release_lock(lock_file, pid=11111)
        check("release_lock cleans up when PID matches",
              not lock_file.exists())

        # 2. Reclaim stale lock from a fake dead PID
        lock_file.write_text("99999999\n", encoding="utf-8")
        check("dead PID is recognized as not alive",
              is_pid_alive(99999999) is False)
        check("acquire_lock reclaims lock from dead PID",
              acquire_lock(lock_file, pid=33333) is True)
        check("reclaimed lock now holds the new PID",
              read_lock_pid(lock_file) == 33333)
        release_lock(lock_file, pid=33333)

        # 3. Reclaim from corrupted lock file
        lock_file.write_text("not-a-pid\n", encoding="utf-8")
        check("acquire_lock reclaims unreadable lock file",
              acquire_lock(lock_file, pid=44444) is True)
        check("reclaimed lock holds new PID after corruption",
              read_lock_pid(lock_file) == 44444)
        release_lock(lock_file, pid=44444)

        # 4. Replace running pusher instance
        proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(15) # pusher_test_subproc"])
        try:
            lock_file.write_text(f"{proc.pid}\n", encoding="utf-8")
            check("child process is alive", is_pid_alive(proc.pid) is True)
            check("child process command line contains pusher", "pusher" in get_process_cmdline(proc.pid).lower())
            check("acquire_lock terminates and replaces running pusher",
                  acquire_lock(lock_file, pid=55555) is True)
            for _ in range(30):
                if proc.poll() is not None:
                    break
                time.sleep(0.05)
            check("stale pusher process was terminated", proc.poll() is not None)
            check("lock now belongs to new PID", read_lock_pid(lock_file) == 55555)
        finally:
            if proc.poll() is None:
                proc.terminate()
            release_lock(lock_file, pid=55555)

        # 5. Non-pusher process is NOT killed when lock is reclaimed
        proc_unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(15) # unrelated_task"])
        try:
            lock_file.write_text(f"{proc_unrelated.pid}\n", encoding="utf-8")
            check("acquire_lock reclaims lock from non-pusher without killing it",
                  acquire_lock(lock_file, pid=66666) is True)
            check("non-pusher process remains alive", proc_unrelated.poll() is None)
        finally:
            if proc_unrelated.poll() is None:
                proc_unrelated.terminate()
            release_lock(lock_file, pid=66666)

    # -- #1538: coalescing floor (KV daily cap protection) --
    check("should_coalesce_push is False when no prior push recorded",
          should_coalesce_push({}, 1000.0, 90) is False)
    check("should_coalesce_push is True within min_interval window",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1050.0, 90) is True)
    check("should_coalesce_push is False once min_interval has elapsed",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1090.0, 90) is False)
    check("should_coalesce_push is False past min_interval window",
          should_coalesce_push({LAST_PUSH_TS_KEY: 1000.0}, 1150.0, 90) is False)

    with tempfile.TemporaryDirectory() as tmp:
        sp = Path(tmp) / "push-state.json"
        state = {}

        # Cycle 1: initial push at t=0
        h1 = "hash_1"
        check("cycle 1: should push new content", should_push_snapshot(state, h1) is True)
        check("cycle 1: should not coalesce first push", should_coalesce_push(state, 0.0, 90) is False)
        push_snapshot_and_record(lambda _b: None, "{}", state, sp, h1, now_ts=0.0)
        check("cycle 1: state records snapshot hash", state.get(SNAPSHOT_HASH_KEY) == h1)
        check("cycle 1: state records push timestamp", state.get(LAST_PUSH_TS_KEY) == 0.0)

        # Cycle 2: unchanged content at t=25
        check("cycle 2: unchanged content is skipped by change-gate",
              should_push_snapshot(state, h1) is False)

        # Cycle 3: changed content at t=50 (within 90s floor)
        h2 = "hash_2"
        check("cycle 3: change-gate detects new hash", should_push_snapshot(state, h2) is True)
        check("cycle 3: floor coalesces push (50s < 90s)", should_coalesce_push(state, 50.0, 90) is True)
        check("cycle 3: persisted hash remains unchanged across coalesced cycle",
              state.get(SNAPSHOT_HASH_KEY) == h1 and state.get(LAST_PUSH_TS_KEY) == 0.0)

        # Cycle 4: changed content still waiting at t=75 (within 90s floor)
        check("cycle 4: change-gate still wants to push", should_push_snapshot(state, h2) is True)
        check("cycle 4: floor still coalesces (75s < 90s)", should_coalesce_push(state, 75.0, 90) is True)

        # Cycle 5: floor expires at t=95 (>= 90s)
        check("cycle 5: change-gate still wants to push", should_push_snapshot(state, h2) is True)
        check("cycle 5: floor allows push (95s >= 90s)", should_coalesce_push(state, 95.0, 90) is False)
        push_snapshot_and_record(lambda _b: None, "{}", state, sp, h2, now_ts=95.0)
        check("cycle 5: state updated with new hash", state.get(SNAPSHOT_HASH_KEY) == h2)
        check("cycle 5: state updated with new push timestamp", state.get(LAST_PUSH_TS_KEY) == 95.0)

    # -- #1656: hot-set capping (split_hot_and_archive) --
    def _room(path, state, ts):
        return {"path": path, "state": state, "steps": [{"id": "s1", "state": state, "timestamp": ts}]}

    running_room = _room("/r/running", "Running", "2026-09-01T00:00:00Z")
    terminal_rooms = [_room(f"/r/term-{i}", "Succeeded" if i % 2 else "Failed",
                             f"2026-09-01T00:{i:02d}:00Z") for i in range(50)]
    mixed = [running_room, *terminal_rooms]
    hot, archive, total = split_hot_and_archive(mixed)
    check("hot set keeps every non-terminal room", running_room in hot)
    check("hot set caps terminal rooms at HOT_TERMINAL_CAP", sum(1 for r in hot if r is not running_room) == HOT_TERMINAL_CAP)
    check("terminal_total counts every terminal room, not just the hot slice", total == 50)
    check("archive carries the FULL terminal population, not just the tail beyond the cap", len(archive) == 50)
    check("archive is sorted newest-first (same measure as drop_stale_rooms' newest_timestamp)",
          archive[0]["path"] == "/r/term-49" and archive[-1]["path"] == "/r/term-0")
    check("the hot set's terminal slice is the SAME newest rooms archive page 0 would return",
          {r["path"] for r in hot if r is not running_room} == {r["path"] for r in archive[:HOT_TERMINAL_CAP]})

    few_terminal = [running_room, terminal_rooms[0], terminal_rooms[1]]
    hot2, archive2, total2 = split_hot_and_archive(few_terminal)
    check("a fleet with fewer terminal rooms than the cap keeps all of them hot",
          len(hot2) == 3 and total2 == 2)

    malformed = [running_room, {"path": "/r/no-state"}, "not-a-dict"]
    hot3, archive3, total3 = split_hot_and_archive(malformed)
    check("a room missing 'state' degrades to non-terminal (kept, never silently dropped)",
          any(r.get("path") == "/r/no-state" for r in hot3 if isinstance(r, dict)))
    check("a non-dict list entry degrades to non-terminal too, never raises", "not-a-dict" in hot3)

    empty_hot, empty_archive, empty_total = split_hot_and_archive([])
    check("an empty room list yields an empty hot set, empty archive, zero total",
          empty_hot == [] and empty_archive == [] and empty_total == 0)

    wrapped_with_archive = build_wrapped(hot, [], {}, 0, terminal_total=total, terminal_archive=archive)
    check("build_wrapped carries terminal_total/terminal_archive through to the pushed body",
          wrapped_with_archive["terminal_total"] == 50 and len(wrapped_with_archive["terminal_archive"]) == 50)
    check("build_wrapped defaults terminal_total/terminal_archive for callers that don't pass them "
          "(every pre-#1656 call site keeps working unchanged)",
          build_wrapped([], [], {}, 0) == {"rooms": [], "underhood": [], "timelines": {},
                                            "stale_hidden_count": 0, "terminal_total": 0,
                                            "terminal_archive": []})

    # #1656 F2 (2026-09-02 review): worker_displayed_heartbeat_at, the hand-copied Python mirror of
    # worker.js's maxIsoOrNull heartbeat merge, is deleted -- the real function now has executable
    # coverage in tools/fleet-glass/worker.selftest.mjs (`node tools/fleet-glass/worker.selftest.mjs`
    # / `pixi run fleet-glass-worker-selftest`), which discriminates against the actual worker.core.mjs
    # code path instead of a copy that could drift from it silently.

    # -- #1669: Conductor room deliverables and upsert identity --
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)
        c_src = Path(td) / "original-notes.md"
        c_src.write_bytes(b"# Plan Title\nSome content here")

        dest_file = c_art / "original-notes.md"
        dest_file.write_bytes(b"# Plan Title\nSome content here")

        manifest_file = c_art / "manifest.jsonl"
        manifest_entry = {
            "title": "Plan Title",
            "source_path": str(c_src),
            "delivered_at": "2026-09-02T12:00:00Z",
            "sha256": sha256_hex(b"# Plan Title\nSome content here"),
            "artifact_file": "original-notes.md",
        }
        manifest_file.write_text(json.dumps(manifest_entry) + "\n", encoding="utf-8")

        # 1. Gather fresh conductor deliverable
        c_items = gather_deliverables(c_root, {}, [])
        check("gather_deliverables gathers conductor deliverable from manifest", len(c_items) == 1)
        if c_items:
            c_item = c_items[0]
            check("conductor deliverable has kind='conductor'", c_item.get("kind") == "conductor")
            check("conductor deliverable id is derived from source_path",
                  c_item.get("id") == f"{str(c_room)}::conductor::{str(c_src)}")
            check("conductor deliverable carries title", c_item.get("title") == "Plan Title")
            check("conductor deliverable carries content", c_item.get("content") == "# Plan Title\nSome content here")
            check("conductor deliverable is not withheld without secret match", c_item.get("withheld") is False)

        # 2. Dedupe against push state
        c_state = mark_pushed({}, c_items)
        c_items_deduped = gather_deliverables(c_root, c_state, [])
        check("gather_deliverables skips already-pushed conductor deliverable with unchanged content",
              len(c_items_deduped) == 0)

        # 3. Re-delivery with updated content (upsert)
        dest_file.write_bytes(b"# Plan Title\nUpdated content")
        manifest_entry2 = {
            "title": "Plan Title v2",
            "source_path": str(c_src),
            "delivered_at": "2026-09-02T12:30:00Z",
            "sha256": sha256_hex(b"# Plan Title\nUpdated content"),
            "artifact_file": "original-notes.md",
        }
        manifest_file.write_text(json.dumps(manifest_entry2) + "\n", encoding="utf-8")

        c_items_updated = gather_deliverables(c_root, c_state, [])
        check("gather_deliverables picks up updated conductor deliverable", len(c_items_updated) == 1)
        if c_items_updated:
            c_up = c_items_updated[0]
            check("re-delivered item has identical id for upsert",
                  c_up.get("id") == f"{str(c_room)}::conductor::{str(c_src)}")
            check("re-delivered item has updated content hash",
                  c_up.get("content_hash") == sha256_hex(b"# Plan Title\nUpdated content"))

        # 4. Secret gate withholding on conductor deliverable
        secret_pats = [re.compile(r"sk-[A-Za-z0-9]{10,}")]
        dest_file.write_text("# Leaked\nsk-secretkey123456789", encoding="utf-8")
        c_items_leaked = gather_deliverables(c_root, {}, secret_pats)
        check("conductor deliverable with secret is withheld",
              len(c_items_leaked) == 1 and c_items_leaked[0].get("withheld") is True)

    # F1 (2026-09-02 review): two sources sharing a basename must not collide on one on-disk file --
    # artifact_file is read from the manifest line, never re-derived from the basename, so two
    # distinct hashed filenames stay two distinct files with two distinct byte payloads.
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)

        src_a = Path(td) / "projA" / "notes.md"
        src_a.parent.mkdir(parents=True)
        src_a.write_bytes(b"# A\nProject A content")

        src_b = Path(td) / "projB" / "notes.md"
        src_b.parent.mkdir(parents=True)
        src_b.write_bytes(b"# B\nProject B content")

        dest_a = c_art / "aaaaaaaa-notes.md"
        dest_a.write_bytes(b"# A\nProject A content")
        dest_b = c_art / "bbbbbbbb-notes.md"
        dest_b.write_bytes(b"# B\nProject B content")

        manifest_file = c_art / "manifest.jsonl"
        entries = [
            {
                "title": "A",
                "source_path": str(src_a),
                "delivered_at": "2026-09-02T12:00:00Z",
                "sha256": sha256_hex(b"# A\nProject A content"),
                "artifact_file": "aaaaaaaa-notes.md",
            },
            {
                "title": "B",
                "source_path": str(src_b),
                "delivered_at": "2026-09-02T12:00:01Z",
                "sha256": sha256_hex(b"# B\nProject B content"),
                "artifact_file": "bbbbbbbb-notes.md",
            },
        ]
        manifest_file.write_text("\n".join(json.dumps(e) for e in entries) + "\n", encoding="utf-8")

        same_basename_items = gather_deliverables(c_root, {}, [])
        check("same-basename sources produce two distinct conductor deliverables",
              len(same_basename_items) == 2)
        by_source = {i.get("source_path"): i for i in same_basename_items}
        check("same-basename source A keeps its own bytes",
              by_source.get(str(src_a), {}).get("content") == "# A\nProject A content")
        check("same-basename source B keeps its own bytes",
              by_source.get(str(src_b), {}).get("content") == "# B\nProject B content")
        check("same-basename sources use distinct artifact paths",
              by_source.get(str(src_a), {}).get("artifact") != by_source.get(str(src_b), {}).get("artifact"))

    # -- #1673: Conductor manifest UTF-8 BOM tolerance, corrupt-line logging, and cross-language fixture --
    with tempfile.TemporaryDirectory() as td:
        c_root = Path(td)
        c_room = c_root / "conductor"
        c_art = c_room / "artifacts" / "conductor"
        c_art.mkdir(parents=True)

        c_src_bom = Path(td) / "bom-notes.md"
        c_src_bom.write_bytes(b"# BOM Title\nContent with BOM")
        dest_bom = c_art / "11111111-bom-notes.md"
        dest_bom.write_bytes(b"# BOM Title\nContent with BOM")

        manifest_file = c_art / "manifest.jsonl"
        manifest_entry_bom = {
            "title": "BOM Title",
            "source_path": str(c_src_bom),
            "delivered_at": "2026-09-02T12:00:00Z",
            "sha256": sha256_hex(b"# BOM Title\nContent with BOM"),
            "artifact_file": "11111111-bom-notes.md",
        }
        # (a) A manifest whose first line carries a UTF-8 BOM parses and yields the item
        bom_bytes = b"\xef\xbb\xbf" + json.dumps(manifest_entry_bom).encode("utf-8") + b"\n"
        manifest_file.write_bytes(bom_bytes)

        bom_items = gather_deliverables(c_root, {}, [])
        check("conductor manifest carrying UTF-8 BOM parses and yields deliverable (#1673 arm a)",
              len(bom_items) == 1 and bom_items[0].get("title") == "BOM Title")

        # (b) A garbage line is logged and skipped while good lines still yield
        c_src_good = Path(td) / "good-notes.md"
        c_src_good.write_bytes(b"# Good Title\nGood content")
        dest_good = c_art / "22222222-good-notes.md"
        dest_good.write_bytes(b"# Good Title\nGood content")

        manifest_entry_good = {
            "title": "Good Title",
            "source_path": str(c_src_good),
            "delivered_at": "2026-09-02T12:01:00Z",
            "sha256": sha256_hex(b"# Good Title\nGood content"),
            "artifact_file": "22222222-good-notes.md",
        }
        garbage_manifest = (
            "corrupt garbage line that is not json\n"
            + json.dumps(manifest_entry_bom) + "\n"
            + "another { malformed json line\n"
            + json.dumps(manifest_entry_good) + "\n"
        )
        manifest_file.write_text(garbage_manifest, encoding="utf-8")
        garbage_items = gather_deliverables(c_root, {}, [])
        check("garbage manifest line is skipped while good lines yield deliverables (#1673 arm b)",
              len(garbage_items) == 2 and {i.get("title") for i in garbage_items} == {"BOM Title", "Good Title"})

        # (3) Cross-language pin: parse checked-in fixture tests/fixtures/conductor-manifest.jsonl produced by C# path
        fixture_path = HERE.parent.parent / "tests" / "fixtures" / "conductor-manifest.jsonl"
        check("cross-language conductor manifest fixture file exists (#1673)", fixture_path.is_file())
        fixture_raw = fixture_path.read_bytes()
        check("cross-language fixture has no UTF-8 BOM and starts with '{'",
              len(fixture_raw) > 0 and fixture_raw[0] == 0x7B and not fixture_raw.startswith(b"\xef\xbb\xbf"))

        manifest_file.write_bytes(fixture_raw)
        fixture_artifact = c_art / "c44a8b84-fixture-plan.md"
        fixture_artifact.write_bytes(b"# Fixture Plan\nFixture content")

        fixture_items = gather_deliverables(c_root, {}, [])
        check("pusher selftest parses cross-language conductor manifest fixture (#1673)",
              len(fixture_items) == 1 and fixture_items[0].get("title") == "Fixture Plan"
              and fixture_items[0].get("kind") == "conductor")

    # -- #1656 F3 (2026-09-02 review): nonterminal_warn_line threshold behavior, restored alongside
    # the #1669 conductor block above rather than being displaced by it (F2, 2026-09-02 review) --
    check("non_terminal_count at the threshold does not warn", nonterminal_warn_line(HOT_NONTERMINAL_WARN) is None)
    check("non_terminal_count one over the threshold warns, naming the threshold",
          nonterminal_warn_line(HOT_NONTERMINAL_WARN + 1) is not None
          and "HOT_NONTERMINAL_WARN" in nonterminal_warn_line(HOT_NONTERMINAL_WARN + 1))

    conductor_obj = {"path": "/r/conductor", "artifacts_path": "/r/conductor/artifacts/conductor"}
    wrapped_with_conductor = build_wrapped([], [], {}, 0, conductor=conductor_obj)
    check("build_wrapped carries conductor object through to the snapshot",
          wrapped_with_conductor.get("conductor") == conductor_obj)

    if failures:
        print(f"pusher.py selftest: FAIL -- {len(failures)} check(s):", file=sys.stderr)
        for f in failures:
            print(f"  !! {f}", file=sys.stderr)
        return 1
    print("pusher.py selftest: pass")
    return 0


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(_selftest())
    main()
