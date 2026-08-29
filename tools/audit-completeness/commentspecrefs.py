"""Fail on spec-section references in code comments that cannot resolve.

#1430: an audit of every `§` in src/tests comments found 538 occurrences of which exactly 2
correctly cited the live spec — the rest pointed at retired spec generations whose numbering
collides with today's `spec/baton.md`, every one phrased present-tense as live authority. The
ruling: a comment must not reference a spec file or section that no longer exists, even as
history. Prose saying so enforces nothing on its own — this is the check that does.

What this can verify is resolution, not topic-truth: a `§` in a .cs file is valid only when its
line names the document it cites and that citation resolves —
  * `spec/baton.md §N` where `§N` is a real heading there, or
  * a `docs/<name>.md §N` where that file exists in the tree.
Whether a resolving citation is ABOUT the right thing needed the one-time #1430 sweep; this keeps
the syntactic floor from regressing, and a renumbering of baton.md fails every citation it broke.

Temporary carve-out: `decision NNNN §N` refs are allowed while the fate of the deleted decision
records is decided (they were removed from the tree with rationale still pointing at them) — the
carve-out is tracked by the issue named in DECISION_CARVEOUT_TRACKING and dies with it.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

SEARCH_ROOTS = ["src", "tests"]

SPEC = ROOT / "spec" / "baton.md"

DECISION_CARVEOUT_TRACKING = "#1431"

SECTION_RE = re.compile(r"§(\d+)(?:\.\d+)?")
HEADING_RE = re.compile(r"^#+\s+§(\d+)\b", re.MULTILINE)
# Decision records are cited both worded ("decision 0047 §4") and bare ("0047 §4"); the ids are
# zero-padded four digits, which is what keeps the bare form matchable without false positives.
DECISION_RE = re.compile(r"(?:decision\s+\d{3,4}|\b0\d{3})\b", re.IGNORECASE)
DOCS_PATH_RE = re.compile(r"docs/[\w./-]+\.md")
LIVE_SPEC_MARKER = "spec/baton.md"


def _live_sections() -> set[str]:
    return set(HEADING_RE.findall(SPEC.read_text(encoding="utf-8")))


def _line_problems(line: str, sections: set[str]) -> list[str]:
    cited = SECTION_RE.findall(line)
    if not cited:
        return []
    if DECISION_RE.search(line):
        return []
    docs_hit = DOCS_PATH_RE.search(line)
    if docs_hit:
        if (ROOT / docs_hit.group(0)).is_file():
            return []
        return [f"cites {docs_hit.group(0)}, which does not exist in the tree"]
    if LIVE_SPEC_MARKER not in line:
        return [f"§{n} names no document — a section number alone cannot resolve" for n in cited]
    return [
        f"§{n} is not a heading in {LIVE_SPEC_MARKER}"
        for n in cited
        if n not in sections
    ]


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return _selftest()

    sections = _live_sections()
    problems: list[str] = []
    scanned = 0

    for root_name in SEARCH_ROOTS:
        root = ROOT / root_name
        if not root.is_dir():
            continue
        for path in root.rglob("*.cs"):
            if any(part in {"bin", "obj"} for part in path.parts):
                continue
            try:
                text = path.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            scanned += 1
            for lineno, line in enumerate(text.splitlines(), start=1):
                for why in _line_problems(line, sections):
                    problems.append(f"{path.relative_to(ROOT)}:{lineno}: {why}")

    print(f"commentspecrefs: {scanned} file(s) scanned against {len(sections)} live spec section(s)")
    if problems:
        print(f" !! {len(problems)} problem(s):")
        for p in problems:
            print(f"  {p}")
        return 1
    print(" OK every § in a comment resolves to a live document")
    return 0


def _selftest() -> int:
    """Prove the rule still catches what it exists for, and spares what it must ignore."""
    sections = {"1", "5", "9"}
    failures = []

    for sample, reason in (
        ("// per §17.2 a reject resolves the item", "bare dead section"),
        ("// see §5.1 for the torn-line rule", "bare section, no document named"),
        ("// spec/baton.md §12 rules this out", "names the live spec but a dead section"),
        ("// docs/no-such-file.md §3 explains this", "names a docs file that does not exist"),
    ):
        if not _line_problems(sample, sections):
            failures.append(f"missed: {reason}: {sample!r}")

    for sample, reason in (
        ("// spec/baton.md §5: the gate is closed exactly one way", "correct live citation"),
        ("// decision 0047 §4 chose this shape", "decision carve-out, worded"),
        ("// the 0054 §1/§6 participant rule", "decision carve-out, bare id"),
        ("// no section reference here at all", "no § on the line"),
        ("// docs/vendor-doc-audit.md §5 catalogues the probes", "existing docs file"),
    ):
        if _line_problems(sample, sections):
            failures.append(f"false positive: {reason}: {sample!r}")

    if failures:
        print(" !! commentspecrefs selftest FAILED:")
        for f in failures:
            print(f"  {f}")
        return 1
    print("commentspecrefs: selftest OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
