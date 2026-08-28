"""Mechanized staleness checks over GitHub-hosted facts nothing else re-runs (#636).

WHY THIS EXISTS
---------------
#635's triage and #627's review found four classes of "recorded in place A, should have moved when
B happened, and nothing noticed" by hand, in one afternoon. Hand-finding does not scale -- several
sat wrong for days. `completeness.py`'s STEP 4 already mechanises one variant (a repo DOC citing a
closed issue); this does the two GitHub-side variants #636 found mechanizable, as a **standalone,
unwired instrument** rather than folding into the already-gating `audit-completeness` -- run it, read
what it finds, and decide per-finding, rather than block a PR on backlog debt nobody has triaged yet
(the same posture #620 asks for: a check ships and is read before it ever gates a merge).

    pixi run audit-staleness-ext            (live report -- reads GitHub; findings, never fails)
    pixi run audit-staleness-ext-selftest   (pure, synthetic; THIS is what gates.py wires in)

WHAT THIS CHECKS
----------------
1. An OPEN issue's body cites a now-CLOSED issue with language that reads as "still blocking /
   unresolved" (`STALENESS_PHRASES`, the same list STEP 4 uses, applied here to GitHub issue bodies
   instead of repo docs).
2. A repo doc/script claims a task is "not wired into CI" while `ci.yml` actually
   invokes it directly (`pixi run <task>`).

WHAT THIS DELIBERATELY DOES NOT CHECK
--------------------------------------
- **"An open issue has a merged PR that satisfied it but never closed it"** (#636's own check 1) --
  investigated and DROPPED. Its own motivating example (#474, satisfied by PR #475) is a false
  positive on inspection: #475's body explicitly reads "the remaining transfer is tracked in #474,
  which stays open" -- correct, deliberate partial completion, not an oversight. A keyword-based
  version cannot tell "deliberately left open" from "should have closed" apart from body PROSE,
  which is exactly the fragile, high-noise territory this project's own culture warns a check must
  not enter ("a noisy check gets ignored, which is worse than none"). Left unbuilt; #636 records the
  finding and why.
- **A decision's premise contradicted by a register** (#636's check 5) -- needs understanding both
  sides; explicitly out of scope per #636 itself.
- **A repeated prose number a script computes itself** (#636's check 3) -- sent to #630.
- **Whether a stale citation SHOULD be updated.** Some hits below are a closed issue cited as prior
  art (legitimate, not stale) rather than a live blocker. This prints every hit; a human reads which
  is which -- the same posture STEP 4 already takes over its docs corpus.

Direct-mention only for the CI-wiring check -- it does not walk pixi's `depends-on` graph (a task
`ci.yml` reaches only transitively, e.g. `test-sidecar` via `test`'s `depends-on`, reads as absent
here). Extending to the dependency graph is real work with its own false-positive risk and is not
attempted; see #620.

Standalone by design, not importing from `completeness.py`: this is meant to run and be read on its
own, unwired from the already-gating checker, and importing internals would make this tool's
correctness depend on that file's. `STALENESS_PHRASES`/`ISSUE_RE` below are a deliberate duplicate of
STEP 4's, not a restatement error -- if that list changes there, update both.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

STALENESS_PHRASES = (
    "blocked by", "depends on", "blocking", "not yet", "unresolved", "pending",
    "waiting on", "not done", "not implemented", "still open", "remains open",
    "not fixed", "not addressed", "not resolved", "tbd",
    "not yet probed", "unprobed", "highest-value open", "no issue owns", "todo",
)
ISSUE_RE = re.compile(r"#(\d{2,5})\b")


def read(path: str) -> str:
    p = os.path.join(ROOT, path)
    if not os.path.exists(p):
        return ""
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read()


def stale_issue_citation_faults(issue_bodies: dict, issue_states: dict) -> list:
    """#636 check 2: an open issue's body cites a closed issue with staleness language.

    Pure function of already-fetched data so `_selftest` can feed synthetic fixtures.
    `issue_bodies` maps OPEN issue number -> body text (only open issues are worth checking here: a
    closed issue citing another closed issue is history, not staleness). `issue_states` maps every
    issue number seen -> "OPEN"/"CLOSED".
    """
    findings = []
    for number, body in issue_bodies.items():
        for lineno, text in enumerate((body or "").splitlines(), start=1):
            low = text.lower()
            if not any(re.search(r"\b" + re.escape(w) + r"\b", low) for w in STALENESS_PHRASES):
                continue
            for m in ISSUE_RE.finditer(text):
                cited = int(m.group(1))
                if cited == number:
                    continue
                if issue_states.get(cited) == "CLOSED":
                    findings.append((number, lineno, cited, text.strip()[:100]))
    return findings


def pixi_task_names() -> set[str]:
    """Every task name pixi.toml declares under [tasks] -- the population the CI-wiring check
    cross-references a doc's claim against."""
    in_tasks = False
    names = set()
    for raw in read("pixi.toml").splitlines():
        stripped = raw.strip()
        if stripped == "[tasks]":
            in_tasks = True
            continue
        if in_tasks and stripped.startswith("["):
            break
        if not in_tasks:
            continue
        m = re.match(r"^([a-zA-Z0-9_-]+)\s*=", raw)
        if m:
            names.add(m.group(1))
    return names


CI_WIRING_CLAIM = re.compile(r"\bnot\s+(?:yet\s+)?(?:wired into|run by|in)\s+CI\b", re.IGNORECASE)
CI_CLAIM_SCAN_DIRS = ("docs", "tools")
CI_CLAIM_SCAN_EXCLUDE = ()


def ci_wiring_claim_faults(pixi_tasks: set[str], ci_yml_text: str, files: dict) -> list:
    """#636 check 4: a doc/script claims a task is 'not wired into CI' while `ci.yml` actually
    invokes it directly.

    `files` maps repo-relative path -> text, so `_selftest` can feed a synthetic tree. Only a task
    named on the SAME line as the claim (backticked, or after `pixi run `) counts -- looking further
    afield risks matching an unrelated task named nearby in prose.
    """
    faults = []
    for rel, text in files.items():
        if not (rel.endswith(".md") or rel.endswith(".py")):
            continue
        if any(rel.startswith(x) for x in CI_CLAIM_SCAN_EXCLUDE):
            continue
        for lineno, line_text in enumerate(text.splitlines(), start=1):
            if not CI_WIRING_CLAIM.search(line_text):
                continue
            named = {t for t in pixi_tasks
                      if re.search(rf"pixi run {re.escape(t)}\b", line_text)
                      or re.search(rf"`{re.escape(t)}`", line_text)}
            for task in named:
                if re.search(rf"pixi run {re.escape(task)}\b", ci_yml_text):
                    faults.append((rel, lineno, task, line_text.strip()[:100]))
    return faults


def _repo_files(dirs: tuple) -> dict:
    files = {}
    for base in dirs:
        for dirpath, _, filenames in os.walk(os.path.join(ROOT, base)):
            for fn in filenames:
                if not (fn.endswith(".md") or fn.endswith(".py")):
                    continue
                rel = os.path.relpath(os.path.join(dirpath, fn), ROOT).replace("\\", "/")
                files[rel] = read(rel)
    return files


def _selftest() -> int:
    """Red/green controls for both pure functions above, over synthetic fixtures only (no `gh` call,
    no repo scan) -- this is what gates.py wires in, because unlike the live report below, it can
    never fail on undiscovered backlog debt; it only fails if the matching logic itself breaks."""
    failures = []

    # ISSUE_RE requires 2-5 digits (matching completeness.py's real-world convention), so fixtures
    # use realistic multi-digit numbers rather than 1/2 -- a single-digit fixture silently never
    # matches ISSUE_RE at all, which would make every arm below pass for the wrong reason.
    green = stale_issue_citation_faults({100: "Relates #200, still fresh"}, {100: "OPEN", 200: "OPEN"})
    if green:
        failures.append("CITATION green arm fired: citing another OPEN issue was flagged")
    red = stale_issue_citation_faults({100: "Blocked by #200 until it lands"}, {100: "OPEN", 200: "CLOSED"})
    if not red:
        failures.append("CITATION red arm did not fire: a closed blocker was not caught")
    self_cite = stale_issue_citation_faults({100: "Blocked by #100, obviously wrong"}, {100: "CLOSED"})
    if self_cite:
        failures.append("CITATION self-reference arm fired: an issue citing itself was flagged")

    tasks = {"vendor-verify", "widget-polish"}
    clean_files = {"docs/x.md": "That is `pixi run vendor-verify`, deliberately not wired into CI."}
    if ci_wiring_claim_faults(tasks, "pixi run test\npixi run lint\n", clean_files):
        failures.append("CI-CLAIM green arm fired: a true 'not wired into CI' claim was flagged")
    stale_files = {"docs/y.md": "That is `pixi run widget-polish`, still not wired into CI."}
    if not ci_wiring_claim_faults(tasks, "pixi run test\npixi run widget-polish\n", stale_files):
        failures.append("CI-CLAIM red arm did not fire: a false 'not wired into CI' claim was not caught")

    if failures:
        print("staleness-ext selftest: FAIL -- " + "; ".join(failures), file=sys.stderr)
        return 1
    print("staleness-ext selftest: pass (both checks discriminate)")
    return 0


def _gh(*args, timeout=30):
    gh = shutil.which("gh")
    if gh is None:
        return None
    try:
        out = subprocess.run(["gh", *args], capture_output=True, text=True, cwd=ROOT, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if out.returncode != 0:
        return None
    try:
        return json.loads(out.stdout)
    except ValueError:
        return None


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")

    if "--selftest" in sys.argv:
        return _selftest()

    print(__doc__.splitlines()[0])
    print()

    all_issues = _gh("issue", "list", "--repo", "aer-works/baton", "--state", "all",
                      "--limit", "1000", "--json", "number,state")
    open_issues = _gh("issue", "list", "--repo", "aer-works/baton", "--state", "open",
                       "--limit", "1000", "--json", "number,body")
    if all_issues is None or open_issues is None:
        print("SKIPPED -- `gh` not on PATH, not authenticated, or the call failed. This is a report; "
              "it never fails the run for that.")
        return 0

    issue_states = {i["number"]: i["state"] for i in all_issues}
    issue_bodies = {i["number"]: i.get("body") or "" for i in open_issues}

    print("-- stale issue citations (open issue cites a closed issue as though unresolved) --")
    citation_findings = stale_issue_citation_faults(issue_bodies, issue_states)
    if not citation_findings:
        print("  none found")
    for number, lineno, cited, snippet in citation_findings:
        print(f"  #{number} cites #{cited} (CLOSED): {snippet}")
    print(f"  {len(citation_findings)} finding(s) -- READ, do not auto-fix: some are legitimate "
          "prior-art references, not live blockers. A human decides which, per #636.")

    print()
    print("-- CI-wiring claims contradicted by ci.yml --")
    pixi_tasks = pixi_task_names()
    ci_yml_text = read(".github/workflows/ci.yml")
    files = _repo_files(CI_CLAIM_SCAN_DIRS)
    ci_findings = ci_wiring_claim_faults(pixi_tasks, ci_yml_text, files)
    if not ci_findings:
        print("  none found")
    for rel, lineno, task, snippet in ci_findings:
        print(f"  {rel}:{lineno}  claims `{task}` is not wired into CI, but ci.yml runs it: {snippet}")

    print()
    total = len(citation_findings) + len(ci_findings)
    print(f"staleness-ext: {total} finding(s) across both checks. Reporting only -- unwired, per "
          "#620/#636 (a check ships and is read before it ever gates a merge).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
