"""Reports which tests were non-deterministic across repeated runs of the same suite (#1206).

Reads the .trx files a repeated `dotnet test` run produced -- one directory per pass -- and reports
every test whose outcome was not identical in all of them. That is the measurement this repo has
never had: 49 flake issues, every one found as a red on a PR that did not touch the code, and no
number anywhere saying whether any of the fixes made the suite more deterministic.

What counts as a fault here is *disagreement*, not failure. A test that fails in all N passes is a
broken test and the suite's own red says so; a test that passes in some and fails in others is the
thing this exists to name. A test missing from a pass entirely counts as disagreement too -- a run
that crashed before reaching it is exactly the shape of #984.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ElementTree
from collections import defaultdict
from pathlib import Path

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def outcomes_in_trx(trx_path: Path) -> dict[str, str]:
    """Map test name -> outcome for one pass. Unparseable file yields nothing, loudly upstream."""
    root = ElementTree.parse(trx_path).getroot()
    outcomes: dict[str, str] = {}
    for result in root.iterfind(".//t:UnitTestResult", TRX_NS):
        name = result.get("testName")
        outcome = result.get("outcome")
        if name and outcome:
            outcomes[name] = outcome
    return outcomes


def find_disagreements(passes: list[dict[str, str]]) -> dict[str, dict[str, int]]:
    """Tests whose outcome was not identical across every pass, with a count per outcome.

    A test absent from a pass is recorded as "NotRun" for that pass rather than skipped: a pass that
    died before reaching a test disagrees with one that ran it, and treating absence as "no opinion"
    would silently forgive the crash-mid-suite shape.
    """
    if len(passes) < 2:
        return {}

    every_name: set[str] = set()
    for outcomes in passes:
        every_name.update(outcomes)

    disagreements: dict[str, dict[str, int]] = {}
    for name in sorted(every_name):
        tally: dict[str, int] = defaultdict(int)
        for outcomes in passes:
            tally[outcomes.get(name, "NotRun")] += 1
        if len(tally) > 1:
            disagreements[name] = dict(tally)

    return disagreements


def load_passes(results_root: Path) -> list[tuple[str, dict[str, str]]]:
    """One entry per pass directory holding at least one .trx, sorted by directory name."""
    loaded: list[tuple[str, dict[str, str]]] = []
    for pass_dir in sorted(p for p in results_root.iterdir() if p.is_dir()):
        merged: dict[str, str] = {}
        for trx in sorted(pass_dir.rglob("*.trx")):
            merged.update(outcomes_in_trx(trx))
        if merged:
            loaded.append((pass_dir.name, merged))
    return loaded


def _selftest() -> int:
    """Polarity arms. A watcher that cannot report a flake is a green light with extra steps."""
    failures = []

    # (a) identical outcomes across passes -> silent, including a test that fails every time
    stable = [
        {"A.Test1": "Passed", "A.Test2": "Failed"},
        {"A.Test1": "Passed", "A.Test2": "Failed"},
    ]
    if find_disagreements(stable):
        failures.append("Arm (a) FAIL: consistent outcomes reported as disagreement")

    # (b) a test that passes once and fails once -> fires, with both outcomes counted
    flaky = [
        {"A.Test1": "Passed", "A.Test2": "Passed"},
        {"A.Test1": "Passed", "A.Test2": "Failed"},
    ]
    found = find_disagreements(flaky)
    if list(found) != ["A.Test2"] or found["A.Test2"] != {"Passed": 1, "Failed": 1}:
        failures.append(f"Arm (b) FAIL: flaky test not reported correctly, got {found!r}")

    # (c) a test missing from one pass entirely -> fires as NotRun, not forgiven
    partial = [{"A.Test1": "Passed", "A.Test2": "Passed"}, {"A.Test1": "Passed"}]
    found_c = find_disagreements(partial)
    if list(found_c) != ["A.Test2"] or found_c["A.Test2"].get("NotRun") != 1:
        failures.append(f"Arm (c) FAIL: test absent from a pass not reported, got {found_c!r}")

    # (d) a single pass cannot disagree with anything -> silent rather than falsely confident
    if find_disagreements([{"A.Test1": "Passed"}]):
        failures.append("Arm (d) FAIL: a single pass produced a disagreement")

    if failures:
        print("flake-watch selftest: FAIL -- " + "; ".join(failures), file=sys.stderr)
        return 1
    print("flake-watch selftest: pass (all 4 arms discriminate)")
    return 0


def main(argv: list[str]) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    if "--selftest" in argv:
        return _selftest()

    if len(argv) < 2:
        print("!! usage: summarize.py <results-root> | --selftest", file=sys.stderr)
        return 1

    results_root = Path(argv[1])
    if not results_root.is_dir():
        print(f"!! no results directory at {results_root}", file=sys.stderr)
        return 1

    loaded = load_passes(results_root)
    if len(loaded) < 2:
        # Not "clean": a watch that compared fewer than two passes measured nothing, and reporting
        # that as a pass is how this check would rot into a green light nobody reads.
        print(f"!! found {len(loaded)} pass(es) under {results_root}; need at least 2 to compare", file=sys.stderr)
        return 1

    names = [name for name, _ in loaded]
    passes = [outcomes for _, outcomes in loaded]
    print(f"flake-watch: compared {len(passes)} passes ({', '.join(names)}), "
          f"{len(passes[0])} tests in the first")

    disagreements = find_disagreements(passes)
    if not disagreements:
        print(f" OK every test agreed with itself across all {len(passes)} passes")
        return 0

    print(f" !! {len(disagreements)} non-deterministic test(s):\n", file=sys.stderr)
    for name, tally in disagreements.items():
        breakdown = ", ".join(f"{outcome} x{count}" for outcome, count in sorted(tally.items()))
        print(f"  {name}\n      {breakdown}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
