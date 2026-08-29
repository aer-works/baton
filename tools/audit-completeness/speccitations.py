"""Fail on line-number citations in the spec.

#1428: a `file.cs:123` citation is a claim about where something sits in a file, and every edit to
that file silently invalidates it — the finale audit found 56 of them in `spec/baton.md`, most
already pointing at the wrong lines. The owner's ruling: the spec cites file + symbol, never a line
number. Prose saying so enforces nothing on its own — this is the check that does, per the
project's rule that anything which must not regress needs one that runs and fails.

The discriminator is a file extension immediately before the `:N`, which is what keeps this lexical
with nothing to triage: clock times (`18:45`), URLs with ports, and issue refs carry no extension,
and a bare `path/file.cs` citation without a line number is exactly what the rule asks for.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

SPEC_FILES = [ROOT / "spec" / "baton.md"]

# An extension-bearing filename followed by :N (optionally a range or a comma list of them).
CITATION_RE = re.compile(
    r"\.(?:cs|csproj|py|md|json|jsonl|toml|yml|yaml|dart|axaml|ts|js|sh|ps1|rs|slnx|props|targets|xml)"
    r":\d+(?:-\d+)?(?:,\s*\d+(?:-\d+)?)*"
)


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return _selftest()

    problems: list[str] = []
    scanned = 0

    for path in SPEC_FILES:
        if not path.is_file():
            problems.append(f"{path.relative_to(ROOT)}: missing — this check's target moved without it")
            continue
        scanned += 1
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            for match in CITATION_RE.finditer(line):
                problems.append(
                    f"{path.relative_to(ROOT)}:{lineno}: line-number citation '…{match.group(0)}' — "
                    f"cite file + symbol instead (#1428: line numbers go stale on every edit)")

    print(f"speccitations: {scanned} spec file(s) scanned")
    if problems:
        print(f" !! {len(problems)} problem(s):")
        for p in problems:
            print(f"  {p}")
        return 1
    print(" OK no line-number citation in the spec")
    return 0


def _selftest() -> int:
    """Prove the pattern still catches what it exists for, and spares what it must ignore."""
    failures = []

    for sample in (
        "see Program.cs:123",
        "RoomEvent.cs:93-95",
        "verify.py:12,40-44",
        "ci.yml:7",
        "ffi.rs:120",
        "Directory.Build.props:6",
    ):
        if not CITATION_RE.search(sample):
            failures.append(f"missed a citation it exists to catch: {sample!r}")

    for sample in (
        "the 18:45 executor",
        "http://localhost:5173/rooms",
        "see Program.cs and the registry",
        "issue #1428: ratio 3:1",
    ):
        if CITATION_RE.search(sample):
            failures.append(f"false positive on legitimate text: {sample!r}")

    if failures:
        print(" !! speccitations selftest FAILED:")
        for f in failures:
            print(f"  {f}")
        return 1
    print("speccitations: selftest OK")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
