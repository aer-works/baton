"""Audit UI markup and screens for equal-weight permission controls (0028 §2, #1124).

Scans `src/**/*.axaml` for permissive-primary pairings: a control whose label/content matches a
permissive word (Allow, Apply, Overwrite, Grant) carrying a primary visual marker
(`Classes="accent"`) while a sibling deny/cancel control (Deny, Cancel) in the same file is
un-primaried.

What it CAN see:
- File-local pairings of primary permissive controls alongside non-primary deny/cancel controls.
- AXAML `Classes="accent"` (list attribute) and `Classes.accent=` (attached-property conditional
  class) on controls whose permissive label sits on the same line OR on a following line inside
  the same element (the repo's own AccessText mnemonic idiom, #1124 review finding A) — the
  lookahead is bounded at the element's close.

What it CANNOT see:
- Pairings split across multiple files or dynamic templates.
- Dynamic control styles applied exclusively in code-behind logic.
- Custom wrapped button components that don't use standard AXAML button names/classes.

Known coarseness, disclosed rather than solved (#1124 review finding C): the deny half is
file-scoped, not DOM-proximity-scoped — a legitimate accent permissive control sharing a FILE with
an unrelated bare Cancel would flag. That is a deliberate narrowness/complexity trade: keep gate
markup self-contained per view (which the current tree does), and a false fire here is loud and
cheap to triage, where the inverse (a DOM parser wrong in silence) is not.
"""
from __future__ import annotations

import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

PERMISSIVE_PAT = r"\b(Allow|Apply|Overwrite|Grant)\b"
DENY_PAT = r"\b(Deny|Cancel)\b"

PERMISSIVE_RE = re.compile(PERMISSIVE_PAT, re.IGNORECASE)
DENY_RE = re.compile(DENY_PAT, re.IGNORECASE)

AXAML_ACCENT_RE = re.compile(r'Classes="[^"]*\baccent\b[^"]*"|Classes\.accent\s*=')


def scan_axaml_text(text: str, rel_path: str) -> list[tuple[str, int, str, str]]:
    """Scan AXAML content for permissive-primary controls paired with un-primaried deny controls."""
    lines = text.splitlines()
    has_unprimaried_deny = False
    for line in lines:
        if DENY_RE.search(line) and not AXAML_ACCENT_RE.search(line):
            has_unprimaried_deny = True
            break

    if not has_unprimaried_deny:
        return []

    violations = []
    for idx, line in enumerate(lines, 1):
        if not AXAML_ACCENT_RE.search(line):
            continue

        # #1124 review finding A: the permissive label may sit on a LATER line of the same
        # element — the repo's own AccessText mnemonic idiom puts it in a child element. Scan
        # from the accent line to the element's close (bounded, so an unrelated later control
        # cannot leak in).
        element_lines = [line]
        for follow in lines[idx:]:
            element_lines.append(follow)
            if "/>" in follow or "</Button>" in follow or "<Button" in follow:
                break
            if len(element_lines) > 12:
                break
        # AccessText mnemonics put the underscore INSIDE the word ("A_llow", "A_pprove") — strip
        # it before matching or the word-boundary regex misses the repo's own labelling idiom.
        element_text = "\n".join(element_lines).replace("_", "")

        if PERMISSIVE_RE.search(element_text):
            violations.append((rel_path, idx, "permissive-primary-axaml", line.strip()))

    return violations


def scan_tree(root_dir: Path) -> tuple[int, list[tuple[str, int, str, str]]]:
    """Scan the AXAML population under root_dir.

    Returns (total_files_scanned, list_of_violations).
    Each violation is (relative_path, line_number, rule, line_content).
    """
    total_files = 0
    violations = []

    # Population 1: src/**/*.axaml
    src_dir = root_dir / "src"
    if src_dir.exists():
        for path in src_dir.rglob("*.axaml"):
            total_files += 1
            rel_path = path.relative_to(root_dir).as_posix()
            text = path.read_text(encoding="utf-8", errors="ignore")
            file_violations = scan_axaml_text(text, rel_path)
            violations.extend(file_violations)

    return total_files, violations


def main() -> int:
    total_files, violations = scan_tree(ROOT)
    if violations:
        print(f"FAILED: Found {len(violations)} permissive-primary pairing violation(s) across {total_files} files examined:")
        for rel_path, line_num, term, content in violations:
            print(f"  !! {rel_path}:{line_num} [{term}]: {content}")
        return 1

    print(f"OK: No permissive-primary pairing violations found across {total_files} files examined.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
