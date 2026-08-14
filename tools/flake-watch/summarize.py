"""Reports which tests were non-deterministic across repeated runs of the same suite (#1206).

Reads the .trx files a repeated `dotnet test` run produced -- one directory per pass -- and reports
every test whose outcome was not identical in all of them. That is the measurement this repo has
never had: 49 flake issues, every one found as a red on a PR that did not touch the code, and no
number anywhere saying whether any of the fixes made the suite more deterministic.

What counts as a fault here is *disagreement*, not failure. A test that fails in all N passes is a
broken test and the suite's own red says so; a test that passes in some and fails in others is the
thing this exists to name. A test missing from a pass is reported too, but separately and in
different words -- see find_disagreements.
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


def find_disagreements(passes: list[dict[str, str]]) -> tuple[dict[str, dict[str, int]], dict[str, dict[str, int]]]:
    """Splits tests that did not behave identically into the two very different reasons why.

    Returns (varied, unstable). **varied** ran in every pass and did not agree with itself — the
    flake this watch exists to find. **unstable** is missing from at least one pass: either a pass
    died before reaching it, or the test's NAME is not the same from run to run, which xunit produces
    whenever theory data contains a clock reading or a GUID.

    They are reported apart because conflating them is how this becomes noise. The first run of this
    watch found 81 of the second kind and none of the first; had they arrived in one undifferentiated
    list headed "non-deterministic tests", the true reading — that nothing flaked, and that 81 cases
    cannot be tracked across runs at all — would have been invisible.
    """
    if len(passes) < 2:
        return {}, {}

    every_name: set[str] = set()
    for outcomes in passes:
        every_name.update(outcomes)

    varied: dict[str, dict[str, int]] = {}
    unstable: dict[str, dict[str, int]] = {}
    for name in sorted(every_name):
        tally: dict[str, int] = defaultdict(int)
        for outcomes in passes:
            tally[outcomes.get(name, "NotRun")] += 1
        if len(tally) == 1:
            continue
        if "NotRun" in tally:
            unstable[name] = dict(tally)
        else:
            varied[name] = dict(tally)

    return varied, unstable


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
    if any(find_disagreements(stable)):
        failures.append("Arm (a) FAIL: consistent outcomes reported as disagreement")

    # (b) a test that passes once and fails once -> fires as a FLAKE, with both outcomes counted
    flaky = [
        {"A.Test1": "Passed", "A.Test2": "Passed"},
        {"A.Test1": "Passed", "A.Test2": "Failed"},
    ]
    varied_b, unstable_b = find_disagreements(flaky)
    if list(varied_b) != ["A.Test2"] or varied_b["A.Test2"] != {"Passed": 1, "Failed": 1} or unstable_b:
        failures.append(f"Arm (b) FAIL: flaky test not reported as varied, got {varied_b!r} / {unstable_b!r}")

    # (c) a test missing from one pass -> fires as UNSTABLE, never as a flake. The two are different
    # findings and the report that conflated them would have buried the first real one.
    partial = [{"A.Test1": "Passed", "A.Test2": "Passed"}, {"A.Test1": "Passed"}]
    varied_c, unstable_c = find_disagreements(partial)
    if varied_c or list(unstable_c) != ["A.Test2"] or unstable_c["A.Test2"].get("NotRun") != 1:
        failures.append(f"Arm (c) FAIL: absent test not reported as unstable, got {varied_c!r} / {unstable_c!r}")

    # (d) a single pass cannot disagree with anything -> silent rather than falsely confident
    if any(find_disagreements([{"A.Test1": "Passed"}])):
        failures.append("Arm (d) FAIL: a single pass produced a disagreement")

    # (e) both kinds at once -> each lands in its own bucket, neither swallowing the other
    both = [
        {"A.Flaky": "Passed", "A.Renamed1": "Passed"},
        {"A.Flaky": "Failed", "A.Renamed2": "Passed"},
    ]
    varied_e, unstable_e = find_disagreements(both)
    if list(varied_e) != ["A.Flaky"] or sorted(unstable_e) != ["A.Renamed1", "A.Renamed2"]:
        failures.append(f"Arm (e) FAIL: mixed findings not split, got {varied_e!r} / {unstable_e!r}")

    if failures:
        print("flake-watch selftest: FAIL -- " + "; ".join(failures), file=sys.stderr)
        return 1
    print("flake-watch selftest: pass (all 5 arms discriminate)")
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

    varied, unstable = find_disagreements(passes)
    if not varied and not unstable:
        print(f" OK every test agreed with itself across all {len(passes)} passes")
        return 0

    if varied:
        print(f" !! {len(varied)} test(s) whose OUTCOME varied — this is a flake:\n", file=sys.stderr)
        for name, tally in varied.items():
            print(f"  {name}\n      {_breakdown(tally)}", file=sys.stderr)

    if unstable:
        print(
            f"\n !! {len(unstable)} test(s) missing from at least one pass. Either a pass died before "
            "reaching them, or their NAME changes between runs — which xunit produces when theory data "
            "contains a clock reading or a GUID, and which makes a test impossible to follow across "
            "runs at all:\n",
            file=sys.stderr)
        for name, tally in unstable.items():
            print(f"  {name}\n      {_breakdown(tally)}", file=sys.stderr)

    return 1


def _breakdown(tally: dict[str, int]) -> str:
    return ", ".join(f"{outcome} x{count}" for outcome, count in sorted(tally.items()))


if __name__ == "__main__":
    sys.exit(main(sys.argv))

