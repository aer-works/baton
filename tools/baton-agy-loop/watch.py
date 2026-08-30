"""One terminal, the whole orchestration -- the operator's stopgap until the daemon/UI own this.

Refreshes a snapshot every few seconds: active dispatch runs (last recorded event and its age),
lane worktrees (branch, tip, dirty state), and open PRs with check status. Read-only everywhere:
never takes a lock, never writes into a run, and the gh section refreshes on a slower cadence so
watching does not spend API budget. `baton status <room-dir> --follow` (#730) is the per-run
deep-dive; this is the fleet view above it.

Usage:
    python tools/baton-agy-loop/watch.py [--interval 5] [--runs 8] [--no-pr]
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def last_event(log_path: Path) -> tuple[str, float] | None:
    try:
        raw = log_path.read_bytes()
    except OSError:
        return None
    lines = [line for line in raw.split(b"\n") if line.strip()]
    if not lines:
        return None
    try:
        event = json.loads(lines[-1])
        kind = event.get("Event", {}).get("eventType", "?")
    except (ValueError, AttributeError):
        kind = "(unparseable)"
    return kind, log_path.stat().st_mtime


def age(ts: float) -> str:
    seconds = int(time.time() - ts)
    if seconds < 90:
        return f"{seconds}s"
    if seconds < 5400:
        return f"{seconds // 60}m"
    return f"{seconds // 3600}h{(seconds % 3600) // 60:02d}m"


def runs_section(limit: int) -> list[str]:
    runs_root = REPO / "baton-agy-loop-scratch" / "runs"
    if not runs_root.is_dir():
        return ["  (no runs directory)"]
    candidates = sorted(
        (entry for entry in runs_root.iterdir() if entry.is_dir()),
        key=lambda entry: entry.stat().st_mtime,
        reverse=True)[:limit]
    lines = []
    for run in candidates:
        info = last_event(run / "room-dir" / "flow.jsonl")
        if info is None:
            lines.append(f"  {run.name}  (no flow.jsonl yet)")
            continue
        kind, mtime = info
        terminal = kind in ("executionSucceeded", "executionFailed", "executionCancelled")
        marker = " " if terminal else "*"
        lines.append(f" {marker}{run.name}  {kind:<28} {age(mtime):>6} ago")
    return lines or ["  (none)"]


def worktrees_section() -> list[str]:
    result = subprocess.run(
        ["git", "-C", str(REPO), "worktree", "list", "--porcelain"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
    if result.returncode != 0:
        return ["  (git worktree list failed)"]
    lines, path = [], None
    for row in result.stdout.splitlines():
        if row.startswith("worktree "):
            path = row.split(" ", 1)[1]
        elif row.startswith("branch ") and path and path != str(REPO).replace("\\", "/"):
            branch = row.rsplit("/", 1)[-1]
            dirty = subprocess.run(
                ["git", "-C", path, "status", "--short"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
            tip = subprocess.run(
                ["git", "-C", path, "log", "-1", "--format=%h %s"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
            flag = "±" if dirty.stdout.strip() else " "
            lines.append(f" {flag}{Path(path).name:<22} {branch[:34]:<34} {tip.stdout.strip()[:60]}")
    return lines or ["  (no lane worktrees)"]


def prs_section() -> list[str]:
    result = subprocess.run(
        ["gh", "pr", "list", "--json", "number,title,autoMergeRequest,statusCheckRollup", "--limit", "10"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, cwd=str(REPO))
    if result.returncode != 0:
        return ["  (gh unavailable)"]
    lines = []
    for pr in json.loads(result.stdout or "[]"):
        checks = pr.get("statusCheckRollup") or []
        failed = sum(1 for c in checks if c.get("conclusion") == "FAILURE")
        pending = sum(1 for c in checks if not c.get("conclusion"))
        state = "RED" if failed else ("..." if pending else "green")
        armed = "armed" if pr.get("autoMergeRequest") else "     "
        lines.append(f"  #{pr['number']:<5} {state:<5} {armed}  {pr['title'][:58]}")
    return lines or ["  (none open)"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--interval", type=int, default=5)
    parser.add_argument("--runs", type=int, default=8)
    parser.add_argument("--no-pr", action="store_true")
    args = parser.parse_args()

    pr_lines, pr_stamp = ["  (first refresh pending)"], 0.0
    while True:
        if not args.no_pr and time.time() - pr_stamp > 60:
            pr_lines, pr_stamp = prs_section(), time.time()
        os.system("cls" if os.name == "nt" else "clear")
        print(f"AER watch  {datetime.now():%H:%M:%S}   (* = not yet terminal, ± = dirty tree; Ctrl-C to stop)")
        print("\n-- dispatch runs (newest first) " + "-" * 40)
        print("\n".join(runs_section(args.runs)))
        print("\n-- lane worktrees " + "-" * 54)
        print("\n".join(worktrees_section()))
        if not args.no_pr:
            print("\n-- open PRs (refreshes each minute) " + "-" * 36)
            print("\n".join(pr_lines))
        try:
            time.sleep(args.interval)
        except KeyboardInterrupt:
            return 0


if __name__ == "__main__":
    raise SystemExit(main())
