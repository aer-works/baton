"""Fleet Glass pusher: derive the fleet snapshot via Baton.Mcp.Host (stdio MCP) and scan ~/.baton/rooms
for terminal-room deliverables, then POST both outbound to the Cloudflare mailbox Worker (worker.js)
every ~25s. Moved into the repo, with the deliverables inbox added, by aer-works/baton#1413.

Outbound-only; the machine running this accepts no inbound connections.

THE SNAPSHOT HALF -- change-gated (#1457)
-------------------------------------------
The wrapped {rooms, underhood} body is hashed (stable, sort_keys) before every POST; a hash that
matches the last SUCCESSFUL push's (persisted in push_state_file, key SNAPSHOT_HASH_KEY) skips the
POST. Cloudflare's KV free tier caps at 1,000 writes/day and worker.js's /push handler is an
unconditional env.FLEET.put per POST -- pushing an unchanged snapshot every interval_seconds (default
25s) burns 3,456 writes/day against that cap for nothing. A missing/unreadable persisted hash always
re-pushes (fail toward one extra write, never toward silence, same posture as the deliverables state
file below); a FAILED POST never persists the hash, so the next cycle retries. See `snapshot_hash` /
`should_push_snapshot`.

Config comes from pusher.config.json next to this script (gitignored, machine-local -- ship
pusher.config.example.json and copy it):
    {
      "dll": "<path to Baton.Mcp.Host.dll>",
      "push_url": "https://.../push/<PUSH_TOKEN>",
      "deliver_url": "https://.../deliver/<PUSH_TOKEN>",   # optional; derived from push_url if absent
      "interval_seconds": 25,
      "roots": [],
      "max_age_days": 3,
      "rooms_root": "~/.baton/rooms",                         # optional; defaults there
      "secret_patterns_file": "secretpatterns.local.txt",    # optional; defaults next to this script
      "push_state_file": "push-state.local.json",            # optional; defaults next to this script
      "underhood_dirs": [], "underhood_logs": []
    }
push_url (and deliver_url, if set) embed the push token -- the config file is a local secret; never
print or commit it.

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

import hashlib
import json
import re
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


def log(msg: str) -> None:
    try:
        if LOG.exists() and LOG.stat().st_size > 1_000_000:
            LOG.write_text("", encoding="utf-8")
        with LOG.open("a", encoding="utf-8") as f:
            f.write(f"{datetime.now(timezone.utc).isoformat()} {msg}\n")
    except OSError:
        pass


# ---------------------------------------------------------------------------------------------
# Fleet snapshot (unchanged pipeline: derive via Baton.Mcp.Host, drop stale rooms, gather underhood)
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


def derive_snapshot(dll: str, roots: list) -> str:
    """Returns the rooms JSON exactly as fleet_status produced it (content[0].text)."""
    proc = subprocess.Popen(
        ["dotnet", dll, "--fleet-status-tool"],
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
    finally:
        proc.terminate()
    result = resp.get("result")
    if result is None:
        raise RuntimeError(f"tools/call error: {resp.get('error')}")
    text = result["content"][0]["text"]
    json.loads(text)  # validate before pushing; raises on garbage
    return text


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


def drop_stale_rooms(body: str, max_age_days: float) -> str:
    """Filter rooms whose newest timestamp is older than the cutoff -- zombie RUNNING rooms
    included (a room that died without terminal.json shows Running forever; age is the only
    honest signal). Rooms with no parseable timestamp are KEPT: unreadable is a finding the
    glass should show, not silently drop."""
    data = json.loads(body)
    # fleet_status emits a bare room list; tolerate a {rooms: [...]} wrapper too.
    bare = isinstance(data, list)
    rooms = data if bare else data.get("rooms")
    if not isinstance(rooms, list):
        return body
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
    if len(kept) != len(rooms):
        log(f"filtered {len(rooms) - len(kept)} stale room(s) older than {max_age_days}d")
    if bare:
        return json.dumps(kept)
    data["rooms"] = kept
    return json.dumps(data)


def _git(cwd: str, *args: str) -> str:
    try:
        out = subprocess.run(
            ["git", *args], cwd=cwd, capture_output=True, text=True, timeout=15, check=False,
        )
        return out.stdout.strip()
    except (OSError, subprocess.TimeoutExpired):
        return ""


def gather_underhood(cfg: dict) -> list:
    """Worktree telemetry for active lanes: branch, diff shape, newest commit, activity line.

    CONTENT-FREE BY DESIGN except the log tail: branch names, file counts, and +/- totals only --
    no diff hunks, so nothing here can leak a secret VALUE. The optional activity line is the
    last line of a lane's echo-worker log (worker narration), capped at 160 chars; drop the
    'underhood_logs' config key to turn that part off."""
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
    for pattern in cfg.get("underhood_logs", []):
        for f in sorted(globmod.glob(pattern)):
            try:
                lines = [ln.strip() for ln in Path(f).read_text(encoding="utf-8", errors="replace").splitlines() if ln.strip()]
            except OSError:
                continue
            if lines:
                name = Path(f).stem
                for e in entries:
                    if name.endswith(e["name"].lstrip("w")) or e["name"].lstrip("w") in name:
                        e["activity"] = lines[-1][:160]
                        break
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


def snapshot_hash(wrapped: dict) -> str:
    """Stable hash of the wrapped {rooms, underhood} body -- sort_keys so the hash does not depend
    on dict insertion order upstream, independent of the (unsorted) exact string actually POSTed."""
    return sha256_hex(json.dumps(wrapped, sort_keys=True).encode("utf-8"))


def should_push_snapshot(state: dict, current_hash: str) -> bool:
    """True unless `current_hash` matches the last SUCCESSFUL push's hash persisted under
    SNAPSHOT_HASH_KEY. A missing/unreadable persisted value (state.get returns None) always
    pushes -- fail toward one extra write, never toward silence."""
    return state.get(SNAPSHOT_HASH_KEY) != current_hash


def push_snapshot_and_record(post, body: str, state: dict, state_path, current_hash: str) -> None:
    """POST first, record the hash ONLY afterwards. This ordering is the change-gate's single most
    safety-critical property (a hash persisted for a FAILED push would gate every retry and go
    silent until the next content change), so it lives in one testable function instead of inline
    in main()'s loop -- the selftest proves a raising `post` leaves the state file untouched."""
    post(body)
    state[SNAPSHOT_HASH_KEY] = current_hash
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
    if stub_reason:
        item["stub_reason"] = stub_reason
    if pattern_index is not None:
        log(f"secret-gate: {room}/{rel} matched pattern #{pattern_index}: withheld")
    return item


def build_verdict_only_item(room: str, verdict: dict) -> dict:
    """A room with zero declared outputs (typically Failed) still gets one inbox entry, so a
    failure with nothing to show is still visible rather than silently absent."""
    text = json.dumps(verdict, indent=2, sort_keys=True)
    content_hash = sha256_hex(text.encode("utf-8"))
    return {
        "id": f"{room}::__verdict__::{content_hash[:16]}",
        "room": room,
        "artifact": None,
        "title": f"{room} — {verdict.get('state') or 'unknown'}",
        "content_hash": content_hash,
        "withheld": False,
        "verdict": verdict,
        "content": text,
    }


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
            item = build_verdict_only_item(room, verdict)
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
    rooms_root = Path(cfg["rooms_root"]).expanduser() if cfg.get("rooms_root") else DEFAULT_ROOMS_ROOT
    patterns_path = Path(cfg["secret_patterns_file"]).expanduser() if cfg.get("secret_patterns_file") else DEFAULT_SECRET_PATTERNS_FILE
    state_path = Path(cfg["push_state_file"]).expanduser() if cfg.get("push_state_file") else DEFAULT_PUSH_STATE_FILE
    deliver_url = derive_deliver_url(cfg)
    skip_log_every = max(1, round(600 / interval)) if interval > 0 else 1
    skip_streak = 0

    while True:
        try:
            body = derive_snapshot(cfg["dll"], cfg.get("roots", []))
            body = drop_stale_rooms(body, cfg.get("max_age_days", 3))
            rooms = json.loads(body)
            wrapped = {"rooms": rooms if isinstance(rooms, list) else rooms.get("rooms"),
                       "underhood": gather_underhood(cfg)}
            current_hash = snapshot_hash(wrapped)
            snap_state = load_push_state(state_path)
            if should_push_snapshot(snap_state, current_hash):
                push_snapshot_and_record(
                    lambda b: post_json(cfg["push_url"], b),
                    json.dumps(wrapped), snap_state, state_path, current_hash)
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
