"""The docs-budget tripwire (#1397): the tracked-markdown population must not grow past the
allowlist next to this file, and every tracked .md must be ON it.

The point of the spec v2.0 reset was shrinkage -- deleting the retired registers rather than
stubbing them. A count that can silently grow back is not a budget, so this fails closed: a new
markdown file has to be added to `docs-allowlist.txt` in the same change that adds it, which is
the moment a reviewer can ask whether it should exist at all.

    pixi run audit-docsbudget

Exact-list, not fuzzy: `docs-allowlist.txt` names every tracked path, one per line. A file the
allowlist does not name fails; so does a tracked count over the allowlist's own length (an
allowlist edited to list a path twice, or otherwise out of step with what it enumerates).
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ALLOWLIST = Path(__file__).resolve().parent / "docs-allowlist.txt"


def tracked_markdown() -> set[str]:
    out = subprocess.run(["git", "ls-files", "*.md"], capture_output=True, text=True,
                         cwd=ROOT, check=True)
    return {ln.strip().replace("\\", "/") for ln in out.stdout.splitlines() if ln.strip()}


def budget_faults(tracked: set[str], allowed: list[str]) -> list[str]:
    """Pure, so `--selftest` can drive it without a real git tree."""
    faults = [f"not on the allowlist: {p}" for p in sorted(tracked - set(allowed))]
    if len(tracked) > len(allowed):
        faults.append(f"tracked count {len(tracked)} exceeds the allowlist's {len(allowed)} entries")
    return faults


def _selftest() -> int:
    ok = budget_faults({"a.md"}, ["a.md", "b.md"]) == []
    ok &= budget_faults({"a.md", "c.md"}, ["a.md"]) != []
    print("docsbudget: selftest pass" if ok else "docsbudget: selftest FAIL")
    return 0 if ok else 1


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return _selftest()
    allowed = [ln.strip() for ln in ALLOWLIST.read_text(encoding="utf-8").splitlines() if ln.strip()]
    tracked = tracked_markdown()
    faults = budget_faults(tracked, allowed)
    print(f"docsbudget: {len(tracked)} tracked .md file(s) against a {len(allowed)}-entry allowlist")
    if faults:
        print(" !! docs-budget tripwire fired:")
        for f in faults:
            print(f"      {f}")
        return 1
    print(" OK tracked markdown stays within the allowlist")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
