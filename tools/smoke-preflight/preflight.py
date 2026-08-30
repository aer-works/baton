"""Validate a live smoke run's ASSUMPTIONS before it is allowed to spend anything.

WHY THIS EXISTS
---------------
`gemini-3-flash` sat in a binding fixture, two dialogue participants and two runbooks for months.
It is not a model `agy` has. It pinned **nothing** -- and nothing looked wrong, because the two
vendors disagree about what an unknown `--model` means:

    claude   rejects it, `is_error: true`      -> a stale pin fails loudly, and self-reports
    agy      accepts it and runs its default   -> a stale pin is INVISIBLE

So two smoke tests had been running on whatever agy defaulted to, while three files read as though a
cheap model was pinned. That is worse than no pin: agy's own catalogue includes
`claude-opus-4-6-thinking`, so a default drifting upward would spend real money with the repo still
looking correct.

The general rule this enforces: **an assumption a live run depends on gets checked before the run
is allowed to cost anything.** Every check here is free -- it reads files and queries catalogues,
and starts no billable session -- so there is no reason not to run it every time.

Wired as a `depends-on` of every `smoke-*` task in pixi.toml. A non-zero exit stops the smoke test
before it spawns a vendor.

THE ASYMMETRY THAT SHAPES THIS
------------------------------
This gate is **load-bearing for agy and belt-and-braces for claude**, and that is not a tidiness
detail:

  * `agy models` prints an exact catalogue, so an agy pin can be checked precisely -- and MUST be,
    because agy will not complain at run time.
  * `claude` has no catalogue command at all. `claude models` is not a subcommand: the words are
    taken as a PROMPT and answered, which spends usage. So claude pins are checked by shape only,
    and the real safety net there is claude's own fail-closed behaviour.

Usage:
    pixi run smoke-preflight
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SMOKE = os.path.join(ROOT, "tests", "Aer.Cli.SmokeTests")

# Aliases claude documents in `--help`. `haiku` is not in its example list but resolves: verified via
# `modelUsage`, which reported claude-haiku-4-5-20251001 rather than a fallback. Checking the
# RESOLVED model rather than trusting the flag is the only way to know an alias is not silently
# reinterpreted.
CLAUDE_ALIASES = {"opus", "sonnet", "haiku", "fable"}
CLAUDE_FULL = re.compile(r"^claude-[a-z0-9.\-]+$")

OK, BAD, WARN = "ok", "FAIL", "warn"


def run(cmd, timeout=90):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        return p.returncode, p.stdout or "", p.stderr or ""
    except Exception as exc:                                                   # noqa: BLE001
        return 127, "", repr(exc)


def agy_catalogue():
    """The exact set agy will accept. Empty means agy is missing, which is its own finding.

    Each line is `id<TAB>display name` (re-measured 2026-08-30, #1422; recorded in
    docs/vendor-capabilities.md's `agy models` section); keep only the id -- matching on the whole
    line made every agy pin FAIL once the description column appeared, which read as every pin
    having gone stale when none had. Tab-strip then whitespace-split, so the pre-2026-08
    multi-column grid format (no tabs, several ids per line) would also parse -- same shape as
    tools/vendor-verify/verify.py's models.agy-value-set check.
    """
    rc, out, _ = run(["agy", "models"])
    if rc != 0:
        return None
    return {tok for line in out.splitlines() for tok in line.split("\t", 1)[0].split()}


def pins():
    """Every (source, adapter, model) a live smoke run would use.

    JSON fixtures are PARSED, so adapter and model are genuinely paired. The C# participants are
    scanned TEXTUALLY -- declared as a limitation rather than presented as equivalent, because a
    regex over source can miss a pin built at runtime.
    """
    found = []
    fixtures = os.path.join(SMOKE, "Fixtures")
    if os.path.isdir(fixtures):
        for name in sorted(os.listdir(fixtures)):
            if not name.endswith(".json"):
                continue
            path = os.path.join(fixtures, name)
            try:
                doc = json.load(open(path, encoding="utf-8"))
            except ValueError:
                continue
            if not isinstance(doc, dict):
                continue
            for worker, entry in doc.items():
                if isinstance(entry, dict) and entry.get("Model"):
                    found.append((f"{name}::{worker}", entry.get("Adapter"), entry["Model"]))

    for name in sorted(os.listdir(SMOKE)):
        if not name.endswith(".cs"):
            continue
        text = open(os.path.join(SMOKE, name), encoding="utf-8", errors="replace").read()
        for m in re.finditer(r'"--model"\s*,\s*"([^"]+)"', text):
            found.append((f"{name} (textual scan)", None, m.group(1)))
    return found


def classify(model, adapter, catalogue):
    """Which vendor will accept this name, and can we actually tell?"""
    in_agy = catalogue is not None and model in catalogue
    claude_shaped = model in CLAUDE_ALIASES or bool(CLAUDE_FULL.match(model))
    if in_agy:
        return OK, "in `agy models`"
    if claude_shaped:
        if adapter in ("gemini", "agy"):
            return BAD, ("claude-shaped name bound to the agy adapter, and NOT in `agy models` -- "
                         "agy would accept it silently and run its own default")
        return OK, "claude alias or full id (claude rejects unknown names itself, so a stale pin here fails loudly)"
    if catalogue is None:
        return WARN, "cannot verify: `agy models` did not run, so the agy catalogue is unknown"
    return BAD, "in neither `agy models` nor claude's alias/id shape -- this pin enforces nothing"


def main() -> int:
    print("smoke preflight -- validating what a live run assumes. Free: no billable session.\n")
    failures, warnings = [], []

    catalogue = agy_catalogue()
    if catalogue is None:
        warnings.append("`agy models` did not run; agy pins cannot be checked this run")
        print("  agy catalogue                 UNAVAILABLE")
    else:
        print(f"  agy catalogue                 {len(catalogue)} model(s)")

    rc, out, _ = run(["claude", "auth", "status"])
    try:
        logged_in = bool((json.loads(out or "{}")).get("loggedIn"))
    except ValueError:
        logged_in = False
    print(f"  claude authenticated          {logged_in}")
    if not logged_in:
        failures.append("claude is not logged in; a live smoke run would fail on auth, not on logic")

    print("\n  model pins")
    rows = pins()
    if not rows:
        failures.append("no model pins found at all -- the scan is broken, or a smoke test is "
                        "running on vendor defaults with nothing pinned")
    for source, adapter, model in rows:
        status, why = classify(model, adapter, catalogue)
        print(f"    {status:<5} {model:<32} {source}")
        if status != OK:
            print(f"          {why}")
        if status == BAD:
            failures.append(f"{source}: {model} -- {why}")
        elif status == WARN:
            warnings.append(f"{source}: {model} -- {why}")

    print("\n  WHAT THIS CANNOT CHECK")
    print("    - Whether agy is authenticated. It has no `auth` subcommand and no structured")
    print("      readiness probe, so unlike claude there is nothing free to ask. An agy smoke test")
    print("      can still fail on auth after this gate passes.")
    print("    - Whether a pin RESOLVES to the model named. This checks the name is acceptable,")
    print("      not what actually ran; only `modelUsage` in a real result proves that.")
    print("    - Pins built at runtime, or in any file this scan does not read. The C# scan is")
    print("      textual and finds `\"--model\", \"X\"` literals only.")
    print("    - That the model is CHEAP. It checks validity, not price -- `claude-opus-4-6-thinking`")
    print("      is a perfectly valid agy model.")

    print()
    for w in warnings:
        print(f"  warn  {w}")
    for f in failures:
        print(f"  FAIL  {f}")
    if failures:
        print(f"\npreflight FAILED ({len(failures)}). The smoke run is stopped before it spends "
              "anything.")
        return 1
    print("preflight passed." + (f" {len(warnings)} warning(s)." if warnings else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
