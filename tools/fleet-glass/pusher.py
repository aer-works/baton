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

THE TIMELINE HALF (#1505)
-------------------------------------------
Pre-#42 (the daemon has not yet been given the projection job, spec/baton.md §7), this pusher gets
per-room timelines the same way it gets the fleet snapshot: one `room_detail` call per NON-TERMINAL
room, through the SAME dotnet-mcp process `derive_snapshot_and_timelines` already spawns for
`fleet_status` -- never a second `dotnet` spawn per room. `extract_timeline` keeps ONLY each entry's
`type` and `timestamp`; `room_detail`'s `stdout` field and any `note`/`detail`/`error` text are
dropped unconditionally, by construction (the function reads exactly two named keys off each entry
and nothing else), so stdout can never ride the mailbox through this path -- see the module's secret
gate above for why that boundary exists at all. Capped at the last TIMELINE_CAP (30) entries per
room: a lane's timeline is step-level transitions (dispatch, execution start/exit, retries, decisions)
written a handful of times per step, not a line per stdout write -- a lane produces tens of these
over its life, not thousands, so this rides the mailbox safely under the same 1,000-write/day KV
budget the change-gate above protects, and 30 is generous headroom over what a normal lane emits
before terminating. Keyed by room PATH, never room NAME (#1505 review note: fleet_status dedupes
rooms by path, so two same-named rooms under different roots are distinct entries; a name-keyed join
would hand one room's timeline to the other -- exactly the wrong-and-confident failure mode #41's
removal below exists to stop, reintroduced by a careless join).

THE HEARTBEAT HALF (#1486)
-------------------------------------------
The change-gate above makes pushed_at legitimately stale on a quiet fleet, and nothing distinguishes
that from a dead pusher. Independent of the gated snapshot, this loop also POSTs a bare timestamp
ping to worker.js's /heartbeat route at a coarse fixed cadence -- hourly, tracked in push_state_file
under HEARTBEAT_STATE_KEY. Arithmetic: 24 writes/day at hourly cadence, against the same 1,000/day KV
free-tier cap the change-gate protects; combined with the change-gated snapshot writes (worst case
one per interval_seconds when the fleet is constantly changing) this adds a small, fixed floor that
never scales with polling frequency. Same save-only-after-success discipline as
push_snapshot_and_record: POST first, record the timestamp only afterwards, so a failed heartbeat
retries next cycle instead of silently going stale. Heartbeat failures are logged and never raise
into the snapshot path -- see main()'s heartbeat try/except, which runs in its own block after the
snapshot has already been sent. A heartbeat body carries nothing at all -- a literal "{}", not even
a timestamp; the Worker stamps its own receipt time server-side (see worker.js's /heartbeat
handler) -- so it is not a deliverable and does not pass through the secret gate below; there is
nothing in it that gate exists to catch.

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

Dedupe is per (room, artifact, content-hash) -- `push_state_file` (gitignored) remembers the hash
last pushed for each (room, artifact) pair, and a run that finds an unchanged hash skips re-pushing
it. A room with zero declared outputs (typically a Failed room) still gets ONE deliverable, carrying
only the verdict summary, so a failure with nothing to show is still visible in the inbox.

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
    uses (spec/baton.md §6). Non-terminal is exactly the population room_detail timelines are
    fetched for: a terminal room's flow.jsonl is already frozen and its verdict is carried in the
    deliverables half below, so re-fetching its timeline every cycle would be pure waste."""
    try:
        return (Path(room_path) / "terminal.json").is_file()
    except (OSError, TypeError):
        return False


def extract_timeline(room_detail_result: dict) -> list[dict]:
    """Content-free timeline projection from one room_detail response: KEEP ONLY `type` and
    `timestamp` off each timeline entry. Does not enumerate fields to DROP (stdout, note, error,
    detail) -- it enumerates the two fields it KEEPS, so a future room_detail field never leaks
    through by accident of this function failing to name it. `stdout` is never read at all, whether
    or not room_detail's response carries one.

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
        out.append(kept)
    return out[-TIMELINE_CAP:]


def derive_snapshot_and_timelines(dll: str, roots: list) -> tuple[str, dict]:
    """Returns (the rooms JSON exactly as fleet_status produced it, {room_path: [timeline entries]}
    for every NON-TERMINAL room) -- ONE dotnet-mcp process for both, reused across every room_detail
    call in this cycle (module docstring's "THE TIMELINE HALF"): spawning a fresh `dotnet` per room
    would multiply the exact per-cycle subprocess cost the daemon-owns-the-projection design (#1502
    menu #42) exists to kill."""
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
        for room in room_list:
            if not isinstance(room, dict):
                continue
            room_path = room.get("path")
            if not isinstance(room_path, str) or not room_path:
                continue
            if is_terminal_room(room_path):
                continue
            try:
                detail_resp = rpc(proc, next_id, "tools/call", {
                    "name": "room_detail",
                    "arguments": {"room": room_path},
                })
                next_id += 1
                detail_result = detail_resp.get("result")
                if detail_result is None:
                    log(f"room_detail error for {room_path}: {detail_resp.get('error')}")
                    continue
                detail = json.loads(detail_result["content"][0]["text"])
                entries = extract_timeline(detail)
                if entries:
                    timelines[room_path] = entries
            except Exception as ex:  # noqa: BLE001 — one room's timeline must not sink the cycle
                log(f"room_detail failed for {room_path}: {type(ex).__name__}: {ex}")
    finally:
        proc.terminate()
    return text, timelines


def newest_timestamp(node) -> str:
    """Max ISO-8601-looking string anywhere in the room object -- shape-agnostic on purpose,
    so a fleet_status field rename degrades to 'room has no timestamp' (kept), never a crash."""
    best = ""
    if isinstance(node, dict):
        for v in node.values():
            best = max(best, newest_timestamp(v))
    elif isinstance(node, list):
        for v in node:
            best = max(best, newest_timestamp(v))
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


def build_wrapped(room_list, underhood, timelines, stale_hidden_count) -> dict:
    """The exact snapshot body main() pushes. One home so the leak selftest exercises the real push
    path's construction, not a hand-rebuilt copy that could drift from it (PR #1508 review)."""
    return {"rooms": room_list,
            "underhood": underhood,
            "timelines": timelines,
            "stale_hidden_count": stale_hidden_count}


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


def send_heartbeat_and_record(post, state: dict, state_path, now_ts: float) -> None:
    """POST first, record only afterwards -- same ordering discipline as push_snapshot_and_record
    (a raising `post` must leave `state` untouched, so a failed heartbeat retries next cycle
    instead of going silent)."""
    post()
    state[HEARTBEAT_STATE_KEY] = now_ts
    save_push_state(state_path, state)


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


def load_push_state(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_push_state(path: Path, state: dict) -> None:
    path.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")


def find_terminal_rooms(rooms_root: Path) -> list[tuple[str, Path]]:
    """(room_name, room_dir) for every room directory that carries a terminal.json.

    A room with no terminal.json is still running (or was never dispatched) -- outside this
    function's job, which is only to find TERMINAL rooms; the fleet snapshot half already covers
    in-flight state.
    """
    if not rooms_root.is_dir():
        return []
    found = []
    for child in sorted(rooms_root.iterdir()):
        if child.is_dir() and (child / "terminal.json").is_file():
            found.append((child.name, child))
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


def build_item(room: str, room_dir: Path, artifact_path: Path, verdict: dict,
                patterns: list[re.Pattern] | None) -> dict:
    """One deliverable for a declared output artifact. `artifact_path` is absolute (terminal.json
    stores absolute paths); the item's "artifact" field is that path relative to the room dir, so
    dedupe keys and inbox rows never carry the operator's home directory."""
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
        "id": f"{room}::{rel}::{content_hash[:16]}",
        "room": room,
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
        log(f"secret-gate: {room}/{rel} matched pattern #{pattern_index}: withheld")
    return item


def build_verdict_only_item(room: str, verdict: dict, room_dir: Path | None = None) -> dict:
    """A room with zero declared outputs (typically Failed) still gets one inbox entry, so a
    failure with nothing to show is still visible rather than silently absent."""
    text = json.dumps(verdict, indent=2, sort_keys=True)
    content_hash = sha256_hex(text.encode("utf-8"))
    item = {
        "id": f"{room}::__verdict__::{content_hash[:16]}",
        "room": room,
        "artifact": None,
        "title": f"{room} — {verdict.get('state') or 'unknown'}",
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


def gather_deliverables(rooms_root: Path, state: dict, patterns: list[re.Pattern] | None) -> list[dict]:
    """Every not-yet-pushed deliverable across all terminal rooms under rooms_root.

    "not yet pushed" is decided per (room, artifact) against `state[key] == content_hash` -- an
    unchanged hash is skipped. Deliberately NOT memorized into `state` here (the caller does that,
    only after a successful network push): when `patterns is None`, every item this run is withheld
    for that reason alone, and it must be re-offered on the NEXT run too, in case an operator has
    fixed the patterns file by then -- see `load_secret_patterns`.
    """
    if patterns is None:
        log("secret-gate: secret_patterns_file missing/unreadable — WITHHOLDING EVERYTHING this run (fail closed)")

    items = []
    for room, room_dir in find_terminal_rooms(rooms_root):
        terminal = load_terminal(room_dir)
        if terminal is None:
            continue
        verdict = verdict_summary(terminal)
        outputs = declared_outputs(terminal)
        if not outputs:
            item = build_verdict_only_item(room, verdict, room_dir)
            key = f"{room}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                items.append(item)
            continue
        for artifact_path in outputs:
            item = build_item(room, room_dir, artifact_path, verdict, patterns)
            key = f"{room}::{item['artifact']}"
            if state.get(key) != item["content_hash"]:
                items.append(item)
    return items


def mark_pushed(state: dict, items: list[dict]) -> dict:
    """New state dict with each item's (room, artifact) -> content_hash recorded. Pure, so callers
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

    acquire_lock(lock_path)
    atexit.register(release_lock, lock_path)

    try:
        while True:
            try:
                body, timelines = derive_snapshot_and_timelines(cfg["dll"], cfg.get("roots", []))
                body, stale_hidden_count = drop_stale_rooms(body, cfg.get("max_age_days", 3))
                rooms = json.loads(body)
                room_list = rooms if isinstance(rooms, list) else rooms.get("rooms")
                # Timelines were fetched pre-stale-filter, keyed by path; only carry forward the ones
                # for rooms that survived drop_stale_rooms above, so a hidden room's timeline is hidden
                # with it rather than riding along as orphaned payload.
                surviving_paths = {r.get("path") for r in (room_list or []) if isinstance(r, dict)}
                wrapped = build_wrapped(
                    room_list,
                    gather_underhood(cfg),
                    {p: t for p, t in timelines.items() if p in surviving_paths},
                    stale_hidden_count)
                current_hash = snapshot_hash(wrapped)
                snap_state = load_push_state(state_path)
                if should_push_snapshot(snap_state, current_hash):
                    now_ts = time.time()
                    if should_coalesce_push(snap_state, now_ts, min_push_interval_s):
                        last_ts = snap_state[LAST_PUSH_TS_KEY]
                        elapsed = int(now_ts - last_ts)
                        log(f"coalesced ({elapsed}s since last push)")
                    else:
                        push_snapshot_and_record(
                            lambda b: post_json(cfg["push_url"], b),
                            json.dumps(wrapped), snap_state, state_path, current_hash, now_ts=now_ts)
                        if skip_streak:
                            log(f"skipped {skip_streak} unchanged cycle(s) since last push")
                            skip_streak = 0
                        log(f"pushed {len(body)} bytes")
                else:
                    skip_streak += 1
                    if should_log_skip(skip_streak, skip_log_every):
                        log(f"unchanged, skipped ({skip_streak} in a row)")
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (snapshot) {type(ex).__name__}: {ex}")

            # Own try/except, runs AFTER the snapshot has already been sent above -- a slow or failing
            # heartbeat POST must never block or delay the snapshot path (#1486).
            try:
                if heartbeat_url is None:
                    pass  # no heartbeat_url configured and none derivable from push_url — skip quietly
                else:
                    hb_state = load_push_state(state_path)
                    now_ts = time.time()
                    if should_send_heartbeat(hb_state, now_ts):
                        send_heartbeat_and_record(
                            lambda: post_json(heartbeat_url, "{}"),
                            hb_state, state_path, now_ts)
                        log("heartbeat sent")
            except Exception as ex:  # noqa: BLE001 — loop must survive anything
                log(f"ERROR (heartbeat) {type(ex).__name__}: {ex}")

            try:
                if deliver_url is None:
                    log("deliver: no deliver_url (set one, or a push_url containing /push/) — skipped")
                else:
                    state = load_push_state(state_path)
                    patterns = load_secret_patterns(patterns_path)
                    items = gather_deliverables(rooms_root, state, patterns)
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
        by_room = {i["room"]: i for i in items if i["artifact"]}
        check("fail-closed: room-a's real report is withheld when patterns are missing",
              by_room["room-a"]["withheld"] is True
              and "patterns file missing" in by_room["room-a"]["stub_reason"]
              and "Report A" not in by_room["room-a"]["content"])
        check("fail-closed: prompt.txt/.stdout.log never enter the item stream",
              all("prompt" not in (i.get("artifact") or "") and "stdout" not in (i.get("artifact") or "")
                  for i in items))
        check("a room with zero declared outputs still yields one verdict-only item",
              any(i["room"] == "room-b" and i["artifact"] is None and i["verdict"]["error"] == "boom"
                  for i in items))

        # -- patterns present, no hit: real content passes through --
        clean_patterns_file = tmp / "clean.txt"
        clean_patterns_file.write_text("# comment only, no real patterns\n", encoding="utf-8")
        clean_patterns = load_secret_patterns(clean_patterns_file)
        check("an empty-but-present patterns file parses to [] (not the fail-closed sentinel)",
              clean_patterns == [])
        items2 = gather_deliverables(rooms_root, {}, clean_patterns)
        report = next(i for i in items2 if i["room"] == "room-a" and i["artifact"])
        check("clean content is uploaded verbatim when nothing matches",
              report["withheld"] is False and "Report A" in report["content"])
        check("title comes from the first markdown heading", report["title"] == "Report A")
        check("deliverable carries ISO-8601 created_at from artifact mtime",
              isinstance(report.get("created_at"), str) and "T" in report["created_at"])
        verdict_only = next(i for i in items2 if i["room"] == "room-b")
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
        secret_item = next(i for i in items3 if i["room"] == "room-c")
        check("a pattern hit withholds the content", secret_item["withheld"] is True)
        check("the stub names the matched pattern's INDEX, not its text",
              secret_item["stub_reason"] == "matched pattern #0" and "sk-" not in secret_item["content"])

        # -- dedupe: an unchanged (room, artifact, hash) is not re-offered --
        state_after = mark_pushed({}, items2)
        items4 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe skips an already-pushed, unchanged artifact",
              not any(i["room"] == "room-a" and i["artifact"] == "artifacts/execution_x/report.md"
                      for i in items4))

        # -- polarity: changed content is offered again despite matching state key --
        (rooms_root / "room-a" / "artifacts" / "execution_x" / "report.md").write_text(
            "# Report A v2\n\nchanged\n", encoding="utf-8")
        items5 = gather_deliverables(rooms_root, state_after, clean_patterns)
        check("dedupe re-offers an artifact whose content changed",
              any(i["room"] == "room-a" and i["title"] == "Report A v2" for i in items5))

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
